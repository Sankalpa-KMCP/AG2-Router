using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace AG2Router.App.Services;

/// <summary>
/// Coordinates the creation, reuse, and disposal synchronization of the CoreWebView2Environment.
/// Handles clean re-initialization after browser process exit and prevents lockfile race conditions (0x8007139F).
/// </summary>
public static class WebView2EnvironmentCoordinator
{
    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    private static CoreWebView2Environment? _currentEnvironment;
    private static TaskCompletionSource<bool>? _processExitTcs;
    private static uint? _activeBrowserPid;

    public static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AG2-Router",
        "webview2"
    );

    /// <summary>
    /// Gets the current active environment or creates a fresh one.
    /// Awaits prior browser process termination if currently exiting to avoid lockfile contention.
    /// </summary>
    public static async Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            // If a previous browser process is currently terminating, await its completion
            if (_processExitTcs != null && !_processExitTcs.Task.IsCompleted)
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                try
                {
                    await _processExitTcs.Task.WaitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Fallback: kill orphaned process if stuck
                    KillProcessTree(_activeBrowserPid);
                }
            }

            if (_currentEnvironment != null)
            {
                return _currentEnvironment;
            }

            Directory.CreateDirectory(UserDataFolder);
            var options = new CoreWebView2EnvironmentOptions();

            const int maxRetries = 4;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _currentEnvironment = await CoreWebView2Environment.CreateAsync(
                        browserExecutableFolder: null,
                        userDataFolder: UserDataFolder,
                        options: options
                    );
                    break;
                }
                catch (COMException ex) when (attempt < maxRetries &&
                    (ex.HResult == unchecked((int)0x8007139F) || ex.HResult == unchecked((int)0x80070020)))
                {
                    await Task.Delay(attempt * 250, ct);
                }
            }

            if (_currentEnvironment == null)
            {
                throw new InvalidOperationException("Failed to initialize CoreWebView2Environment after retry attempts.");
            }

            _processExitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _currentEnvironment.BrowserProcessExited += OnBrowserProcessExited;

            return _currentEnvironment;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public static void NotifyWindowClosing(uint? browserPid)
    {
        _activeBrowserPid = browserPid;
    }

    private static void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        _semaphore.Wait();
        try
        {
            if (_currentEnvironment != null)
            {
                _currentEnvironment.BrowserProcessExited -= OnBrowserProcessExited;
                _currentEnvironment = null;
            }
            _activeBrowserPid = null;
            _processExitTcs?.TrySetResult(true);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static void KillProcessTree(uint? pid)
    {
        if (pid.HasValue && pid.Value > 0)
        {
            try
            {
                var proc = Process.GetProcessById((int)pid.Value);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignored if process already terminated
            }
        }
    }
}
