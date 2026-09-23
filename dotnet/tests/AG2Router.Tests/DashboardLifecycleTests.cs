using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using AG2Router.App.Diagnostics;
using AG2Router.App.Lifecycle;
using AG2Router.App.Services;
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
    public async Task JsRuntimeDiagnostics_UsesInjectedDestinationAndClearsErrors()
    {
        const string destination = "test://records-and-clear";
        var writes = new ConcurrentQueue<PersistenceObservation>();
        var store = CreateStore(destination, (path, snapshot) =>
        {
            writes.Enqueue(new PersistenceObservation(path, snapshot.ToArray()));
            return Task.CompletedTask;
        });

        store.RecordError("TestCategory", "Test error message", "at line 1");
        var errors = store.Errors;

        Assert.Single(errors);
        Assert.Equal("TestCategory", errors[0].Source);
        Assert.Equal("Test error message", errors[0].Message);
        Assert.Equal("at line 1", errors[0].StackTrace);

        await store.ClearAsync();

        Assert.Empty(store.Errors);
        Assert.NotEmpty(writes);
        Assert.All(writes, write => Assert.Equal(destination, write.Destination));
        Assert.Empty(writes.Last().Snapshot);
    }

    [Fact]
    public async Task JsRuntimeDiagnostics_ConcurrentStoresCannotCrossContaminateDestinations()
    {
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var writes = new ConcurrentQueue<PersistenceObservation>();

        var first = CreateStore("test://first", async (path, snapshot) =>
        {
            writes.Enqueue(new PersistenceObservation(path, snapshot.ToArray()));
            firstStarted.TrySetResult(true);
            await releaseFirst.Task;
        });
        var second = CreateStore("test://second", (path, snapshot) =>
        {
            writes.Enqueue(new PersistenceObservation(path, snapshot.ToArray()));
            return Task.CompletedTask;
        });

        first.RecordError("First", "first message");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        second.RecordError("Second", "second message");
        await second.FlushAsync();

        releaseFirst.TrySetResult(true);
        await first.FlushAsync();

        Assert.Contains(writes, write =>
            write.Destination == "test://first" &&
            write.Snapshot.All(error => error.Source == "First"));
        Assert.Contains(writes, write =>
            write.Destination == "test://second" &&
            write.Snapshot.All(error => error.Source == "Second"));
        Assert.DoesNotContain(writes, write =>
            write.Destination == "test://first" &&
            write.Snapshot.Any(error => error.Source == "Second"));
        Assert.DoesNotContain(writes, write =>
            write.Destination == "test://second" &&
            write.Snapshot.Any(error => error.Source == "First"));
    }

    [Fact]
    public async Task JsRuntimeDiagnostics_BoundedAt100_EvictsOldestEntries()
    {
        var writes = new ConcurrentQueue<PersistenceObservation>();
        var store = CreateStore("test://bounded", (path, snapshot) =>
        {
            writes.Enqueue(new PersistenceObservation(path, snapshot.ToArray()));
            return Task.CompletedTask;
        });

        for (var i = 0; i < 105; i++)
        {
            store.RecordError("Source", $"Message {i}", $"Stack {i}");
        }

        await store.FlushAsync();
        var errors = store.Errors;

        Assert.Equal(JsRuntimeDiagnostics.MaxRetainedErrors, errors.Count);
        Assert.Equal("Message 5", errors[0].Message);
        Assert.Equal("Message 104", errors[99].Message);
        Assert.Equal(errors.Select(error => error.Message), writes.Last().Snapshot.Select(error => error.Message));
    }

    [Fact]
    public async Task JsRuntimeDiagnostics_BlockedPersistenceDoesNotBlockRecordError()
    {
        var writeStarted = NewSignal();
        var releaseWrite = NewSignal();
        var store = CreateStore("test://blocked", async (_, _) =>
        {
            writeStarted.TrySetResult(true);
            await releaseWrite.Task;
        });

        var recordCall = Task.Run(() => store.RecordError("Blocked", "caller already returned"));
        try
        {
            await recordCall.WaitAsync(TimeSpan.FromSeconds(1));
            await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Single(store.Errors);
            Assert.False(releaseWrite.Task.IsCompleted);

            var flush = store.FlushAsync();
            Assert.False(flush.IsCompleted);
            releaseWrite.TrySetResult(true);
            await flush;
        }
        finally
        {
            releaseWrite.TrySetResult(true);
        }
    }

    [Fact]
    public async Task JsRuntimeDiagnostics_FlushUsesAnExactQueueBoundary()
    {
        var firstStarted = NewSignal();
        var secondStarted = NewSignal();
        var releaseFirst = NewSignal();
        var releaseSecond = NewSignal();
        var callCount = 0;
        var store = CreateStore("test://flush-boundary", async (_, _) =>
        {
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task;
            }
            else if (call == 2)
            {
                secondStarted.TrySetResult(true);
                await releaseSecond.Task;
            }
        });

        store.RecordError("First", "before boundary");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var boundary = store.FlushAsync();
        store.RecordError("Second", "after boundary");

        Assert.False(boundary.IsCompleted);
        releaseFirst.TrySetResult(true);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await boundary.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(releaseSecond.Task.IsCompleted);

        releaseSecond.TrySetResult(true);
        await store.FlushAsync();
    }

    [Fact]
    public async Task JsRuntimeDiagnostics_ClearWaitsForItsOrderedEmptyWrite()
    {
        var firstStarted = NewSignal();
        var clearStarted = NewSignal();
        var releaseFirst = NewSignal();
        var releaseClear = NewSignal();
        var writes = new ConcurrentQueue<PersistenceObservation>();
        var callCount = 0;
        var store = CreateStore("test://clear-order", async (path, snapshot) =>
        {
            writes.Enqueue(new PersistenceObservation(path, snapshot.ToArray()));
            var call = Interlocked.Increment(ref callCount);
            if (call == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task;
            }
            else if (call == 2)
            {
                clearStarted.TrySetResult(true);
                await releaseClear.Task;
            }
        });

        store.RecordError("Old", "must be cleared");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var clear = store.ClearAsync();

        Assert.False(clear.IsCompleted);
        releaseFirst.TrySetResult(true);
        await clearStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(clear.IsCompleted);
        Assert.Empty(writes.Last().Snapshot);

        releaseClear.TrySetResult(true);
        await clear;

        Assert.Empty(store.Errors);
        Assert.Empty(writes.Last().Snapshot);
    }

    [Fact]
    public async Task JsRuntimeDiagnostics_PersistenceFailureIsObservableAndWorkerRecovers()
    {
        var callCount = 0;
        var store = CreateStore("test://failure", (_, _) =>
        {
            var call = Interlocked.Increment(ref callCount);
            return call is 1 or 3
                ? Task.FromException(new IOException($"Injected failure {call}"))
                : Task.CompletedTask;
        });

        store.RecordError("Failure", "first write fails");
        var flushFailure = await Assert.ThrowsAsync<IOException>(() => store.FlushAsync());
        Assert.Contains("Injected failure 1", flushFailure.Message);
        await Assert.ThrowsAsync<IOException>(() => store.FlushAsync());

        store.RecordError("Recovery", "later write succeeds");
        await store.FlushAsync();

        var clearFailure = await Assert.ThrowsAsync<IOException>(() => store.ClearAsync());
        Assert.Contains("Injected failure 3", clearFailure.Message);
        await Assert.ThrowsAsync<IOException>(() => store.FlushAsync());

        await store.ClearAsync();
        Assert.Empty(store.Errors);
    }

    [Fact]
    public async Task JsRuntimeDiagnostics_ConcurrentRecordsRemainBoundedAndPersistTheFinalState()
    {
        var writes = new ConcurrentQueue<PersistenceObservation>();
        var store = CreateStore("test://concurrent", (path, snapshot) =>
        {
            writes.Enqueue(new PersistenceObservation(path, snapshot.ToArray()));
            return Task.CompletedTask;
        });

        Parallel.For(0, 200, i => store.RecordError("Concurrent", $"Message {i}"));
        await store.FlushAsync();

        var errors = store.Errors;
        Assert.Equal(JsRuntimeDiagnostics.MaxRetainedErrors, errors.Count);
        Assert.Equal(100, errors.Select(error => error.Message).Distinct().Count());
        Assert.Equal(errors.Select(error => error.Message), writes.Last().Snapshot.Select(error => error.Message));
    }

    [Fact]
    public async Task JsRuntimeDiagnostics_ShutdownDrainIsBoundedAndBestEffort()
    {
        var lateFailure = NewSignal();

        Assert.False(await JsRuntimeDiagnostics.DrainForShutdownAsync(
            () => lateFailure.Task,
            TimeSpan.Zero));
        lateFailure.TrySetException(new IOException("Injected late shutdown failure"));

        Assert.False(await JsRuntimeDiagnostics.DrainForShutdownAsync(
            () => Task.FromException(new IOException("Injected shutdown failure")),
            TimeSpan.FromSeconds(1)));

        Assert.False(await JsRuntimeDiagnostics.DrainForShutdownAsync(
            () => Task.FromException(new IOException("Injected already-faulted failure with zero timeout")),
            TimeSpan.Zero));

        Assert.False(await JsRuntimeDiagnostics.DrainForShutdownAsync(
            () => throw new IOException("Injected synchronous flush failure"),
            TimeSpan.FromSeconds(1)));

        Assert.True(await JsRuntimeDiagnostics.DrainForShutdownAsync(
            () => Task.CompletedTask,
            TimeSpan.FromSeconds(1)));
    }

    private static JsRuntimeDiagnosticsStore CreateStore(
        string destination,
        Func<string, IReadOnlyList<CapturedJsError>, Task> persistAsync)
        => new(destination, persistAsync);

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record PersistenceObservation(
        string Destination,
        CapturedJsError[] Snapshot);
}

