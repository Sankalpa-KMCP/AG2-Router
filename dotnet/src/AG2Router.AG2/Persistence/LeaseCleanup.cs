namespace AG2Router.AG2.Persistence;

/// <summary>Captures cleanup errors so callers can release ownership and preserve their domain outcome.</summary>
internal static class LeaseCleanup
{
    public static async Task<Exception?> TryDisposeAsync(IAsyncDisposable? lease, Exception? earlierError = null)
    {
        if (lease == null) return earlierError;
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            return earlierError == null ? ex : new AggregateException(earlierError, ex);
        }
        return earlierError;
    }

    public static void PreservePrimaryFailure(Exception? primaryError, Exception cleanupError)
    {
        if (primaryError == null) return;
        if (primaryError is OperationCanceledException)
        {
            // Keep the original cancellation exception/token while retaining the secondary failure.
            primaryError.Data["LeaseCleanupFailure"] = cleanupError;
            return;
        }
        throw new AggregateException("Operation failed and lease cleanup also failed.", primaryError, cleanupError);
    }
}
