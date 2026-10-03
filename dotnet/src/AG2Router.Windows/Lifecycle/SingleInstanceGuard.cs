using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace AG2Router.Windows.Lifecycle;

/// <summary>
/// The paired endpoints used by a single-instance owner and its command clients.
/// Tests supply unique endpoints instead of opening the production namespace.
/// </summary>
public sealed class SingleInstanceIpcNamespace
{
    public static SingleInstanceIpcNamespace Production { get; } = new(
        @"Local\AG2Router_Session_Mutex", "AG2Router_Session_IPC_Pipe");

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
    private readonly SingleInstanceIpcNamespace _ipcNamespace;

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
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _ipcNamespace.PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    );

                    var waitTask = server.WaitForConnectionAsync(token);

                    if (isFirstLoop)
                    {
                        isFirstLoop = false;
                        readyTcs.TrySetResult(true);
                    }

                    await waitTask.ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync(token).ConfigureAwait(false);
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
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
        }, token);
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
