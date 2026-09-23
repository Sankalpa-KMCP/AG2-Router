using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2;

/// <summary>
/// Truthful placeholder AG2 adapter for the native shell foundation.
/// Does not perform live process discovery or RPC integration until subsequent migration slices.
/// </summary>
public class PlaceholderAG2Adapter : IAG2Adapter
{
    public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Ag2StatusDto(
            Connected: false,
            Status: "OFFLINE",
            Activity: new ActivityStatusDto("OFFLINE", 0, 0, DateTime.UtcNow.ToString("o")),
            Message: "Waiting for Antigravity 2"
        ));
    }

    public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<AccountIdentityDto?>(null);
    }

    public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<QuotaSnapshotDto?>(null);
    }

    public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AccountQuotaObservation(null, null));
    }

    public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ActivityStatusDto(
            State: "OFFLINE",
            TotalTrajectories: 0,
            RunningTrajectories: 0,
            Timestamp: DateTime.UtcNow.ToString("o")
        ));
    }
}
