using AG2Router.Core.Models;

namespace AG2Router.Core.Contracts;

public interface IQuotaObservationStore
{
    Task<IReadOnlyList<AccountModelQuotaObservation>> GetAllObservationsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountModelQuotaObservation>> GetObservationsForAccountAsync(string accountId, CancellationToken cancellationToken = default);
    Task<AccountModelQuotaObservation?> GetObservationAsync(string accountId, string modelKey, CancellationToken cancellationToken = default);
    Task RecordObservationsAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations, CancellationToken cancellationToken = default);

    // A coherent complete snapshot also makes previously known, absent models unknown.
    // Implementations must rebase under write ownership and preserve newer observations.
    Task RecordCompleteSnapshotAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations,
        DateTimeOffset observedAtUtc, string source, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Complete quota snapshot persistence is unavailable.");

    // Production stores must compare and invalidate atomically. Unsupported stores
    // fail closed rather than acknowledging an invalidation they did not persist.
    Task InvalidateIfUnchangedAsync(AccountModelQuotaObservation expected, DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken = default) => throw new NotSupportedException("Conditional quota invalidation is unavailable.");

    // Invalidates all cached quota observations for the specified account.
    // Production stores must persist the invalidation atomically so unverified targets
    // cannot be re-selected using stale positive evidence after switch rollback.
    // Durable production implementations must override this method: account-wide
    // invalidation is a safety-critical recovery step and must rebase under write
    // ownership and land as ONE atomic durable replacement. This default performs a
    // sequential per-row conditional invalidation, which is safe for in-memory stores
    // but cannot guarantee that a crash leaves no half-invalidated account state.
    async Task InvalidateObservationsForAccountAsync(string accountId, DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetObservationsForAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        foreach (var obs in existing)
        {
            if (obs.RemainingFraction != null || obs.ResetTime != null || !string.Equals(obs.Source, "LiveTargetVerificationRejected", StringComparison.Ordinal))
            {
                await InvalidateIfUnchangedAsync(obs, invalidatedAtUtc, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Evidence revision boundary: reads the observation rows and the durable evidence
    // revision they were read at from ONE consistent snapshot. The revision must change
    // whenever a durable observation mutation lands and must be stable across non-mutating
    // reads. Router pool-status publication validates the revision so a status computed
    // from older evidence can be rejected after a durable revision re-check.
    // Stores that do not track revisions report a constant revision, which makes the
    // revision check trivially pass; durable stores must override all revision members.
    async Task<QuotaObservationSnapshot> GetObservationSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        return new QuotaObservationSnapshot(
            await GetAllObservationsAsync(cancellationToken).ConfigureAwait(false),
            Revision: 0);
    }

    Task<long> GetObservationRevisionAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);

    // Non-blocking view of the last successfully loaded or committed revision on this
    // instance. Null means unproven. This does not poll external writers: callers needing
    // cross-instance freshness must use the asynchronous durable revision read.
    // Revision-neutral stores use the same constant as their asynchronous defaults.
    long? KnownObservationRevision => 0L;
}
