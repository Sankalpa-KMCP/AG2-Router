using System.IO;
using System.IO.Pipes;

namespace AG2Router.Windows.Lifecycle;

/// <summary>
/// Manages per-user session single-instance execution using a session-scoped Mutex and Named Pipe.
/// When a duplicate instance is launched in the same user session, it signals the primary instance
/// to activate its window and terminates cleanly.
/// </summary>
public class SingleInstanceGuard : IAsyncDisposable
{
    private const string MutexName = @"Local\AG2Router_Session_Mutex";
    private const string PipeName = "AG2Router_Session_IPC_Pipe";

    private Mutex? _mutex;
    private bool _isPrimaryInstance;
    private CancellationTokenSource? _pipeCts;
    private Task? _listenerTask;

    /// <summary>
    /// Attempt to acquire the session-scoped mutex.
    /// If primary, starts a background pipe listener and waits deterministically for its IPC endpoint
    /// to be ready before returning true.
    /// If secondary, sends an activation signal to the primary instance and returns false.
    /// </summary>
    public bool TryAcquire(Action onActivateRequested, Action? onCloseRequested = null, Action? onExitRequested = null)
    {
        _mutex = new Mutex(true, MutexName, out _isPrimaryInstance);

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
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
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

    private static void SendActivationSignal()
    {
        SendCommand("ACTIVATE");
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
                        PipeName,
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
