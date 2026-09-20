using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

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

        // Attempt secondary instance acquisition in the same user session
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
}
