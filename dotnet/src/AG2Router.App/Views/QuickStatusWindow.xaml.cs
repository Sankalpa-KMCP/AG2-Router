using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AG2Router.Windows.Tray;

namespace AG2Router.App.Views;

public partial class QuickStatusWindow : Window
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private readonly Action _onOpenDashboard;

    public QuickStatusWindow(Action onOpenDashboard)
    {
        InitializeComponent();
        _onOpenDashboard = onOpenDashboard;
    }

    public void ShowNearTray()
    {
        var mousePos = System.Windows.Forms.Cursor.Position;
        var (scaleX, scaleY) = TaskbarPositionHelper.GetMonitorDpiScale(mousePos);
        if (scaleX <= 0) scaleX = 1.0;
        if (scaleY <= 0) scaleY = 1.0;

        int physWidth = (int)Math.Ceiling(Width * scaleX);
        int physHeight = (int)Math.Ceiling(Height * scaleY);

        var pos = TaskbarPositionHelper.CalculateFlyoutPosition(physWidth, physHeight);

        var helper = new WindowInteropHelper(this);
        var hwnd = helper.EnsureHandle();

        SetWindowPos(hwnd, IntPtr.Zero, pos.X, pos.Y, physWidth, physHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        Left = pos.X / scaleX;
        Top = pos.Y / scaleY;

        Show();
        Activate();
        BtnOpenDashboard.Focus();
    }

    private void BtnOpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _onOpenDashboard();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Hide();
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Hide();
    }
}
