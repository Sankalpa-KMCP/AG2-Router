using System.Windows;
using AG2Router.App.Server;
using AG2Router.App.Tray;
using AG2Router.App.Views;
using AG2Router.Windows.Lifecycle;

namespace AG2Router.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstanceGuard;
    private LoopbackServer? _loopbackServer;
    private TrayIconManager? _trayIconManager;
    private MainWindow? _mainWindow;
    private QuickStatusWindow? _quickStatusWindow;
    private bool _isShuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Enforce per-user session single-instance execution
        _singleInstanceGuard = new SingleInstanceGuard();
        if (!_singleInstanceGuard.TryAcquire(OnSecondaryInstanceActivated))
        {
            // Another instance is already active in this session.
            // It has been signaled to activate its window. We exit immediately.
            Shutdown();
            return;
        }

        // 2. Prevent WPF from shutting down when windows are hidden to tray
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            // 3. Start embedded loopback server on ephemeral port (127.0.0.1:0)
            _loopbackServer = new LoopbackServer();
            await _loopbackServer.StartAsync(0);

            var dashboardUrl = $"{_loopbackServer.BoundUrl}/index.html";

            // 4. Initialize windows
            _mainWindow = new MainWindow(dashboardUrl);
            _quickStatusWindow = new QuickStatusWindow(OpenDashboard);

            // 5. Initialize notification area tray icon
            _trayIconManager = new TrayIconManager(
                onToggleQuickStatus: ToggleQuickStatus,
                onOpenDashboard: OpenDashboard,
                onExitRequested: ExitApplication
            );

            // 6. Show dashboard window on initial startup
            _mainWindow.ShowDashboard();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"An error occurred while starting AG2 Router: {ex.Message}",
                "AG2 Router Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
            ExitApplication();
        }
    }

    private void OnSecondaryInstanceActivated()
    {
        Dispatcher.Invoke(() =>
        {
            OpenDashboard();
        });
    }

    private void ToggleQuickStatus()
    {
        Dispatcher.Invoke(() =>
        {
            if (_quickStatusWindow == null) return;

            if (_quickStatusWindow.IsVisible)
            {
                _quickStatusWindow.Hide();
            }
            else
            {
                _quickStatusWindow.ShowNearTray();
            }
        });
    }

    private void OpenDashboard()
    {
        Dispatcher.Invoke(() =>
        {
            if (_quickStatusWindow != null && _quickStatusWindow.IsVisible)
            {
                _quickStatusWindow.Hide();
            }

            _mainWindow?.ShowDashboard();
        });
    }

    private async void ExitApplication()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        // Clean up UI & Tray icon immediately to eliminate ghost icons
        _trayIconManager?.Dispose();
        _trayIconManager = null;

        _quickStatusWindow?.Close();
        _quickStatusWindow = null;

        _mainWindow?.RequestExplicitExit();
        _mainWindow = null;

        // Stop in-process loopback host gracefully
        if (_loopbackServer != null)
        {
            await _loopbackServer.StopAsync();
            await _loopbackServer.DisposeAsync();
            _loopbackServer = null;
        }

        // Release mutex and pipe
        if (_singleInstanceGuard != null)
        {
            await _singleInstanceGuard.DisposeAsync();
            _singleInstanceGuard = null;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconManager?.Dispose();
        base.OnExit(e);
    }
}
