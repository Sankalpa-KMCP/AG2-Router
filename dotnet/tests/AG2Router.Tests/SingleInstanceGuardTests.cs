using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public async Task SingleInstanceGuard_AcquiresPrimary_AndDetectsSecondary()
    {
        await using var primary = new SingleInstanceGuard();
        bool activatedSignalReceived = false;

        bool primaryAcquired = primary.TryAcquire(() =>
        {
            activatedSignalReceived = true;
        });

        Assert.True(primaryAcquired, "First instance must successfully acquire the session guard");

        // Attempt secondary instance acquisition in the same user session
        await using var secondary = new SingleInstanceGuard();
        bool secondaryAcquired = secondary.TryAcquire(() => { });

        Assert.False(secondaryAcquired, "Secondary instance must be rejected");

        // Wait briefly for named pipe signal to propagate to primary
        await Task.Delay(200);
        Assert.True(activatedSignalReceived, "Primary instance should have received the activation signal");
    }
}
