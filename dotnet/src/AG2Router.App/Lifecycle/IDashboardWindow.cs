using System.Windows;

namespace AG2Router.App.Lifecycle;

public interface IDashboardWindow
{
    bool IsVisible { get; }
    WindowState WindowState { get; set; }
    void Show();
    bool Activate();
    bool Focus();
    void Close();
    event EventHandler Closed;
}