public sealed class WebViewRecoveryPolicyTests
{
    [Theory]
    [InlineData(WebViewFailureKind.Navigation, WebViewRecoveryAction.Reload)]
    [InlineData(WebViewFailureKind.Initialization, WebViewRecoveryAction.Recreate)]
    [InlineData(WebViewFailureKind.Process, WebViewRecoveryAction.Recreate)]
    public void EachFailureClassConsumesSharedBoundedBudget(
        WebViewFailureKind kind, WebViewRecoveryAction firstAction)
    {
        var policy = new WebViewRecoveryPolicy();
        Assert.Equal(firstAction, policy.RecordFailure(kind));
        Assert.Equal(WebViewRecoveryAction.Recreate, policy.RecordFailure(WebViewFailureKind.Process));
        Assert.Equal(WebViewRecoveryAction.ManualRetry, policy.RecordFailure(WebViewFailureKind.Initialization));
        Assert.Equal(WebViewRecoveryAction.ManualRetry, policy.RecordFailure(WebViewFailureKind.Navigation));
    }

    [Fact]
    public void SuccessAndManualRetryResetBudget()
    {
        var policy = new WebViewRecoveryPolicy(1);
        policy.RecordFailure(WebViewFailureKind.Navigation);
        Assert.Equal(WebViewRecoveryAction.ManualRetry, policy.RecordFailure(WebViewFailureKind.Process));
        policy.RecordSuccess();
        Assert.Equal(WebViewRecoveryAction.Recreate, policy.RecordFailure(WebViewFailureKind.Process));
        policy.ResetForManualRetry();
        Assert.Equal(WebViewRecoveryAction.Reload, policy.RecordFailure(WebViewFailureKind.Navigation));
    }

