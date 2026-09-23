using System.Runtime.InteropServices;
using AG2Router.App.Services;
using Xunit;

namespace AG2Router.Tests;

public class MockTestEnvironment
{
    public string Id { get; } = Guid.NewGuid().ToString();
}

public class WebView2EnvironmentCoordinatorTests
{
    [Fact]
    public async Task NeverCompletingEnsureOperationExitsAtDeadline()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<TimeoutException>(() => WebViewAttemptDeadline.RunAsync(
            () => never.Task, TimeSpan.FromMilliseconds(25)).WaitAsync(TimeSpan.FromSeconds(2)));
        never.TrySetResult();
    }

    [Fact]
    public async Task NeverCompletingFactoryConsumesBoundedAttemptsAndReachesFailure()
    {
        int attempts = 0;
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: _ => {
                Interlocked.Increment(ref attempts);
                return new TaskCompletionSource<MockTestEnvironment>().Task;
            },
            subscribeProcessExited: (_, _) => { },
            maxRetries: 2,
            creationTimeout: TimeSpan.FromMilliseconds(25),
            delayFunc: (_, _) => Task.CompletedTask);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.GetOrCreateEnvironmentAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task GetOrCreateEnvironment_FirstCall_CreatesEnvironment()
    {
        int factoryCalls = 0;
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: _ =>
            {
                factoryCalls++;
                return Task.FromResult(new MockTestEnvironment());
            },
            subscribeProcessExited: (_, _) => { }
        );

        var env = await coordinator.GetOrCreateEnvironmentAsync();

        Assert.NotNull(env);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task GetOrCreateEnvironment_MultipleCalls_ReusesSameInstanceWithoutDuplicateCreation()
    {
        int factoryCalls = 0;
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: _ =>
            {
                factoryCalls++;
                return Task.FromResult(new MockTestEnvironment());
            },
            subscribeProcessExited: (_, _) => { }
        );

        var env1 = await coordinator.GetOrCreateEnvironmentAsync();
        var env2 = await coordinator.GetOrCreateEnvironmentAsync();

        Assert.Same(env1, env2);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public async Task ConcurrentCalls_SerializeAndCreateOnlyOnce()
    {
        int factoryCalls = 0;
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: async _ =>
            {
                Interlocked.Increment(ref factoryCalls);
                await Task.Delay(25);
                return new MockTestEnvironment();
            },
            subscribeProcessExited: (_, _) => { }
        );

        var tasks = Enumerable.Range(0, 10).Select(_ => coordinator.GetOrCreateEnvironmentAsync());
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, factoryCalls);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task ReopenWhileExitPending_AwaitsExitEventAndDoesNotDeadlock()
    {
        Action? exitHook = null;
        int factoryCalls = 0;
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: _ =>
            {
                factoryCalls++;
                return Task.FromResult(new MockTestEnvironment());
            },
            subscribeProcessExited: (_, handler) => exitHook = handler
        );

        var env1 = await coordinator.GetOrCreateEnvironmentAsync();
        coordinator.NotifyWindowClosing();

        // Start reopen while previous exit has not fired
        var reopenTask = coordinator.GetOrCreateEnvironmentAsync();
        Assert.False(reopenTask.IsCompleted);

        // Fire exit event from another thread (simulating Chromium exit)
        exitHook?.Invoke();

        var env2 = await reopenTask;
        Assert.NotSame(env1, env2);
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public async Task TimeoutWithoutProcessKill_ProceedsToCreationSafely()
    {
        int factoryCalls = 0;
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: _ =>
            {
                factoryCalls++;
                return Task.FromResult(new MockTestEnvironment());
            },
            subscribeProcessExited: (_, _) => { /* never fire */ },
            exitWaitTimeout: TimeSpan.FromMilliseconds(50) // fast test timeout
        );

        var env1 = await coordinator.GetOrCreateEnvironmentAsync();
        coordinator.NotifyWindowClosing(12345);

        // Exit event never fires; wait should time out after 50ms and proceed without throwing or killing
        var env2 = await coordinator.GetOrCreateEnvironmentAsync();
        Assert.NotNull(env2);
        Assert.NotSame(env1, env2);
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public async Task LockContention_RetriesWithBackoffAndSucceeds()
    {
        int attempts = 0;
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new COMException("Locked", unchecked((int)0x8007139F));
                }
                return Task.FromResult(new MockTestEnvironment());
            },
            subscribeProcessExited: (_, _) => { },
            isLockContentionException: ex => ex is COMException c && c.HResult == unchecked((int)0x8007139F),
            delayFunc: (_, _) => Task.CompletedTask // zero delay in unit tests
        );

        var env = await coordinator.GetOrCreateEnvironmentAsync();

        Assert.NotNull(env);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task LockContention_ExhaustsRetries_ThrowsWithInnerException()
    {
        int attempts = 0;
        var expectedComException = new COMException("Locked", unchecked((int)0x8007139F));
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: _ =>
            {
                attempts++;
                throw expectedComException;
            },
            subscribeProcessExited: (_, _) => { },
            isLockContentionException: ex => ex is COMException c && c.HResult == unchecked((int)0x8007139F),
            delayFunc: (_, _) => Task.CompletedTask,
            maxRetries: 3
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.GetOrCreateEnvironmentAsync()
        );

        Assert.Equal(3, attempts);
        Assert.Same(expectedComException, ex.InnerException);
    }

    [Fact]
    public async Task LateExitEvent_DoesNotWipeNewEnvironment()
    {
        Action? firstExitHook = null;
        int factoryCount = 0;
        var coordinator = new WebView2EnvironmentCoordinatorCore<MockTestEnvironment>(
            environmentFactory: _ => Task.FromResult(new MockTestEnvironment()),
            subscribeProcessExited: (_, h) =>
            {
                if (factoryCount++ == 0)
                {
                    firstExitHook = h;
                }
            },
            exitWaitTimeout: TimeSpan.FromMilliseconds(10)
        );

        var env1 = await coordinator.GetOrCreateEnvironmentAsync();
        coordinator.NotifyWindowClosing();

        // Reopen times out because firstExitHook was not invoked yet
        var env2 = await coordinator.GetOrCreateEnvironmentAsync();
        Assert.NotSame(env1, env2);

        // Now late exit event from env1 fires
        firstExitHook?.Invoke();

        // Third call should reuse env2, NOT recreate because env1 must not have nulled out env2
        var env3 = await coordinator.GetOrCreateEnvironmentAsync();
        Assert.Same(env2, env3);
    }
}
