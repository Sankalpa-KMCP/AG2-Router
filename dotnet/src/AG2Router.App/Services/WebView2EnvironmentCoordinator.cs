using System.IO;
using System.Runtime.InteropServices;
using AG2Router.App.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace AG2Router.App.Services;

public static class WebViewAttemptDeadline
{
    public static async Task RunAsync(Func<Task> operation, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operation().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Generic coordinator core managing the lifecycle, concurrency, and process exit synchronization
/// of WebView2 environments. Fully decoupled from native COM runtimes for unit testability.
/// </summary>
public class WebView2EnvironmentCoordinatorCore<TEnv> where TEnv : class
{
    private readonly Func<CancellationToken, Task<TEnv>> _environmentFactory;
    private readonly Action<TEnv, Action> _subscribeProcessExited;
    private readonly Action<TEnv, Action>? _unsubscribeProcessExited;
    private readonly Func<Exception, bool> _isLockContentionException;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayFunc;
    private readonly Action<string, Exception?>? _logger;
    private readonly TimeSpan _exitWaitTimeout;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _creationTimeout;

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _stateLock = new();

    private TEnv? _currentEnvironment;
    private TaskCompletionSource<bool>? _currentExitTcs;
    private uint? _activeBrowserPid;
    private long _generation;

    public WebView2EnvironmentCoordinatorCore(
        Func<CancellationToken, Task<TEnv>> environmentFactory,
        Action<TEnv, Action> subscribeProcessExited,
        Action<TEnv, Action>? unsubscribeProcessExited = null,
        Func<Exception, bool>? isLockContentionException = null,
        Func<TimeSpan, CancellationToken, Task>? delayFunc = null,
        Action<string, Exception?>? logger = null,
        TimeSpan? exitWaitTimeout = null,
        int maxRetries = 4,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? creationTimeout = null)
    {
        _environmentFactory = environmentFactory ?? throw new ArgumentNullException(nameof(environmentFactory));
        _subscribeProcessExited = subscribeProcessExited ?? throw new ArgumentNullException(nameof(subscribeProcessExited));
        _unsubscribeProcessExited = unsubscribeProcessExited;
        _isLockContentionException = isLockContentionException ?? (_ => false);
        _delayFunc = delayFunc ?? Task.Delay;
        _logger = logger;
        _exitWaitTimeout = exitWaitTimeout ?? TimeSpan.FromSeconds(5);
        _maxRetries = maxRetries;
        _initialRetryDelay = initialRetryDelay ?? TimeSpan.FromMilliseconds(200);
        _creationTimeout = creationTimeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<TEnv> GetOrCreateEnvironmentAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Healthy reuse check
            lock (_stateLock)
            {
                if (_currentEnvironment != null)
                {
                    return _currentEnvironment;
                }
            }

            // 2. Await previous process exit if shutting down
            TaskCompletionSource<bool>? exitTcsToWait;
            uint? exitingPid;
            lock (_stateLock)
            {
                exitTcsToWait = _currentExitTcs;
                exitingPid = _activeBrowserPid;
            }

            if (exitTcsToWait != null && !exitTcsToWait.Task.IsCompleted)
            {
                try
                {
                    await exitTcsToWait.Task.WaitAsync(_exitWaitTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    _logger?.Invoke(
                        $"Previous browser process {(exitingPid.HasValue ? $"PID {exitingPid}" : "")} exit wait timed out after {_exitWaitTimeout.TotalSeconds}s. Proceeding to creation without process kill.",
                        ex
                    );
                }
            }

            // 3. Double-check after exit wait
            lock (_stateLock)
            {
                if (_currentEnvironment != null)
                {
                    return _currentEnvironment;
                }
            }

            // 4. Creation with exponential backoff on lock contention
            TEnv? createdEnv = null;
            Exception? lastException = null;

            for (int attempt = 1; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(_creationTimeout);
                    var creation = _environmentFactory(timeoutCts.Token);
                    try
                    {
                        createdEnv = await creation.WaitAsync(_creationTimeout, ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        timeoutCts.Cancel();
                        _ = creation.ContinueWith(task => _ = task.Exception,
                            TaskContinuationOptions.OnlyOnFaulted);
                        throw;
                    }
                    break;
                }
                catch (TimeoutException ex)
                {
                    lastException = ex;
                    _logger?.Invoke($"WebView2 environment creation timed out on attempt {attempt}/{_maxRetries}.", ex);
                    if (attempt < _maxRetries)
                        await _delayFunc(_initialRetryDelay, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (_isLockContentionException(ex))
                {
                    lastException = ex;
                    if (attempt < _maxRetries)
                    {
                        var delayMs = (int)(_initialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                        _logger?.Invoke($"Lockfile contention on attempt {attempt}/{_maxRetries}. Retrying in {delayMs}ms...", ex);
                        await _delayFunc(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
                    }
                }
            }

            if (createdEnv == null)
            {
                throw new InvalidOperationException(
                    $"Failed to initialize WebView2 environment after {_maxRetries} retry attempts.",
                    lastException
                );
            }

            long currentGen;
            lock (_stateLock)
            {
                currentGen = ++_generation;
            }

            // 5. Wire exit notification (RunContinuationsAsynchronously is essential to avoid re-entering COM state)
            var newExitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Action? exitCallback = null;
            exitCallback = () =>
            {
                // CRITICAL: NEVER acquire _semaphore here - breaks circular wait deadlock!
                if (_unsubscribeProcessExited != null && exitCallback != null)
                {
                    try { _unsubscribeProcessExited(createdEnv, exitCallback); } catch { }
                }

                lock (_stateLock)
                {
                    // Guard against late-firing events from older environments
                    if (ReferenceEquals(_currentEnvironment, createdEnv) && _generation == currentGen)
                    {
                        _currentEnvironment = null;
                    }
                    _activeBrowserPid = null;
                }

                newExitTcs.TrySetResult(true);
            };

            _subscribeProcessExited(createdEnv, exitCallback);

            lock (_stateLock)
            {
                _currentEnvironment = createdEnv;
                _currentExitTcs = newExitTcs;
            }

            return createdEnv;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void NotifyWindowClosing(uint? browserPid = null)
    {
        lock (_stateLock)
        {
            if (browserPid.HasValue)
            {
                _activeBrowserPid = browserPid;
            }
            // Mark current environment as null so closing environments are never handed out to new windows
            _currentEnvironment = null;
        }
    }
}

/// <summary>
/// Coordinates the creation, reuse, and disposal synchronization of the CoreWebView2Environment.
/// Handles clean re-initialization after browser process exit and prevents lockfile race conditions (0x8007139F).
/// </summary>
public static class WebView2EnvironmentCoordinator
{
    public static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AG2-Router",
        "webview2"
    );

    private static readonly WebView2EnvironmentCoordinatorCore<CoreWebView2Environment> _core = new(
        environmentFactory: async ct =>
        {
            Directory.CreateDirectory(UserDataFolder);
            var options = new CoreWebView2EnvironmentOptions();
            return await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: UserDataFolder,
                options: options
            ).ConfigureAwait(false);
        },
        subscribeProcessExited: (env, onExited) =>
        {
            void Handler(object? s, CoreWebView2BrowserProcessExitedEventArgs e) => onExited();
            env.BrowserProcessExited += Handler;
        },
        isLockContentionException: ex =>
            ex is COMException comEx && (
                comEx.HResult == unchecked((int)0x8007139F) ||
                comEx.HResult == unchecked((int)0x80070020)
            ),
        delayFunc: Task.Delay,
        logger: (msg, ex) => JsRuntimeDiagnostics.RecordError("WebView2Coordinator", msg, ex?.ToString())
    );

    /// <summary>
    /// Gets the current active environment or creates a fresh one.
    /// Awaits prior browser process termination if currently exiting to avoid lockfile contention.
    /// </summary>
    public static Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync(CancellationToken ct = default)
        => _core.GetOrCreateEnvironmentAsync(ct);

    /// <summary>
    /// Signals that a dashboard window is closing and its WebView2 control is tearing down.
    /// Informs the coordinator to release the active environment reference and track the exiting process.
    /// </summary>
    public static void NotifyWindowClosing(uint? browserPid = null)
        => _core.NotifyWindowClosing(browserPid);
}
