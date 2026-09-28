using System.Text.RegularExpressions;
using System.Windows;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Adapter;
using AG2Router.AG2.Discovery;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Security;
using AG2Router.AG2.Switching;
using AG2Router.AG2.Vault;
using AG2Router.App.Diagnostics;
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
    private NativeAutoRouter? _autoRouter;
    private IAutostartService? _autostartService;
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
            Log("Command line: --exit requested. Requesting graceful shutdown of primary instance...");
            bool exited = SingleInstanceGuard.RequestExitAndWait(timeoutMs: 5000);
            if (exited)
            {
                Log("Primary instance confirmed exited (or was not running).");
                Shutdown(0);
            }
            else
            {
                Log("Warning: Primary instance did not exit within 5000ms.");
                Shutdown(1);
            }
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
            Log("Initializing native vault and account services...");
            var processDetector = new AG2ProcessDetector();
            _ag2Adapter = new AG2LiveAdapter(processDetector);
            _dpapiProvider = new WindowsDpapiProvider();
            _sessionVault = new SessionVault(dpapiProvider: _dpapiProvider);
            _accountStore = new LocalMetadataAccountStore();
            _wincredReader = new WindowsWinCredReader();
            _wincredWriter = new WindowsWinCredWriter();
            _enrollmentService = new AccountEnrollmentService(_ag2Adapter, _wincredReader, _sessionVault, _accountStore);
            var processLifecycle = new WindowsAG2ProcessLifecycle(processDetector);
            string journalPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_accountStore.GetFilePath())!, "switch-journal.json");
            var switchJournalStore = new SwitchJournalStore(journalPath);
            _switchCoordinator = new NativeAccountSwitchCoordinator(
                _accountStore,
                _sessionVault,
                _wincredReader,
                _wincredWriter,
                _ag2Adapter,
                processLifecycle,
                switchJournalStore);

            Log("Reconciling startup switch journal...");
            var reconciliationResult = await _switchCoordinator.ReconcileStartupJournalAsync();
            Log($"Startup switch journal reconciliation completed: {reconciliationResult.Status}. {reconciliationResult.Message}");

            Log("Initializing NativeAutoRouter and AutostartService...");
            string configPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_accountStore.GetFilePath())!, "config.json");
            _autoRouter = new NativeAutoRouter(_accountStore, _sessionVault, _ag2Adapter, _switchCoordinator, configFilePath: configPath);
            _autostartService = new WindowsRegistryAutostartService(new WindowsRegistryAccessor());

            Log("Initializing TelemetryPollingCoordinator...");
            _telemetryCoordinator = new TelemetryPollingCoordinator(
                _ag2Adapter,
                interval: TimeSpan.FromMilliseconds(_autoRouter.GetConfig().PollingIntervalMs),
                autoRouter: _autoRouter,
                log: Log);
            _telemetryCoordinator.Start();

            Log("Starting loopback server...");
            _loopbackServer = new LoopbackServer();
            await _loopbackServer.StartAsync(
                0,
                statusProvider: () => _telemetryCoordinator.CurrentStatus,
                accountStore: _accountStore,
                sessionVault: _sessionVault,
                enrollmentService: _enrollmentService,
                switchCoordinator: _switchCoordinator,
                autoRouter: _autoRouter,
                autostartService: _autostartService,
                onPollingIntervalChanged: interval => _telemetryCoordinator?.UpdateInterval(interval));

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
                    var autoStatus = status.Router.AutoSwitchEnabled ? $"Auto: {status.Router.State}" : "Auto: Off";
                    _trayIconManager?.UpdateTooltip($"AG2 Router - {status.Ag2.Status} ({autoStatus})");
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
                FormatStartupErrorMessage(ex),
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

        if (_switchCoordinator != null)
        {
            try
            {
                await _switchCoordinator.CoordinateShutdownAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException ex)
            {
                Log($"Shutdown deferred: {ex.Message}");
                _isShuttingDown = false;
                System.Windows.MessageBox.Show(ex.Message, "AG2 Router Switch Recovery",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

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

        if (_autoRouter != null)
        {
            try
            {
                await _autoRouter.DisposeAsync();
            }
            catch { }
            _autoRouter = null;
        }

        _quickStatusWindow?.Close();
        _quickStatusWindow = null;

        _dashboardManager?.CloseDashboard();
        _dashboardManager = null;

        _switchCoordinator = null;

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

        // Give queued JavaScript diagnostics a short, best-effort drain window.
        await JsRuntimeDiagnostics.DrainForShutdownAsync(TimeSpan.FromSeconds(1));

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"App.OnExit called with exit code {e.ApplicationExitCode}");
        _trayIconManager?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Formats an actionable startup error message preserving the high-level context and appending inner validation details
    /// when safe and not already included, while strictly sanitizing and redacting tokens/credentials and omitting stack traces.
    /// </summary>
    public static string FormatStartupErrorMessage(Exception ex)
    {
        if (ex == null)
        {
            return "An error occurred while starting AG2 Router: An unexpected error occurred.";
        }

        const string prefix = "An error occurred while starting AG2 Router: ";
        string primary = SanitizeConfigPath(StripStackTraces(ex.Message ?? string.Empty).Trim());

        string result;
        if (string.IsNullOrWhiteSpace(primary))
        {
            result = "An error occurred while starting AG2 Router.";
        }
        else if (primary.StartsWith("An error occurred while starting AG2 Router", StringComparison.OrdinalIgnoreCase))
        {
            result = primary;
        }
        else
        {
            result = prefix + primary;
        }

        IEnumerable<Exception> innerExceptions = ex switch
        {
            AggregateException agg => agg.Flatten().InnerExceptions,
            _ => GetInnerExceptions(ex)
        };

        foreach (var inner in innerExceptions)
        {
            string detail = SanitizeConfigPath(StripStackTraces(inner.Message ?? string.Empty).Trim());
            if (!string.IsNullOrWhiteSpace(detail) &&
                !result.Contains(detail, StringComparison.OrdinalIgnoreCase))
            {
                if (result.EndsWith('.'))
                {
                    result = $"{result.TrimEnd('.')}: {detail}";
                }
                else if (result.EndsWith(':'))
                {
                    result = $"{result} {detail}";
                }
                else
                {
                    result = $"{result}: {detail}";
                }
            }
        }

        string sanitized = SanitizeConfigPath(result);
        sanitized = AG2Security.SanitizeCommandLine(sanitized);
        sanitized = AG2Security.RedactSensitiveText(sanitized);
        return sanitized;
    }

    private static readonly Regex ConfigPathRegex = new(
        @"Router configuration file '(?<path>.*?)'(?<suffix>\s+(?:contains invalid configuration|is malformed))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string SanitizeConfigPath(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return message;

        return ConfigPathRegex.Replace(message, match =>
        {
            string rawPath = match.Groups["path"].Value;
            string fileName = string.Empty;
            try
            {
                fileName = System.IO.Path.GetFileName(rawPath);
            }
            catch
            {
                // Fall back if path contains invalid characters
            }

            if (string.IsNullOrWhiteSpace(fileName) || string.Equals(fileName, "config.json", StringComparison.OrdinalIgnoreCase))
            {
                fileName = "config.json";
            }

            string suffix = match.Groups["suffix"].Value;
            return $"Router configuration file '{fileName}'{suffix}";
        });
    }

    private static IEnumerable<Exception> GetInnerExceptions(Exception ex)
    {
        var current = ex.InnerException;
        while (current != null)
        {
            yield return current;
            current = current.InnerException;
        }
    }

    private static string StripStackTraces(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var filtered = lines.Where(line =>
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase)) return false;
            if (trimmed.StartsWith("in ", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(":line ")) return false;
            if (trimmed.StartsWith("--- End of stack trace", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        });
        return string.Join(Environment.NewLine, filtered).Trim();
    }
}
