using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace AG2Router.App.Views;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly string _dashboardUrl;
    private bool _isExplicitExit;
    private bool _isWebViewInitialized;

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
        if (_isWebViewInitialized) return;

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var userDataFolder = Path.Combine(localAppData, "AG2-Router", "webview2");
            Directory.CreateDirectory(userDataFolder);

            var envOptions = new CoreWebView2EnvironmentOptions();
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder,
                options: envOptions
            );

            await DashboardWebView.EnsureCoreWebView2Async(env);

            DashboardWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            DashboardWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            DashboardWebView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            DashboardWebView.CoreWebView2.Navigate(_dashboardUrl);
            _isWebViewInitialized = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to initialize WebView2 dashboard runtime: {ex.Message}\n\nPlease ensure the Microsoft Edge WebView2 Runtime is installed.",
                "AG2 Router Initialization Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error
            );
        }
    }

    public void ShowDashboard()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();

        if (_isWebViewInitialized && DashboardWebView.CoreWebView2 != null)
        {
            try
            {
                DashboardWebView.Visibility = Visibility.Visible;
                DashboardWebView.CoreWebView2.Resume();
            }
            catch
            {
                // Safely ignored if already active
            }
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            // Close-to-tray: Cancel window destruction and hide
            e.Cancel = true;
            Hide();

            // Suspend WebView2 render cycles while hidden
            await SuspendWebViewAsync();
        }
    }

    private async Task SuspendWebViewAsync()
    {
        if (_isWebViewInitialized && DashboardWebView.CoreWebView2 != null)
        {
            try
            {
                DashboardWebView.Visibility = Visibility.Collapsed;
                await DashboardWebView.CoreWebView2.TrySuspendAsync();
            }
            catch
            {
                // Suspend is an optimization; failure is non-fatal
            }
        }
    }

    public void RequestExplicitExit()
    {
        _isExplicitExit = true;
        Close();
    }
}
