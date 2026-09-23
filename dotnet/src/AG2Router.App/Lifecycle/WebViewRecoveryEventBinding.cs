namespace AG2Router.App.Lifecycle;

public sealed class WebViewNavigationOutcomeEventArgs(bool isSuccess, string detail) : EventArgs
{
    public bool IsSuccess { get; } = isSuccess;
    public string Detail { get; } = detail;
}

public sealed class WebViewProcessFailureEventArgs(string detail) : EventArgs
{
    public string Detail { get; } = detail;
}

public interface IWebViewRecoveryEventSource
{
    event EventHandler<WebViewNavigationOutcomeEventArgs>? NavigationCompleted;
    event EventHandler<WebViewProcessFailureEventArgs>? ProcessFailed;
}

// Owns event-to-recovery dispatch, including callbacks already queued when the
// old event source is detached. No WebView2 instance is needed to test it.
public sealed class WebViewRecoveryEventBinding : IDisposable
{
    private readonly IWebViewRecoveryEventSource _source;
    private readonly WebViewRecoveryPolicy _policy;
    private readonly long _generation;
    private readonly Func<bool> _isCurrentSource;
    private readonly Action _onSuccess;
    private readonly Func<WebViewFailureKind, string, long, Task> _onFailure;
    private bool _disposed;

    public WebViewRecoveryEventBinding(
        IWebViewRecoveryEventSource source, WebViewRecoveryPolicy policy, long generation,
        Func<bool> isCurrentSource, Action onSuccess,
        Func<WebViewFailureKind, string, long, Task> onFailure)
    {
        _source = source;
        _policy = policy;
        _generation = generation;
        _isCurrentSource = isCurrentSource;
        _onSuccess = onSuccess;
        _onFailure = onFailure;
        source.NavigationCompleted += OnNavigationCompleted;
        source.ProcessFailed += OnProcessFailed;
    }

    private bool IsCurrent() => !_disposed && _policy.IsCurrent(_generation) && _isCurrentSource();

    private async void OnNavigationCompleted(object? sender, WebViewNavigationOutcomeEventArgs e)
    {
        if (!IsCurrent()) return;
        if (e.IsSuccess)
        {
            if (_policy.RecordSuccessIfCurrent(_generation) && IsCurrent()) _onSuccess();
        }
        else
        {
            await _onFailure(WebViewFailureKind.Navigation, e.Detail, _generation);
        }
    }

    private async void OnProcessFailed(object? sender, WebViewProcessFailureEventArgs e)
    {
        if (!IsCurrent()) return;
        await _onFailure(WebViewFailureKind.Process, e.Detail, _generation);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.NavigationCompleted -= OnNavigationCompleted;
        _source.ProcessFailed -= OnProcessFailed;
    }
}
