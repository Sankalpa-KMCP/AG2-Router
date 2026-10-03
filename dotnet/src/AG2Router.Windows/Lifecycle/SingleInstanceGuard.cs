using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace AG2Router.Windows.Lifecycle;

/// <summary>
/// The paired endpoints used by a single-instance owner and its command clients.
/// Tests supply unique endpoints instead of opening the production namespace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Isolation scope:</b> Both production endpoints are scoped to one Windows session.
/// The mutex uses the kernel-managed <c>Local\</c> namespace, and the pipe name is qualified
/// with the terminal-services session id so that independent Windows sessions never contend
/// for one machine-global pipe. <see cref="Production"/> evaluates the session id once per
/// process, so every AG2 Router process in the same session reconstructs the same name.
/// </para>
/// </remarks>
public sealed class SingleInstanceIpcNamespace
{
    private const string ProductionMutexName = @"Local\AG2Router_Session_Mutex";
    private const string ProductionPipePrefix = "AG2Router_Session_IPC_Pipe_";

    public static SingleInstanceIpcNamespace Production { get; } =
        ForSession(Process.GetCurrentProcess().SessionId);

    /// <summary>
    /// Builds the production endpoint pair for a session discriminator. The mutex name is
    /// invariant because the <c>Local\</c> prefix already scopes it per session; the pipe name
    /// carries the discriminator explicitly because the named-pipe namespace is machine-global.
    /// </summary>
    public static SingleInstanceIpcNamespace ForSession(int sessionId) => new(
        ProductionMutexName, $"{ProductionPipePrefix}{sessionId}");

    public string MutexName { get; }
    public string PipeName { get; }

    public SingleInstanceIpcNamespace(string mutexName, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        MutexName = mutexName;
        PipeName = pipeName;
    }
}

/// <summary>
/// Manages per-user session single-instance execution using a session-scoped Mutex and Named Pipe.
/// When a duplicate instance is launched in the same user session, it signals the primary instance
/// to activate its window and terminates cleanly.
/// </summary>
public class SingleInstanceGuard : IAsyncDisposable
{
    /// <summary>
    /// Server-side pipe options for the IPC listener. <see cref="PipeOptions.CurrentUserOnly"/>
    /// restricts connections to the owning Windows user; this is structural configuration,
    /// asserted by tests, while true cross-user rejection requires a second OS account to
    /// verify dynamically.
    /// </summary>
    internal static PipeOptions ServerPipeOptions { get; } =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    /// <summary>Legitimate clients write one command line immediately after connecting.</summary>
    private static readonly TimeSpan CommandReadTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ListenerRestartDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ConnectionDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly SingleInstanceIpcNamespace _ipcNamespace;
    private readonly ConcurrentDictionary<Task, byte> _connectionTasks = new();

    private Mutex? _mutex;
    private bool _isPrimaryInstance;
    private CancellationTokenSource? _pipeCts;
    private Task? _listenerTask;

    public SingleInstanceGuard(SingleInstanceIpcNamespace? ipcNamespace = null)
    {
        _ipcNamespace = ipcNamespace ?? SingleInstanceIpcNamespace.Production;
    }

    /// <summary>
    /// Attempt to acquire the session-scoped mutex.
    /// If primary, starts a background pipe listener and waits deterministically for its IPC endpoint
    /// to be ready before returning true.
    /// If secondary, sends an activation signal to the primary instance and returns false.
    /// </summary>
    public bool TryAcquire(Action onActivateRequested, Action? onCloseRequested = null, Action? onExitRequested = null)
    {
        _mutex = new Mutex(true, _ipcNamespace.MutexName, out _isPrimaryInstance);

        if (!_isPrimaryInstance)
        {
            // Secondary instance in the current user session: signal primary to foreground and exit
            SendActivationSignal();
            return false;
        }

        // Primary instance: listen for duplicate instance launches
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartPipeListener(onActivateRequested, onCloseRequested, onExitRequested, readyTcs);

        try
        {
            // Bounded deterministic wait for the pipe server to instantiate and begin listening
            if (!readyTcs.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                CleanupFailedAcquisition();
                return false;
            }
        }
        catch (Exception)
        {
            CleanupFailedAcquisition();
            return false;
        }

        return true;
    }

    private void CleanupFailedAcquisition()
    {
        if (_pipeCts != null)
        {
            try { _pipeCts.Cancel(); } catch { }
            _pipeCts.Dispose();
            _pipeCts = null;
        }
        if (_mutex != null)
        {
            if (_isPrimaryInstance)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
            _mutex.Dispose();
            _mutex = null;
        }
        _isPrimaryInstance = false;
    }

    /// <summary>
    /// Sends a command to the primary running instance over the session named pipe.
    /// </summary>
    public static bool SendCommand(string command, int timeoutMs = 3000)
        => SendCommand(SingleInstanceIpcNamespace.Production, command, timeoutMs);

