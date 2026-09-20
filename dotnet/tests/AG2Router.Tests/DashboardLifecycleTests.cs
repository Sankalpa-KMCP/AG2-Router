using System.Windows;
using AG2Router.App.Diagnostics;
using AG2Router.App.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

public class MockDashboardWindow : IDashboardWindow
{
    public bool IsVisible { get; set; } = false;
    public WindowState WindowState { get; set; } = WindowState.Normal;
    public int ShowCallCount { get; private set; }
    public int ActivateCallCount { get; private set; }
    public int FocusCallCount { get; private set; }
    public int CloseCallCount { get; private set; }
    public bool IsClosed { get; private set; }

    public event EventHandler? Closed;

    public void Show()
    {
        ShowCallCount++;
        IsVisible = true;
    }

    public bool Activate()
    {
        ActivateCallCount++;
        return true;
    }

    public bool Focus()
    {
        FocusCallCount++;
        return true;
    }

    public void Close()
    {
        CloseCallCount++;
        IsClosed = true;
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void SimulateUserClose()
    {
        IsClosed = true;
        IsVisible = false;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}

public class DashboardLifecycleTests
{
    private const string TestDashboardUrl = "http://127.0.0.1:54321/index.html";

    [Fact]
    public void InitialState_DashboardIsNotOpenAndNoWindowCreated()
    {
        int factoryCalls = 0;
        var manager = new DashboardLifecycleManager(TestDashboardUrl, url =>
        {
            factoryCalls++;
            return new MockDashboardWindow();
        });

        Assert.False(manager.IsDashboardOpen);
        Assert.Null(manager.CurrentWindow);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void OpenDashboard_LazilyCreatesWindowViaFactory()
    {
        int factoryCalls = 0;
        string? passedUrl = null;
        MockDashboardWindow? createdWindow = null;

        var manager = new DashboardLifecycleManager(TestDashboardUrl, url =>
        {
            factoryCalls++;
            passedUrl = url;
            createdWindow = new MockDashboardWindow();
            return createdWindow;
        });

        bool openedFired = false;
        manager.DashboardOpened += () => openedFired = true;

        manager.OpenDashboard();

        Assert.True(manager.IsDashboardOpen);
        Assert.NotNull(manager.CurrentWindow);
        Assert.Same(createdWindow, manager.CurrentWindow);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(TestDashboardUrl, passedUrl);
        Assert.True(openedFired);
        Assert.NotNull(createdWindow);
        Assert.Equal(1, createdWindow.ShowCallCount);
        Assert.Equal(1, createdWindow.ActivateCallCount);
    }

    [Fact]
    public void OpenDashboard_WhenAlreadyOpen_ReusesExistingWindowWithoutCallingFactory()
    {
        int factoryCalls = 0;
        var manager = new DashboardLifecycleManager(TestDashboardUrl, url =>
        {
            factoryCalls++;
            return new MockDashboardWindow();
        });

        manager.OpenDashboard();
        var firstWindow = (MockDashboardWindow)manager.CurrentWindow!;
        Assert.Equal(1, factoryCalls);

        // Second call
        manager.OpenDashboard();
        Assert.Equal(1, factoryCalls);
        Assert.Same(firstWindow, manager.CurrentWindow);
        Assert.Equal(2, firstWindow.ActivateCallCount);
    }

    [Fact]
    public void OpenDashboard_WhenMinimized_RestoresWindowStateToNormal()
    {
        var window = new MockDashboardWindow();
        var manager = new DashboardLifecycleManager(TestDashboardUrl, _ => window);

        manager.OpenDashboard();
        window.WindowState = WindowState.Minimized;

        manager.OpenDashboard();
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [Fact]
    public void CloseDashboard_ClosesWindowAndResetsState()
    {
        MockDashboardWindow? window = null;
        var manager = new DashboardLifecycleManager(TestDashboardUrl, _ =>
        {
            window = new MockDashboardWindow();
            return window;
        });

        bool closedFired = false;
        manager.DashboardClosed += () => closedFired = true;

        manager.OpenDashboard();
        Assert.True(manager.IsDashboardOpen);

        manager.CloseDashboard();
        Assert.False(manager.IsDashboardOpen);
        Assert.Null(manager.CurrentWindow);
        Assert.NotNull(window);
        Assert.Equal(1, window.CloseCallCount);
        Assert.True(window.IsClosed);
        Assert.True(closedFired);
    }

    [Fact]
    public void WindowClosedEvent_FromWindow_ResetsManagerState()
    {
        MockDashboardWindow? window = null;
        var manager = new DashboardLifecycleManager(TestDashboardUrl, _ =>
        {
            window = new MockDashboardWindow();
            return window;
        });

        bool closedFired = false;
        manager.DashboardClosed += () => closedFired = true;

        manager.OpenDashboard();
        Assert.True(manager.IsDashboardOpen);

        // Simulate user clicking [X] on window
        window!.SimulateUserClose();

        Assert.False(manager.IsDashboardOpen);
        Assert.Null(manager.CurrentWindow);
        Assert.True(closedFired);
    }

    [Fact]
    public void ReopenAfterClose_CreatesFreshWindowInstance()
    {
        var createdWindows = new List<MockDashboardWindow>();
        var manager = new DashboardLifecycleManager(TestDashboardUrl, _ =>
        {
            var win = new MockDashboardWindow();
            createdWindows.Add(win);
            return win;
        });

        // Open 1
        manager.OpenDashboard();
        Assert.Single(createdWindows);
        var firstWindow = createdWindows[0];

        // Close 1
        manager.CloseDashboard();
        Assert.False(manager.IsDashboardOpen);

        // Open 2
        manager.OpenDashboard();
        Assert.Equal(2, createdWindows.Count);
        var secondWindow = createdWindows[1];

        Assert.NotSame(firstWindow, secondWindow);
        Assert.Same(secondWindow, manager.CurrentWindow);
    }

    [Fact]
    public void JsRuntimeDiagnostics_RecordsAndClearsErrors()
    {
        JsRuntimeDiagnostics.Clear();
        Assert.Empty(JsRuntimeDiagnostics.Errors);

        JsRuntimeDiagnostics.RecordError("TestCategory", "Test error message", "at line 1");
        var errors = JsRuntimeDiagnostics.Errors;

        Assert.Single(errors);
        Assert.Equal("TestCategory", errors[0].Source);
        Assert.Equal("Test error message", errors[0].Message);
        Assert.Equal("at line 1", errors[0].StackTrace);

        JsRuntimeDiagnostics.Clear();
        Assert.Empty(JsRuntimeDiagnostics.Errors);
    }
}
