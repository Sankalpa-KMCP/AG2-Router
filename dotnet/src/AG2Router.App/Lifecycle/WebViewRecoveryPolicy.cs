namespace AG2Router.App.Lifecycle;

public enum WebViewFailureKind
{
    Navigation,
    Initialization,
    Process
}

public enum WebViewRecoveryAction
{
    Reload,
    Recreate,
    ManualRetry
}

public sealed class WebViewRecoveryPolicy
{
    private readonly int _maxBudget;
    private int _consecutiveFailures;
    private long _generation;
    private readonly object _lock = new();

    public WebViewRecoveryPolicy(int maxBudget = 2)
    {
        _maxBudget = Math.Max(1, maxBudget);
    }

    public WebViewRecoveryAction RecordFailure(WebViewFailureKind kind)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures > _maxBudget)
            {
                return WebViewRecoveryAction.ManualRetry;
            }

            return kind switch
            {
                WebViewFailureKind.Navigation => WebViewRecoveryAction.Reload,
                _ => WebViewRecoveryAction.Recreate
            };
        }
    }

    public long CurrentGeneration { get { lock (_lock) return _generation; } }

    public long AdvanceGeneration()
    {
        lock (_lock) return ++_generation;
    }

    public bool IsCurrent(long generation)
    {
        lock (_lock) return generation == _generation;
    }

    public WebViewRecoveryAction? RecordFailureIfCurrent(long generation, WebViewFailureKind kind)
    {
        lock (_lock)
            return generation == _generation ? RecordFailure(kind) : null;
    }

    public bool RecordSuccessIfCurrent(long generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return false;
            _consecutiveFailures = 0;
            return true;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }
    }

    public void ResetForManualRetry()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
        }
    }
}
