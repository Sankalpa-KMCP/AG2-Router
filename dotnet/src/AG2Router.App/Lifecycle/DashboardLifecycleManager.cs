using System.Windows;

namespace AG2Router.App.Lifecycle;

/// <summary>
/// Coordinates the lazy creation, activation, and disposal lifecycle of the native dashboard window.
/// Enforces tray-first execution by ensuring WebView2 and its associated window are never instantiated
/// until explicitly requested by the user or secondary instance activation.
/// </summary>
public class DashboardLifecycleManager
{
    private readonly Func<string, IDashboardWindow> _windowFactory;
    private readonly string _dashboardUrl;
    private IDashboardWindow? _currentWindow;

    public bool IsDashboardOpen => _currentWindow != null;
    public IDashboardWindow? CurrentWindow => _currentWindow;

    public event Action? DashboardOpened;
    public event Action? DashboardClosed;

    public DashboardLifecycleManager(string dashboardUrl, Func<string, IDashboardWindow>? windowFactory = null)
    {
        _dashboardUrl = dashboardUrl ?? throw new ArgumentNullException(nameof(dashboardUrl));
        _windowFactory = windowFactory ?? (url => new Views.MainWindow(url));
    }

    /// <summary>
    /// Lazily creates the dashboard window on demand, or activates and restores the existing window if already open.
    /// </summary>
    public void OpenDashboard()
    {
        if (_currentWindow == null)
        {
            _currentWindow = _windowFactory(_dashboardUrl);
            _currentWindow.Closed += OnWindowClosed;
            _currentWindow.Show();
            _currentWindow.Activate();
            DashboardOpened?.Invoke();
        }
        else
        {
            if (_currentWindow.WindowState == WindowState.Minimized)
            {
                _currentWindow.WindowState = WindowState.Normal;
            }
            _currentWindow.Show();
            _currentWindow.Activate();
            _currentWindow.Focus();
        }
    }

    /// <summary>
    /// Explicitly closes the dashboard window if open, triggering child process cleanup.
    /// </summary>
    public void CloseDashboard()
    {
        if (_currentWindow != null)
        {
            var win = _currentWindow;
            _currentWindow = null;
            win.Closed -= OnWindowClosed;
            win.Close();
            DashboardClosed?.Invoke();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is IDashboardWindow win)
        {
            win.Closed -= OnWindowClosed;
        }
        _currentWindow = null;
        DashboardClosed?.Invoke();
    }
}
