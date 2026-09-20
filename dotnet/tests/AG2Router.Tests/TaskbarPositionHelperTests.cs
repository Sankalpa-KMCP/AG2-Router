using AG2Router.Windows.Tray;
using Xunit;

namespace AG2Router.Tests;

public class TaskbarPositionHelperTests
{
    [Fact]
    public void TaskbarPositionHelper_CalculatesBoundsWithinScreenWorkArea()
    {
        var pos = TaskbarPositionHelper.CalculateFlyoutPosition(320, 280);
        var screen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        var workArea = screen.WorkingArea;

        // Coordinates must reside within or on the boundary of the working area
        Assert.True(pos.X >= workArea.Left, $"X ({pos.X}) must be >= workArea.Left ({workArea.Left})");
        Assert.True(pos.X + 320 <= workArea.Right + 16, $"X + Width must be within workArea.Right");
        Assert.True(pos.Y >= workArea.Top, $"Y ({pos.Y}) must be >= workArea.Top ({workArea.Top})");
        Assert.True(pos.Y + 280 <= workArea.Bottom + 16, $"Y + Height must be within workArea.Bottom");
    }
}
