using AG2Router.Core.Models;

namespace AG2Router.Core.Contracts;

/// <summary>Outcome of one idempotent batch ingestion into the usage ledger.</summary>
/// <param name="InsertedCount">Calls persisted for the first time in this batch.</param>
/// <param name="DuplicateCount">
/// Calls whose key was already present with an identical accounting payload. Duplicates leave
/// durable bytes untouched; observation metadata of the first persisted record always wins.
/// </param>
public sealed record UsageIngestResult(int InsertedCount, int DuplicateCount);

/// <summary>
/// Durable ledger of observed conversation-model usage calls. One record represents one
/// validated conversation-call accounting unit — not all Antigravity usage. All Accounts
/// totals include unattributed records, so per-account totals may legitimately sum to less
/// than the global total; that remainder must remain representable.
/// </summary>
/// <remarks>
/// Implementations must be idempotent per call key, must never overwrite conflicting
/// accounting data (first-persisted accounting wins), must fail closed on corrupt or
/// unsupported storage, and must not silently assign historical calls to the currently
/// active account or to an inferred event date.
/// </remarks>
public interface IUsageCallLedger
{
    string LedgerDirectoryPath { get; }

    /// <summary>
    /// Persists a batch of calls atomically as one logical unit: either every record in the
    /// batch is accounted for (inserted or recognized duplicate) or the ledger is left
    /// unchanged. Conflicting accounting payloads for an already-persisted call key fail the
    /// whole batch without altering the prior durable record.
    /// </summary>
    Task<UsageIngestResult> IngestBatchAsync(IReadOnlyList<UsageCallRecord> calls, CancellationToken cancellationToken = default);

    /// <summary>All persisted usage calls, across every segment, in stable segment order.</summary>
    Task<IReadOnlyList<UsageCallRecord>> GetAllCallsAsync(CancellationToken cancellationToken = default);

    /// <summary>Usage calls defensibly attributed to one managed account id.</summary>
    Task<IReadOnlyList<UsageCallRecord>> GetCallsByAccountAsync(string accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Usage calls with no defensible account attribution. These are the explicit remainder
    /// between All Accounts totals and the sum of per-account totals.
    /// </summary>
    Task<IReadOnlyList<UsageCallRecord>> GetUnattributedCallsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls eligible for observation-time trends: records with
    /// <see cref="UsageTimeAttribution.ObservationTime"/> whose first-observation instant falls
    /// in <paramref name="fromUtcInclusive"/> (inclusive) to <paramref name="toUtcExclusive"/>
    /// (exclusive). HistoricalUnknown records are excluded because their event time is unknown.
    /// </summary>
    Task<IReadOnlyList<UsageCallRecord>> GetObservationTimeCallsBetweenAsync(
        DateTimeOffset fromUtcInclusive, DateTimeOffset toUtcExclusive, CancellationToken cancellationToken = default);

    /// <summary>Usage calls recorded under one canonical response model key (null is unknown, not a filter).</summary>
    Task<IReadOnlyList<UsageCallRecord>> GetCallsByModelAsync(string responseModelKey, CancellationToken cancellationToken = default);
}
