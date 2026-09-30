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
}
