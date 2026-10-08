using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AG2Router.Core.Models;
using AG2Router.Windows.Tray;

namespace AG2Router.App.Views;

public partial class QuickStatusWindow : Window
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private readonly Action _onOpenDashboard;
    private readonly Func<SystemStatusDto>? _statusProvider;

    public QuickStatusWindow(Action onOpenDashboard, Func<SystemStatusDto>? statusProvider = null)
    {
        InitializeComponent();
        _onOpenDashboard = onOpenDashboard;
        _statusProvider = statusProvider;
    }

    public void UpdateStatus(SystemStatusDto? status)
    {
        if (status == null) return;

        if (status.Ag2.Connected)
        {
            TxtStatus.Text = status.Ag2.Activity?.State == "BUSY" ? "Busy" : "Connected";
            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
        }
        else
        {
            TxtStatus.Text = status.Ag2.Status == "DEGRADED" ? "Degraded" : "Not connected";
            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 242, 245));
        }

        TxtAccount.Text = status.Telemetry?.CurrentAccount?.Email ?? "—";

        var (quotaLabel, quotaValue, quotaToolTip) = ModelQuotaFormatter.FormatModelQuota(status.Telemetry?.Quota?.Models);
        LblQuota.Text = quotaLabel;
        TxtQuota.Text = quotaValue;
        TxtQuota.ToolTip = quotaToolTip;

        var (autoText, autoTip) = FormatAutoSwitchStatus(status.Router.AutoSwitchEnabled, status.Router.PoolStatus);
        TxtAutoSwitch.Text = autoText;
        TxtAutoSwitch.ToolTip = autoTip;
    }

    internal static (string Text, string? ToolTip) FormatAutoSwitchStatus(bool autoSwitchEnabled, CandidatePoolStatusDto? poolStatus)
    {
        if (!autoSwitchEnabled)
        {
            return ("Disabled", null);
        }
        if (poolStatus != null && !poolStatus.HasUsableCandidate)
        {
            string text = poolStatus.ReasonCode switch
            {
                CandidatePoolReasonCodes.AllExhausted or CandidatePoolReasonCodes.QuotaDepleted => "Pool Exhausted",
                CandidatePoolReasonCodes.AllInCooldown => "In Cooldown",
                CandidatePoolReasonCodes.EvidenceStaleOrUnknown => "No Fresh Evidence",
                CandidatePoolReasonCodes.ReserveOnly => "Reserve Only",
                CandidatePoolReasonCodes.ValidationOrSessionFailed => "Invalid Sessions",
                CandidatePoolReasonCodes.NoEnrolledAlternatives => "No Alternatives",
                CandidatePoolReasonCodes.AllBelowMinimum => "Below Minimum",
                _ => "No Candidates"
            };
            return (text, poolStatus.Message);
        }
        return ("Active", null);
    }

    public void ShowNearTray()
    {
        if (_statusProvider != null)
        {
            UpdateStatus(_statusProvider());
        }

        var mousePos = System.Windows.Forms.Cursor.Position;
        var (scaleX, scaleY) = TaskbarPositionHelper.GetMonitorDpiScale(mousePos);
        if (scaleX <= 0) scaleX = 1.0;
        if (scaleY <= 0) scaleY = 1.0;

        int physWidth = (int)Math.Ceiling(Width * scaleX);
        int physHeight = (int)Math.Ceiling(Height * scaleY);

        var pos = TaskbarPositionHelper.CalculateFlyoutPosition(physWidth, physHeight);

        var helper = new WindowInteropHelper(this);
        var hwnd = helper.EnsureHandle();

        SetWindowPos(hwnd, IntPtr.Zero, pos.X, pos.Y, physWidth, physHeight,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        Left = pos.X / scaleX;
        Top = pos.Y / scaleY;

        Show();
        Activate();
        BtnOpenDashboard.Focus();
    }

    private void BtnOpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _onOpenDashboard();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Hide();
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Hide();
    }
}

public static class ModelQuotaFormatter
{
    public static (string Label, string Value, string? ToolTip) FormatModelQuota(IReadOnlyList<ModelQuotaDto>? models)
    {
        if (models is { Count: > 0 })
        {
            var first = models[0];
            var label = string.IsNullOrWhiteSpace(first.Label) ? "Model Quota" : first.Label;
            static string FormatPercent(double? fraction) =>
                fraction is double f && double.IsFinite(f)
                    ? $"{Math.Round(Math.Clamp(f, 0.0, 1.0) * 100)}%"
                    : "—%";

            var value = FormatPercent(first.RemainingFraction);
            var tooltip = models.Count > 1
                ? string.Join(Environment.NewLine, models.Select(m => $"{m.Label}: {FormatPercent(m.RemainingFraction)}"))
                : null;
            return (label, value, tooltip);
        }

        return ("Model Quota", "—%", null);
    }
}
