using System.Windows;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Adapter;
using AG2Router.AG2.Discovery;
using AG2Router.AG2.Switching;
using AG2Router.AG2.Vault;
using AG2Router.App.Lifecycle;
using AG2Router.App.Server;
using AG2Router.App.Services;
using AG2Router.App.Tray;
using AG2Router.App.Views;
using AG2Router.Windows.Lifecycle;
using AG2Router.Windows.Security;

namespace AG2Router.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstanceGuard;
    private LoopbackServer? _loopbackServer;
    private TrayIconManager? _trayIconManager;
    private DashboardLifecycleManager? _dashboardManager;
    private QuickStatusWindow? _quickStatusWindow;
    private AG2LiveAdapter? _ag2Adapter;
    private TelemetryPollingCoordinator? _telemetryCoordinator;
    private WindowsDpapiProvider? _dpapiProvider;
    private SessionVault? _sessionVault;
    private LocalMetadataAccountStore? _accountStore;
    private WindowsWinCredReader? _wincredReader;
    private WindowsWinCredWriter? _wincredWriter;
    private AccountEnrollmentService? _enrollmentService;
    private NativeAccountSwitchCoordinator? _switchCoordinator;
    private bool _isShuttingDown;

    private static void Log(string msg)
    {
        try
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AG2-Router",
                "app.log"
            );
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
            System.IO.File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [PID {Environment.ProcessId}] {msg}\n");
        }
        catch { }
    }

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppDomain.CurrentDomain.ProcessExit += (s, ev) =>
        {
            Log("AppDomain.ProcessExit triggered. Environment.StackTrace:\n" + Environment.StackTrace);
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            Log($"UnhandledException: {ev.ExceptionObject}");
        };
        DispatcherUnhandledException += (s, ev) =>
        {
            Log($"DispatcherUnhandledException: {ev.Exception}");
        };

        // 1. Handle explicit command-line controls for secondary instances
        if (e.Args.Contains("--close"))
        {
            Log("Command line: --close requested. Forwarding to primary instance.");
            SingleInstanceGuard.SendCommand("CLOSE");
            Shutdown();
            return;
        }

        if (e.Args.Contains("--exit"))
        {
            Log("Command line: --exit requested. Forwarding to primary instance.");
            SingleInstanceGuard.SendCommand("EXIT");
            Shutdown();
            return;
        }

        // 2. Enforce per-user session single-instance execution
        _singleInstanceGuard = new SingleInstanceGuard();
        if (!_singleInstanceGuard.TryAcquire(
            onActivateRequested: OnSecondaryInstanceActivated,
            onCloseRequested: () => Dispatcher.Invoke(() =>
            {
                Log("Named pipe command CLOSE received.");
                _dashboardManager?.CloseDashboard();
            }),
            onExitRequested: () => Dispatcher.Invoke(ExitApplication)))
        {
            Log("Duplicate instance detected; activation signal sent to primary instance. Terminating secondary instance.");
            Shutdown();
            return;
        }

        Log("Primary instance acquired single-instance guard.");

        // 3. Prevent WPF from shutting down when windows are hidden to tray
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            Log("Initializing AG2LiveAdapter and TelemetryPollingCoordinator...");
            var processDetector = new AG2ProcessDetector();
            _ag2Adapter = new AG2LiveAdapter(processDetector);
            _telemetryCoordinator = new TelemetryPollingCoordinator(_ag2Adapter);
            _telemetryCoordinator.Start();

            Log("Initializing native vault and account services...");
            _dpapiProvider = new WindowsDpapiProvider();
            _sessionVault = new SessionVault(dpapiProvider: _dpapiProvider);
            _accountStore = new LocalMetadataAccountStore();
            _wincredReader = new WindowsWinCredReader();
            _wincredWriter = new WindowsWinCredWriter();
            _enrollmentService = new AccountEnrollmentService(_ag2Adapter, _wincredReader, _sessionVault, _accountStore);
            var processLifecycle = new WindowsAG2ProcessLifecycle(processDetector);
            _switchCoordinator = new NativeAccountSwitchCoordinator(
                _accountStore,
                _sessionVault,
                _wincredReader,
                _wincredWriter,
                _ag2Adapter,
                processLifecycle);

            Log("Starting loopback server...");
            _loopbackServer = new LoopbackServer();
            await _loopbackServer.StartAsync(
                0,
                statusProvider: () => _telemetryCoordinator.CurrentStatus,
                accountStore: _accountStore,
                sessionVault: _sessionVault,
                enrollmentService: _enrollmentService,
                switchCoordinator: _switchCoordinator);

            var dashboardUrl = $"{_loopbackServer.BoundUrl}/index.html";
            Log($"Loopback server bound to: {dashboardUrl}");

            // 4. Initialize dashboard lifecycle manager (lazy window creation)
            Log("Initializing DashboardLifecycleManager...");
            _dashboardManager = new DashboardLifecycleManager(dashboardUrl);

            Log("Initializing QuickStatusWindow...");
            _quickStatusWindow = new QuickStatusWindow(OpenDashboard, statusProvider: () => _telemetryCoordinator.CurrentStatus);

            _telemetryCoordinator.StatusUpdated += status =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (_quickStatusWindow?.IsVisible == true)
                    {
                        _quickStatusWindow.UpdateStatus(status);
                    }
                });
            };

            // 5. Initialize notification area tray icon
            Log("Initializing TrayIconManager...");
            _trayIconManager = new TrayIconManager(
                onToggleQuickStatus: ToggleQuickStatus,
                onOpenDashboard: OpenDashboard,
                onExitRequested: ExitApplication
            );

            Log("Tray-first startup completed. Application active in system tray.");

            if (e.Args.Contains("--open"))
            {
                OpenDashboard();
            }
        }
        catch (Exception ex)
        {
            Log($"Startup failed with exception: {ex}");
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
        Log("OnSecondaryInstanceActivated invoked by named pipe listener.");
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
        Log("OpenDashboard requested.");
        Dispatcher.Invoke(() =>
        {
            if (_quickStatusWindow != null && _quickStatusWindow.IsVisible)
            {
                _quickStatusWindow.Hide();
            }

            Log("Calling _dashboardManager.OpenDashboard()");
            _dashboardManager?.OpenDashboard();
            Log("Called _dashboardManager.OpenDashboard() completed.");
        });
    }

    private async void ExitApplication()
    {
        Log("ExitApplication requested.");
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        // Clean up UI & Tray icon immediately to eliminate ghost icons
        _trayIconManager?.Dispose();
        _trayIconManager = null;

        // Stop telemetry polling coordinator before destroying UI subscribers
        if (_telemetryCoordinator != null)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _telemetryCoordinator.StopAsync(shutdownCts.Token);
                await _telemetryCoordinator.DisposeAsync();
            }
            catch { }
            _telemetryCoordinator = null;
        }

        _quickStatusWindow?.Close();
        _quickStatusWindow = null;

        _dashboardManager?.CloseDashboard();
        _dashboardManager = null;

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
        Log($"App.OnExit called with exit code {e.ApplicationExitCode}");
        _trayIconManager?.Dispose();
        base.OnExit(e);
    }
}
