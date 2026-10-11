namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Snapshot of diagnostic sink operational health and bounded accounting counters (R5, R6).
/// </summary>
public sealed record DiagnosticSinkHealth(
    bool IsHealthy,
    long TotalEmitted,
    long TotalPersisted,
    long TotalSuppressed,
    long TotalDropped,
    string? LastError,
    int OwnedSegmentCount = 0,
    long OwnedTotalSizeBytes = 0,
    bool IsWriterLeaseHeld = true,
    int RetentionFailureCount = 0
);
