using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

[CollectionDefinition("SingleInstanceGuard", DisableParallelization = true)]
public class SingleInstanceGuardTestCollection { }

[Collection("SingleInstanceGuard")]
public class SingleInstanceGuardTests
{
    [Fact]
    public async Task SingleInstanceGuard_AcquiresPrimary_AndDetectsSecondary()
    {
        await using var primary = new SingleInstanceGuard();
        var activationReceivedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool primaryAcquired = primary.TryAcquire(() =>
        {
            activationReceivedTcs.TrySetResult(true);
        });

        Assert.True(primaryAcquired, "First instance must successfully acquire the session guard");

        // Attempt secondary instance acquisition immediately in the same user session (no artificial sleep)
        await using var secondary = new SingleInstanceGuard();
        bool secondaryAcquired = secondary.TryAcquire(() => { });

        Assert.False(secondaryAcquired, "Secondary instance must be rejected");

        // Await deterministic signal propagation with bounded timeout (5 seconds)
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completedTask = await Task.WhenAny(activationReceivedTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

        Assert.True(completedTask == activationReceivedTcs.Task, "Primary instance should have received the activation signal within bounded timeout window");
        Assert.True(await activationReceivedTcs.Task, "Signal value must be true");
    }

    [Fact]
    public async Task SingleInstanceGuard_RoutesCommandsDeterministically_OverNamedPipe()
    {
        await using var primary = new SingleInstanceGuard();
        var closeReceivedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitReceivedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool primaryAcquired = primary.TryAcquire(
            onActivateRequested: () => { },
            onCloseRequested: () => closeReceivedTcs.TrySetResult(true),
            onExitRequested: () => exitReceivedTcs.TrySetResult(true)
        );

        Assert.True(primaryAcquired, "First instance must successfully acquire the session guard");

        // Send CLOSE command
        bool closeSent = SingleInstanceGuard.SendCommand("CLOSE");
        Assert.True(closeSent, "CLOSE command should be delivered over named pipe");

        using var closeTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var closeResult = await Task.WhenAny(closeReceivedTcs.Task, Task.Delay(Timeout.Infinite, closeTimeoutCts.Token));
        Assert.True(closeResult == closeReceivedTcs.Task, "Primary instance should have received CLOSE command within bounded timeout");

        // Send EXIT command
        bool exitSent = SingleInstanceGuard.SendCommand("EXIT");
        Assert.True(exitSent, "EXIT command should be delivered over named pipe");

        using var exitTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exitResult = await Task.WhenAny(exitReceivedTcs.Task, Task.Delay(Timeout.Infinite, exitTimeoutCts.Token));
        Assert.True(exitResult == exitReceivedTcs.Task, "Primary instance should have received EXIT command within bounded timeout");
    }

    [Fact]
    public async Task SingleInstanceGuard_RecyclesListener_ForMultipleSequentialCommands()
    {
        await using var primary = new SingleInstanceGuard();
        var activateCount = 0;
        var closeCount = 0;
        var exitCount = 0;
        var allDoneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        bool primaryAcquired = primary.TryAcquire(
            onActivateRequested: () =>
            {
                Interlocked.Increment(ref activateCount);
                if (Volatile.Read(ref activateCount) == 2 && Volatile.Read(ref closeCount) == 1 && Volatile.Read(ref exitCount) == 1)
                {
                    allDoneTcs.TrySetResult(true);
                }
            },
            onCloseRequested: () =>
            {
                Interlocked.Increment(ref closeCount);
                if (Volatile.Read(ref activateCount) == 2 && Volatile.Read(ref closeCount) == 1 && Volatile.Read(ref exitCount) == 1)
                {
                    allDoneTcs.TrySetResult(true);
                }
            },
            onExitRequested: () =>
            {
                Interlocked.Increment(ref exitCount);
                if (Volatile.Read(ref activateCount) == 2 && Volatile.Read(ref closeCount) == 1 && Volatile.Read(ref exitCount) == 1)
                {
                    allDoneTcs.TrySetResult(true);
                }
            }
        );

        Assert.True(primaryAcquired, "First instance must successfully acquire the session guard");

        // Send sequential commands back-to-back: ACTIVATE, CLOSE, EXIT, ACTIVATE
        Assert.True(SingleInstanceGuard.SendCommand("ACTIVATE"), "First ACTIVATE should succeed");
        Assert.True(SingleInstanceGuard.SendCommand("CLOSE"), "CLOSE should succeed");
        Assert.True(SingleInstanceGuard.SendCommand("EXIT"), "EXIT should succeed");
        Assert.True(SingleInstanceGuard.SendCommand("ACTIVATE"), "Second ACTIVATE should succeed");

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await Task.WhenAny(allDoneTcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));

        Assert.True(result == allDoneTcs.Task, "All 4 sequential commands must be delivered and processed within bounded timeout");
        Assert.Equal(2, Volatile.Read(ref activateCount));
        Assert.Equal(1, Volatile.Read(ref closeCount));
        Assert.Equal(1, Volatile.Read(ref exitCount));
    }

    [Fact]
    public async Task SingleInstanceGuard_DisposesCleanly_AndAllowsNewPrimaryToAcquire()
    {
        var primary1 = new SingleInstanceGuard();
        bool acquired1 = primary1.TryAcquire(() => { });
        Assert.True(acquired1, "Primary 1 must acquire");

        // Secondary should fail while Primary 1 is active
        var secondary1 = new SingleInstanceGuard();
        bool secondaryAcquired = secondary1.TryAcquire(() => { });
        Assert.False(secondaryAcquired, "Secondary must fail while Primary 1 active");
        await secondary1.DisposeAsync();

        // Dispose Primary 1
        await primary1.DisposeAsync();

        // Now a new Primary 2 should be able to acquire cleanly without collisions
        await using var primary2 = new SingleInstanceGuard();
        var activate2Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool acquired2 = primary2.TryAcquire(() => activate2Tcs.TrySetResult(true));
        Assert.True(acquired2, "Primary 2 must successfully acquire after Primary 1 disposed");

        // Verify IPC still works on the new instance
        Assert.True(SingleInstanceGuard.SendCommand("ACTIVATE"), "ACTIVATE on Primary 2 should succeed");
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await Task.WhenAny(activate2Tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));
        Assert.True(result == activate2Tcs.Task, "Primary 2 should receive activation signal");
    }
}
