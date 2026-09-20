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

namespace AG2Router.App.Views;

public partial class MainWindow : Window, IDashboardWindow
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly string _dashboardUrl;
    private CoreWebView2DevToolsProtocolEventReceiver? _exceptionReceiver;
    private CoreWebView2DevToolsProtocolEventReceiver? _consoleReceiver;
    private bool _isWebViewInitialized;
    private bool _isDisposed;

    public uint? BrowserProcessId { get; private set; }

    public MainWindow(string dashboardUrl)
    {
        InitializeComponent();
        _dashboardUrl = dashboardUrl;
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
            int useDarkMode = 1;
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

        try
        {
            var env = await WebView2EnvironmentCoordinator.GetOrCreateEnvironmentAsync();
            if (_isDisposed) return;

            await DashboardWebView.EnsureCoreWebView2Async(env);
            if (_isDisposed)
            {
                TeardownWebView();
                return;
            }

            BrowserProcessId = DashboardWebView.CoreWebView2.BrowserProcessId;

            DashboardWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            DashboardWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            DashboardWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            // 1. Trap unhandled promise rejections before application scripts load
            await DashboardWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                window.addEventListener('unhandledrejection', (e) => {
                    const reason = e.reason ? (e.reason.stack || e.reason.toString()) : 'Unhandled rejection with null reason';
                    console.error('UnhandledPromiseRejection: ' + reason);
                });
            ");

            // 2. Hook Runtime.exceptionThrown for synchronous & uncaught script exceptions
            _exceptionReceiver = DashboardWebView.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
            _exceptionReceiver.DevToolsProtocolEventReceived += OnExceptionThrown;

            // 3. Hook Runtime.consoleAPICalled to catch console.error / console.assert
            _consoleReceiver = DashboardWebView.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
            _consoleReceiver.DevToolsProtocolEventReceived += OnConsoleAPICalled;

            // 4. Enable CDP Runtime domain
            await DashboardWebView.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");

            // 5. Monitor navigation and process failures
            DashboardWebView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                if (!e.IsSuccess)
                {
                    JsRuntimeDiagnostics.RecordError(
                        "NavigationFailure",
                        $"Navigation failed with status: {e.WebErrorStatus}"
                    );
                }
            };

            DashboardWebView.CoreWebView2.ProcessFailed += (s, e) =>
            {
                JsRuntimeDiagnostics.RecordError(
                    "ProcessFailed",
                    $"Process {e.ProcessFailedKind} exited with code {e.ExitCode}"
                );
            };

            DashboardWebView.CoreWebView2.Navigate(_dashboardUrl);
            _isWebViewInitialized = true;
        }
        catch (ObjectDisposedException)
        {
            // Window closed during asynchronous initialization
        }
        catch (Exception ex)
        {
            JsRuntimeDiagnostics.RecordError("InitializationError", ex.Message, ex.StackTrace);
            System.Windows.MessageBox.Show(
                $"Failed to initialize WebView2 dashboard runtime: {ex.Message}\n\nPlease ensure the Microsoft Edge WebView2 Runtime is installed.",
                "AG2 Router Initialization Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
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

        if (DashboardWebView != null)
        {
            if (_isWebViewInitialized && DashboardWebView.CoreWebView2 != null)
            {
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
}
