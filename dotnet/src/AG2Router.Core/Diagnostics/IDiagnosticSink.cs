namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Universal interface for diagnostic event emission, bounded flushing, health accounting,
/// and recent event inspection. Implementations must never throw exceptions or block
/// critical domain operations.
/// </summary>
public interface IDiagnosticSink
{
    /// <summary>
    /// Non-blocking, best-effort emission of a sanitized diagnostic event.
    /// Drops or suppresses events under memory or queue capacity limits.
    /// </summary>
    void Emit(DiagnosticEvent @event);

    /// <summary>
    /// Flushes pending queue operations to persistent storage within the specified timeout.
    /// Returns true if all pending events were persisted, or false if timed out or failed.
    /// </summary>
    Task<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves current operational health and accounting metrics.
    /// </summary>
    DiagnosticSinkHealth GetHealth();

    /// <summary>
    /// Retrieves a snapshot of the in-memory recent sanitized event buffer.
    /// </summary>
    IReadOnlyList<DiagnosticEvent> GetRecentEvents();

    /// <summary>
    /// Checks whether the given severity is enabled.
    /// </summary>
    bool IsEnabled(DiagnosticSeverity severity);
}
