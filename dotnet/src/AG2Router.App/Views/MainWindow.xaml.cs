using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AG2Router.App.Diagnostics;
using AG2Router.App.Lifecycle;
using AG2Router.App.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AG2Router.App.Views;

public partial class MainWindow : Window, IDashboardWindow
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly string _dashboardUrl;
    private readonly WebViewNavigationPolicy _navigationPolicy;
    private CoreWebView2DevToolsProtocolEventReceiver? _exceptionReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _consoleReceiver;
    private CoreWebViewRecoveryEventSource? _eventSource;
    private WebViewRecoveryEventBinding? _eventBinding;
    private bool _isWebViewInitialized;
    private bool _isDisposed;
    private readonly WebViewRecoveryPolicy _recoveryPolicy = new();
    private bool _recoveryInProgress;
    private (WebViewFailureKind Kind, long Generation)? _pendingRecovery;
    private WebViewFailureKind? _exhaustedFailureKind;

    public uint? BrowserProcessId { get; private set; }

    public MainWindow(string dashboardUrl)
    {
        InitializeComponent();
        _dashboardUrl = dashboardUrl;
        _navigationPolicy = new WebViewNavigationPolicy(dashboardUrl);
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyImmersiveDarkMode();
        await InitializeWebViewAsync();
    }

    private void ApplyImmersiveDarkMode()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDarkMode = 0; // Light title bar matching White Premium UI
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }
        catch
        {
            // Optional styling; safely ignored on older OS versions
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_isWebViewInitialized || _isDisposed) return;
        var webView = DashboardWebView;
        long generation = _recoveryPolicy.CurrentGeneration;

        try
        {
            var env = await WebView2EnvironmentCoordinator.GetOrCreateEnvironmentAsync();
            if (!IsCurrentWebView(webView, generation)) return;

            await WebViewAttemptDeadline.RunAsync(
                () => webView.EnsureCoreWebView2Async(env), TimeSpan.FromSeconds(10));
            if (!IsCurrentWebView(webView, generation)) return;
            var core = webView.CoreWebView2;

            webView.DefaultBackgroundColor = System.Drawing.Color.White;

            BrowserProcessId = core.BrowserProcessId;

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;

            // 1. Trap unhandled promise rejections before application scripts load
            await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
                window.addEventListener('unhandledrejection', (e) => {
                    const reason = e.reason ? (e.reason.stack || e.reason.toString()) : 'Unhandled rejection with null reason';
                    console.error('UnhandledPromiseRejection: ' + reason);
                });
            ");
            if (!IsCurrentWebView(webView, generation)) return;

            // 2. Hook Runtime.exceptionThrown for synchronous & uncaught script exceptions
            _exceptionReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
            _exceptionReceiver.DevToolsProtocolEventReceived += OnExceptionThrown;

            // 3. Hook Runtime.consoleAPICalled to catch console.error / console.assert
            _consoleReceiver = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
            _consoleReceiver.DevToolsProtocolEventReceived += OnConsoleAPICalled;

            // 4. Enable CDP Runtime domain
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            if (!IsCurrentWebView(webView, generation)) return;

            // 5. Monitor navigation and process failures
            _eventSource = new CoreWebViewRecoveryEventSource(core);
            _eventBinding = new WebViewRecoveryEventBinding(
                _eventSource, _recoveryPolicy, generation,
                () => IsCurrentWebView(webView, generation),
                () =>
                {
                    _exhaustedFailureKind = null;
                    ErrorOverlay.Visibility = Visibility.Collapsed;
                },
                async (kind, detail, capturedGeneration) =>
                {
                    JsRuntimeDiagnostics.RecordError(
                        kind == WebViewFailureKind.Process ? "ProcessFailed" : "NavigationFailure", detail);
                    await RecoverWebViewAsync(kind, detail, capturedGeneration);
                });

            // 6. Security hardening (F7): lock top-level navigation to trusted local app origin
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;

            core.Navigate(_dashboardUrl);
            _isWebViewInitialized = true;
        }
        catch (ObjectDisposedException)
        {
            // Window closed during asynchronous initialization
        }
        catch (Exception ex)
        {
            if (!IsCurrentWebView(webView, generation)) return;
            JsRuntimeDiagnostics.RecordError("InitializationError", ex.Message, ex.StackTrace);
            if (_recoveryInProgress) throw;
            await RecoverWebViewAsync(WebViewFailureKind.Initialization, ex.Message, generation);
        }
    }

    private bool IsCurrentWebView(WebView2 webView, long generation) =>
        !_isDisposed && ReferenceEquals(DashboardWebView, webView) && _recoveryPolicy.IsCurrent(generation);

    private async Task RecoverWebViewAsync(WebViewFailureKind kind, string detail, long? sourceGeneration = null)
    {
        long generation = sourceGeneration ?? _recoveryPolicy.CurrentGeneration;
        if (_isDisposed || !_recoveryPolicy.IsCurrent(generation)) return;
        if (_recoveryInProgress)
        {
            if (kind == WebViewFailureKind.Process) _pendingRecovery = (kind, generation);
            return;
        }
        _recoveryInProgress = true;
        try
        {
            while (!_isDisposed && _recoveryPolicy.IsCurrent(generation))
            {
                var action = _recoveryPolicy.RecordFailureIfCurrent(generation, kind);
                if (action == null) return;
                if (action == WebViewRecoveryAction.ManualRetry)
                {
                    _exhaustedFailureKind = kind;
                    ErrorOverlayMessage.Text = $"Dashboard recovery stopped after repeated {kind.ToString().ToLowerInvariant()} failures ({detail}). Retry manually when the problem is resolved.";
                    ErrorOverlay.Visibility = Visibility.Visible;
                    return;
                }

                await Task.Delay(500);
                if (_isDisposed || !_recoveryPolicy.IsCurrent(generation)) return;
                try
                {
                    if (action == WebViewRecoveryAction.Reload && DashboardWebView.CoreWebView2 != null)
                        DashboardWebView.CoreWebView2.Navigate(_dashboardUrl);
                    else
                    {
                        RecreateWebViewControl();
                        generation = _recoveryPolicy.CurrentGeneration;
                        await InitializeWebViewAsync();
                    }
                    return;
                }
                catch (Exception ex)
                {
                    if (!_recoveryPolicy.IsCurrent(generation)) return;
                    JsRuntimeDiagnostics.RecordError("RecoveryError", ex.Message, ex.StackTrace);
                    kind = WebViewFailureKind.Initialization;
                    detail = ex.Message;
                }
            }
        }
        finally
        {
            _recoveryInProgress = false;
            if (!_isDisposed && _pendingRecovery is { } pending &&
                _recoveryPolicy.IsCurrent(pending.Generation))
            {
                _pendingRecovery = null;
                await RecoverWebViewAsync(pending.Kind, "WebView process failed during recovery", pending.Generation);
            }
            else _pendingRecovery = null;
        }
    }

    private void RecreateWebViewControl()
    {
        _recoveryPolicy.AdvanceGeneration();
        _pendingRecovery = null;
        var old = DashboardWebView;
        DetachWebViewEvents();
        if (old.CoreWebView2 != null)
        {
            old.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            old.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
        }
        if (_exceptionReceiver != null)
        {
            _exceptionReceiver.DevToolsProtocolEventReceived -= OnExceptionThrown;
            _exceptionReceiver = null;
        }
        if (_consoleReceiver != null)
        {
            _consoleReceiver.DevToolsProtocolEventReceived -= OnConsoleAPICalled;
            _consoleReceiver = null;
        }
        WebView2EnvironmentCoordinator.NotifyWindowClosing(BrowserProcessId);
        BrowserProcessId = null;
        (old.Parent as System.Windows.Controls.Panel)?.Children.Remove(old);
        old.Dispose();
        DashboardWebView = new WebView2();
        ((Grid)Content).Children.Insert(0, DashboardWebView);
        _isWebViewInitialized = false;
    }

    private void DetachWebViewEvents()
    {
        _eventBinding?.Dispose();
        _eventBinding = null;
        _eventSource?.Dispose();
        _eventSource = null;
    }

    private sealed class CoreWebViewRecoveryEventSource : IWebViewRecoveryEventSource, IDisposable
    {
        private readonly CoreWebView2 _core;

        public CoreWebViewRecoveryEventSource(CoreWebView2 core)
        {
            _core = core;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;
        }

        public event EventHandler<WebViewNavigationOutcomeEventArgs>? NavigationCompleted;
        public event EventHandler<WebViewProcessFailureEventArgs>? ProcessFailed;

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                // Deliberately cancelled navigation (e.g. unauthorized navigation blocked by policy)
                // must not trigger failure recovery or error overlays.
                return;
            }

            NavigationCompleted?.Invoke(this,
                new WebViewNavigationOutcomeEventArgs(e.IsSuccess, e.WebErrorStatus.ToString()));
        }

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) =>
            ProcessFailed?.Invoke(this,
                new WebViewProcessFailureEventArgs($"Process {e.ProcessFailedKind} exited with code {e.ExitCode}"));

        public void Dispose()
        {
            _core.NavigationCompleted -= OnNavigationCompleted;
            _core.ProcessFailed -= OnProcessFailed;
        }
    }

    private void OnExceptionThrown(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var details = doc.RootElement.GetProperty("exceptionDetails");
            var text = details.GetProperty("text").GetString() ?? "Uncaught JS Exception";
            var message = details.TryGetProperty("exception", out var ex) && ex.TryGetProperty("description", out var desc)
                ? desc.GetString() ?? text
                : text;

            string? stack = details.TryGetProperty("stackTrace", out var st) ? st.ToString() : null;
            JsRuntimeDiagnostics.RecordError("ExceptionThrown", message, stack);
        }
        catch
        {
            JsRuntimeDiagnostics.RecordError("ExceptionThrown", e.ParameterObjectAsJson);
        }
    }

    private void OnConsoleAPICalled(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type is "error" or "assert")
            {
                var args = doc.RootElement.GetProperty("args");
                var message = string.Join(" ", args.EnumerateArray().Select(a =>
                    a.TryGetProperty("value", out var v) ? v.ToString() :
                    a.TryGetProperty("description", out var d) ? d.ToString() : a.ToString()));

                JsRuntimeDiagnostics.RecordError($"Console.{type}", message);
            }
        }
        catch
        {
            // Ignore parse errors on debug output
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        TeardownWebView();
    }

    private void TeardownWebView()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _recoveryPolicy.AdvanceGeneration();
        _pendingRecovery = null;
        DetachWebViewEvents();

        if (DashboardWebView != null)
        {
            if (_isWebViewInitialized && DashboardWebView.CoreWebView2 != null)
            {
                DashboardWebView.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                DashboardWebView.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;

                if (_exceptionReceiver != null)
                {
                    _exceptionReceiver.DevToolsProtocolEventReceived -= OnExceptionThrown;
                    _exceptionReceiver = null;
                }

                if (_consoleReceiver != null)
                {
                    _consoleReceiver.DevToolsProtocolEventReceived -= OnConsoleAPICalled;
                    _consoleReceiver = null;
                }

                try
                {
                    DashboardWebView.CoreWebView2.Stop();
                }
                catch
                {
                    // Ignore stop error during shutdown
                }
            }

            WebView2EnvironmentCoordinator.NotifyWindowClosing(BrowserProcessId);

            // Detach from visual tree so WPF does not hold HwndHost reference
            (DashboardWebView.Parent as System.Windows.Controls.Panel)?.Children.Remove(DashboardWebView);

            try
            {
                DashboardWebView.Dispose();
            }
            catch
            {
                // Ignore disposal errors on teardown
            }
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!_navigationPolicy.ShouldAllowNavigation(e.Uri, out var reason))
        {
            e.Cancel = true;
            JsRuntimeDiagnostics.RecordError("NavigationBlocked", $"Blocked navigation to unauthorized URI '{e.Uri}'. Reason: {reason}");
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        JsRuntimeDiagnostics.RecordError("NewWindowBlocked", $"Blocked new window request to URI '{e.Uri}'.");
    }

    private void OnRetryConnectionClicked(object sender, RoutedEventArgs e)
    {
        _recoveryPolicy.ResetForManualRetry();
        ErrorOverlay.Visibility = Visibility.Collapsed;
        if (_isDisposed) return;
        if (_exhaustedFailureKind == WebViewFailureKind.Navigation && DashboardWebView.CoreWebView2 != null)
        {
            try
            {
                DashboardWebView.CoreWebView2.Navigate(_dashboardUrl);
            }
            catch (Exception ex)
            {
                JsRuntimeDiagnostics.RecordError("NavigationRetryError", ex.Message, ex.StackTrace);
                _ = RecoverWebViewAsync(WebViewFailureKind.Navigation, ex.Message);
            }
        }
        else
        {
            _exhaustedFailureKind = null;
            try
            {
                RecreateWebViewControl();
                _ = InitializeWebViewAsync();
            }
            catch (Exception ex)
            {
                JsRuntimeDiagnostics.RecordError("ManualRecoveryError", ex.Message, ex.StackTrace);
                _ = RecoverWebViewAsync(WebViewFailureKind.Initialization, ex.Message);
            }
        }
    }
}
