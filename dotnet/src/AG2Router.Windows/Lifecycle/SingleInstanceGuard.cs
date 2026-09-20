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

    /// <summary>
    /// Attempt to acquire the session-scoped mutex.
    /// If primary, starts a background pipe listener to receive activation signals.
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
        StartPipeListener(onActivateRequested, onCloseRequested, onExitRequested);
        return true;
    }

    /// <summary>
    /// Sends a command to the primary running instance over the session named pipe.
    /// </summary>
    public static bool SendCommand(string command, int timeoutMs = 1000)
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

    private void StartPipeListener(Action onActivateRequested, Action? onCloseRequested, Action? onExitRequested)
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        Task.Run(async () =>
        {
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

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync(token);
                    switch (message)
                    {
                        case "ACTIVATE":
                            onActivateRequested();
                            break;
                        case "CLOSE":
                            onCloseRequested?.Invoke();
                            break;
                        case "EXIT":
                            onExitRequested?.Invoke();
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Delay before re-listening to avoid tight loops on error
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