    [Fact]
    public void StaleSuccessCannotClearNewGenerationFailureExhaustion()
    {
        var policy = new WebViewRecoveryPolicy(1);
        long oldGeneration = policy.CurrentGeneration;
        long currentGeneration = policy.AdvanceGeneration();
        Assert.Equal(WebViewRecoveryAction.Recreate,
            policy.RecordFailureIfCurrent(currentGeneration, WebViewFailureKind.Process));
        Assert.False(policy.RecordSuccessIfCurrent(oldGeneration));
        Assert.Equal(WebViewRecoveryAction.ManualRetry,
            policy.RecordFailureIfCurrent(currentGeneration, WebViewFailureKind.Initialization));
    }

    [Fact]
    public void StaleFailureCannotConsumeNewGenerationRecoveryBudget()
    {
        var policy = new WebViewRecoveryPolicy(1);
        long oldGeneration = policy.CurrentGeneration;
        long currentGeneration = policy.AdvanceGeneration();
        Assert.Null(policy.RecordFailureIfCurrent(oldGeneration, WebViewFailureKind.Process));
        Assert.True(policy.RecordSuccessIfCurrent(currentGeneration));
        Assert.Equal(WebViewRecoveryAction.Reload,
            policy.RecordFailureIfCurrent(currentGeneration, WebViewFailureKind.Navigation));
    }
}

