using AG2Router.Core.Models;

namespace AG2Router.Core.Contracts;

public interface IAccountStore
{
    Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default);
    Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default);
}

public interface IAG2Adapter
{
    Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default);
    Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default);
    Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default);
}

public interface ISessionVault
{
    Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default);
    string GetVaultPath();
}

public interface IRouterState
{
    RouterStatusDto GetStatus();
}