    public static bool SendCommand(SingleInstanceIpcNamespace ipcNamespace, string command, int timeoutMs = 3000)
    {
        ArgumentNullException.ThrowIfNull(ipcNamespace);
        try
        {
            using var client = new NamedPipeClientStream(".", ipcNamespace.PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client);
            writer.WriteLine(command);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends an EXIT command to the primary running instance and waits boundedly
    /// for the primary instance to release its session mutex and terminate.
    /// Returns true if the primary instance has successfully exited or if no primary
    /// instance was running; returns false if the primary instance timed out.
    /// </summary>
    public static bool RequestExitAndWait(int timeoutMs = 5000)
        => RequestExitAndWait(SingleInstanceIpcNamespace.Production, timeoutMs);

    public static bool RequestExitAndWait(SingleInstanceIpcNamespace ipcNamespace, int timeoutMs = 5000)
    {
        ArgumentNullException.ThrowIfNull(ipcNamespace);
        // 1. Send the EXIT command over the session IPC pipe
        bool sent = SendCommand(ipcNamespace, "EXIT", timeoutMs: Math.Min(timeoutMs, 2000));
        if (!sent)
        {
            // No primary instance was listening. Check if mutex exists.
            if (!Mutex.TryOpenExisting(ipcNamespace.MutexName, out var existingMutex))
            {
                // No mutex and no pipe -> primary instance is definitely not running
                return true;
            }
            existingMutex.Dispose();
        }

        // 2. Wait boundedly for the primary instance to release/abandon the mutex
        return WaitForPrimaryExit(ipcNamespace, timeoutMs);
    }

    /// <summary>
    /// Waits up to timeoutMs for the primary instance's session mutex to be released or abandoned.
    /// Returns true if the primary instance exited or was not running; false if timed out.
    /// </summary>
    public static bool WaitForPrimaryExit(int timeoutMs = 5000)
        => WaitForPrimaryExit(SingleInstanceIpcNamespace.Production, timeoutMs);

    public static bool WaitForPrimaryExit(SingleInstanceIpcNamespace ipcNamespace, int timeoutMs = 5000)
    {
        ArgumentNullException.ThrowIfNull(ipcNamespace);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (!Mutex.TryOpenExisting(ipcNamespace.MutexName, out var mutex))
                {
                    // Mutex no longer exists: primary instance has disposed it and exited
                    return true;
                }

                using (mutex)
                {
                    int remainingMs = Math.Max(10, timeoutMs - (int)sw.ElapsedMilliseconds);
                    if (mutex.WaitOne(remainingMs))
                    {
                        try { mutex.ReleaseMutex(); } catch { }
                        return true;
                    }
                }
            }
            catch (AbandonedMutexException)
            {
                // Primary process terminated and abandoned the mutex
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Mutex ceased to exist
                return true;
            }
            catch
            {
                // Transient exception during teardown
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private void SendActivationSignal()
    {
        SendCommand(_ipcNamespace, "ACTIVATE");
    }

    private void StartPipeListener(
        Action onActivateRequested,
        Action? onCloseRequested,
        Action? onExitRequested,
        TaskCompletionSource<bool> readyTcs)
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        _listenerTask = Task.Run(async () =>
        {
            bool isFirstLoop = true;
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _ipcNamespace.PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        ServerPipeOptions
                    );

                    var waitTask = server.WaitForConnectionAsync(token);

                    if (isFirstLoop)
                    {
                        isFirstLoop = false;
                        readyTcs.TrySetResult(true);
                    }

                    await waitTask.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    // Connection ownership moves to an independent handler so one stalled
                    // client can never occupy the accept path; the loop immediately creates
                    // the next server instance and stays available for later clients.
                    var connection = server;
                    server = null;
                    TrackConnection(HandleConnectionAsync(
                        connection, token, onActivateRequested, onCloseRequested, onExitRequested));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (isFirstLoop)
                    {
                        isFirstLoop = false;
                        readyTcs.TrySetException(ex);
                        break;
                    }

                    // Delay before re-listening to avoid tight loops on unexpected transient error
                    try
                    {
                        await Task.Delay(ListenerRestartDelay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                finally
                {
                    if (server != null)
                    {
                        try { await server.DisposeAsync().ConfigureAwait(false); } catch { }
                    }
                }
            }
        }, token);
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream connection,
        CancellationToken shutdownToken,
        Action onActivateRequested,
        Action? onCloseRequested,
        Action? onExitRequested)
    {
        try
        {
            // A client that connects but never completes a command must release its handler:
            // this read is bounded independently of the accept loop and of other clients.
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            readCts.CancelAfter(CommandReadTimeout);
            using var reader = new StreamReader(connection);
            var message = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            switch (message)
            {
                case "ACTIVATE":
                    _ = Task.Run(() => { try { onActivateRequested(); } catch { } });
                    break;
                case "CLOSE":
                    if (onCloseRequested != null)
                        _ = Task.Run(() => { try { onCloseRequested(); } catch { } });
                    break;
                case "EXIT":
                    if (onExitRequested != null)
                        _ = Task.Run(() => { try { onExitRequested(); } catch { } });
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Stalled command read timed out, or the instance is shutting down.
        }
        catch
        {
            // Malformed or failed clients must never terminate the listener.
        }
        finally
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private void TrackConnection(Task handler)
    {
        _connectionTasks[handler] = 0;
        _ = handler.ContinueWith(
            static (completed, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(completed, out _),
            _connectionTasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipeCts != null)
        {
            await _pipeCts.CancelAsync();
            if (_listenerTask != null)
            {
                try
                {
                    await _listenerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { }
                _listenerTask = null;
            }

            if (!_connectionTasks.IsEmpty)
            {
                // Connected handlers exit promptly through their shutdown-linked read token;
                // the bounded drain only guards against pathological waits.
                try
                {
                    await Task.WhenAll(_connectionTasks.Keys.ToArray())
                        .WaitAsync(ConnectionDrainTimeout).ConfigureAwait(false);
                }
                catch { }
            }
            _connectionTasks.Clear();
            _pipeCts.Dispose();
            _pipeCts = null;
        }

        if (_mutex != null)
        {
            if (_isPrimaryInstance)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch
                {
                    // Ignored during process teardown
                }
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }
}
