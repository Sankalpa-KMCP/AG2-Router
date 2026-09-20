using System.Drawing;

namespace AG2Router.Windows.Tray;

/// <summary>
/// Calculates optimal positioning for quick-status popup windows relative to cursor, taskbar,
/// and display working area, clamped cleanly inside the active monitor bounds.
/// </summary>
public static class TaskbarPositionHelper
{
    public static Point CalculateFlyoutPosition(double flyoutWidth, double flyoutHeight)
    {
        // 1. Get current mouse position (where user clicked tray icon)
        var mousePos = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(mousePos);
        var workArea = screen.WorkingArea;

        int width = (int)Math.Ceiling(flyoutWidth);
        int height = (int)Math.Ceiling(flyoutHeight);

        // 2. Center horizontally on cursor, then clamp to working area with 8px margin
        int x = mousePos.X - (width / 2);
        x = Math.Max(workArea.Left + 8, Math.Min(x, workArea.Right - width - 8));

        // 3. Determine vertical placement based on taskbar location
        int y;
        if (workArea.Bottom < screen.Bounds.Bottom)
        {
            // Taskbar is at the bottom (standard Windows 10/11)
            y = workArea.Bottom - height - 8;
        }
        else if (workArea.Top > screen.Bounds.Top)
        {
            // Taskbar is at the top
            y = workArea.Top + 8;
        }
        else if (workArea.Right < screen.Bounds.Right)
        {
            // Taskbar is on the right
            y = mousePos.Y - (height / 2);
            x = workArea.Right - width - 8;
        }
        else if (workArea.Left > screen.Bounds.Left)
        {
            // Taskbar is on the left
            y = mousePos.Y - (height / 2);
            x = workArea.Left + 8;
        }
        else
        {
            // Fallback: bottom-right corner of work area
            x = workArea.Right - width - 8;
            y = workArea.Bottom - height - 8;
        }

        // Clamp Y safely within working area
        y = Math.Max(workArea.Top + 8, Math.Min(y, workArea.Bottom - height - 8));

        return new Point(x, y);
    }
}
