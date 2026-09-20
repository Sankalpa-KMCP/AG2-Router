using System.Windows;
using System.Windows.Input;
using AG2Router.Windows.Tray;

namespace AG2Router.App.Views;

public partial class QuickStatusWindow : Window
{
    private readonly Action _onOpenDashboard;

    public QuickStatusWindow(Action onOpenDashboard)
    {
        InitializeComponent();
        _onOpenDashboard = onOpenDashboard;
    }

    public void ShowNearTray()
    {
        var pos = TaskbarPositionHelper.CalculateFlyoutPosition(Width, Height);
        Left = pos.X;
        Top = pos.Y;

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
