using AG2Router.Core.Models;

namespace AG2Router.Core.Contracts;

public interface IAccountStore
{
    Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default);
    Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default);
    Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default);
    Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default);
    Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default);
    Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default);
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
    Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default);
    Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default);
    string GetVaultPath();
}

public interface IRouterState
{
    RouterStatusDto GetStatus();
}
