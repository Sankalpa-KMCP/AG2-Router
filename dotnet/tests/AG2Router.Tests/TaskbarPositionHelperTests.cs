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

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void TaskbarPositionHelper_WithDpiScaling_CalculatesBoundsWithinScreenWorkArea(double dpiScale)
    {
        double scaledWidth = 320 * dpiScale;
        double scaledHeight = 280 * dpiScale;

        var pos = TaskbarPositionHelper.CalculateFlyoutPosition(scaledWidth, scaledHeight);
        var screen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        var workArea = screen.WorkingArea;

        Assert.True(pos.X >= workArea.Left, $"X ({pos.X}) must be >= workArea.Left ({workArea.Left})");
        Assert.True(pos.X + scaledWidth <= workArea.Right + 16, $"X + scaledWidth must be within workArea.Right");
        Assert.True(pos.Y >= workArea.Top, $"Y ({pos.Y}) must be >= workArea.Top ({workArea.Top})");
        Assert.True(pos.Y + scaledHeight <= workArea.Bottom + 16, $"Y + scaledHeight must be within workArea.Bottom");
    }

    [Fact]
    public void TaskbarPositionHelper_GetMonitorDpiScale_ReturnsPositiveScaleFactors()
    {
        var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        var pt = new System.Drawing.Point(primaryScreen.Bounds.X + 10, primaryScreen.Bounds.Y + 10);

        var (scaleX, scaleY) = TaskbarPositionHelper.GetMonitorDpiScale(pt);

        Assert.True(scaleX >= 1.0, $"scaleX ({scaleX}) should be >= 1.0");
        Assert.True(scaleY >= 1.0, $"scaleY ({scaleY}) should be >= 1.0");
    }
}
