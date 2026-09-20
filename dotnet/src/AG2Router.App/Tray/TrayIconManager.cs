using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AG2Router.App.Tray;

/// <summary>
/// Manages the Windows notification area (system tray) icon using built-in System.Windows.Forms.NotifyIcon.
/// Follows standard Windows shell conventions:
/// - Single left click toggles quick-status flyout.
/// - Double left click opens dashboard.
/// - Right click displays context menu.
/// - Clean disposal prevents ghost icons on shutdown.
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _onToggleQuickStatus;
    private readonly Action _onOpenDashboard;
    private readonly Action _onExitRequested;
    private Icon? _generatedIcon;

    public TrayIconManager(
        Action onToggleQuickStatus,
        Action onOpenDashboard,
        Action onExitRequested)
    {
        _onToggleQuickStatus = onToggleQuickStatus;
        _onOpenDashboard = onOpenDashboard;
        _onExitRequested = onExitRequested;

        _generatedIcon = CreateModernTrayIcon();

        var contextMenu = new ContextMenuStrip();
        
        var quickStatusItem = new ToolStripMenuItem("&Quick Status", null, (s, e) => _onToggleQuickStatus());
        var openDashboardItem = new ToolStripMenuItem("&Open Dashboard", null, (s, e) => _onOpenDashboard())
        {
            Font = new Font(contextMenu.Font, System.Drawing.FontStyle.Bold)
        };
        var separator = new ToolStripSeparator();
        var exitItem = new ToolStripMenuItem("E&xit AG2 Router", null, (s, e) => _onExitRequested());

        contextMenu.Items.Add(quickStatusItem);
        contextMenu.Items.Add(openDashboardItem);
        contextMenu.Items.Add(separator);
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _generatedIcon,
            Text = "AG2 Router - Antigravity: Waiting",
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _onToggleQuickStatus();
        }
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        _onOpenDashboard();
    }

    public void UpdateTooltip(string text)
    {
        // NOTIFYICONDATA text length limit is 127 chars
        if (text.Length > 63) text = text[..60] + "...";
        _notifyIcon.Text = text;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon CreateModernTrayIcon()
    {
        // Generate a crisp, modern vector-drawn tray icon (32x32) with dark background and blue accent ring
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Outer circular shield
        using var bgBrush = new SolidBrush(Color.FromArgb(22, 25, 32)); // #161920
        g.FillEllipse(bgBrush, 1, 1, 30, 30);

        // Accent border (blue #3b82f6)
        using var borderPen = new Pen(Color.FromArgb(59, 130, 246), 2f);
        g.DrawEllipse(borderPen, 2, 2, 28, 28);

        // Inner monogram "A" or chevron
        using var textBrush = new SolidBrush(Color.FromArgb(240, 242, 245));
        using var font = new Font("Segoe UI", 12f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString("A", font, textBrush, new RectangleF(0, 0, 32, 32), sf);

        var hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
            {
                DestroyIcon(hIcon);
            }
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        if (_generatedIcon != null)
        {
            _generatedIcon.Dispose();
            _generatedIcon = null;
        }
    }
}