public sealed class WebViewRecoveryEventBindingTests
{
    [Fact]
    public void QueuedOldNavigationSuccessCannotHideCurrentFailureOverlayOrResetBudget()
    {
        var policy = new WebViewRecoveryPolicy(1);
        var oldSource = new FakeWebViewRecoveryEventSource();
        bool overlayVisible = true;
        long oldGeneration = policy.CurrentGeneration;
        using var oldBinding = new WebViewRecoveryEventBinding(oldSource, policy, oldGeneration,
            () => true, () => overlayVisible = false, (_, _, _) => Task.CompletedTask);
        var queuedOldSuccess = oldSource.CaptureNavigationCallback();

        long currentGeneration = policy.AdvanceGeneration();
        oldBinding.Dispose();
        var currentSource = new FakeWebViewRecoveryEventSource();
        using var currentBinding = new WebViewRecoveryEventBinding(currentSource, policy, currentGeneration,
            () => true, () => overlayVisible = false, (_, _, _) => Task.CompletedTask);
        Assert.Equal(WebViewRecoveryAction.Recreate,
            policy.RecordFailureIfCurrent(currentGeneration, WebViewFailureKind.Process));

        // A callback already queued by the old control can run after detachment.
        queuedOldSuccess(oldSource, new WebViewNavigationOutcomeEventArgs(true, "Success"));
        Assert.True(overlayVisible);
        Assert.Equal(WebViewRecoveryAction.ManualRetry,
            policy.RecordFailureIfCurrent(currentGeneration, WebViewFailureKind.Initialization));

        currentSource.RaiseNavigationSuccess();
        Assert.False(overlayVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueuedOldFailureCannotScheduleRecoveryAgainstHealthyControl(bool processFailure)
    {
        var policy = new WebViewRecoveryPolicy(1);
        var oldSource = new FakeWebViewRecoveryEventSource();
        int recoveries = 0;
        long oldGeneration = policy.CurrentGeneration;
        using var oldBinding = new WebViewRecoveryEventBinding(oldSource, policy, oldGeneration,
            () => true, () => { }, (_, _, _) => { recoveries++; return Task.CompletedTask; });
        var queuedNavigation = oldSource.CaptureNavigationCallback();
        var queuedProcess = oldSource.CaptureProcessCallback();

        long currentGeneration = policy.AdvanceGeneration();
        oldBinding.Dispose();
        var currentSource = new FakeWebViewRecoveryEventSource();
        using var currentBinding = new WebViewRecoveryEventBinding(currentSource, policy, currentGeneration,
            () => true, () => { }, (_, _, _) => { recoveries++; return Task.CompletedTask; });
        currentSource.RaiseNavigationSuccess();

        if (processFailure)
            queuedProcess(oldSource, new WebViewProcessFailureEventArgs("Old process exited"));
        else
            queuedNavigation(oldSource, new WebViewNavigationOutcomeEventArgs(false, "Old navigation failed"));
        Assert.Equal(0, recoveries);
        Assert.Equal(WebViewRecoveryAction.Reload,
            policy.RecordFailureIfCurrent(currentGeneration, WebViewFailureKind.Navigation));

        currentSource.RaiseProcessFailure();
        Assert.Equal(1, recoveries);
    }

    private sealed class FakeWebViewRecoveryEventSource : IWebViewRecoveryEventSource
    {
        public event EventHandler<WebViewNavigationOutcomeEventArgs>? NavigationCompleted;
        public event EventHandler<WebViewProcessFailureEventArgs>? ProcessFailed;

        public EventHandler<WebViewNavigationOutcomeEventArgs> CaptureNavigationCallback() =>
            NavigationCompleted ?? throw new InvalidOperationException("No navigation handler attached.");

        public EventHandler<WebViewProcessFailureEventArgs> CaptureProcessCallback() =>
            ProcessFailed ?? throw new InvalidOperationException("No process handler attached.");

        public void RaiseNavigationSuccess() =>
            NavigationCompleted?.Invoke(this, new WebViewNavigationOutcomeEventArgs(true, "Success"));

        public void RaiseProcessFailure() =>
            ProcessFailed?.Invoke(this, new WebViewProcessFailureEventArgs("Current process failed"));
    }
}
