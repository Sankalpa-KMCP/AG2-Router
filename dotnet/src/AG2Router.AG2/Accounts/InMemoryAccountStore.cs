using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Accounts;

/// <summary>
/// Thread-safe in-memory account store.
/// Suitable for unit tests, isolated environments, and in-memory caching.
/// Contains ONLY non-sensitive metadata (email, priority, label).
/// </summary>
public class InMemoryAccountStore : IAccountStore
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, AccountMetadata> _accounts = new(StringComparer.Ordinal);
    private string? _activeAccountId;

    /// <inheritdoc />
    public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            var sorted = _accounts.Values
                .OrderBy(a => a.Priority)
                .ToList();
            return Task.FromResult<IReadOnlyList<AccountMetadata>>(sorted);
        }
    }

    /// <inheritdoc />
    public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult<AccountMetadata?>(null);

        lock (_syncRoot)
        {
            _accounts.TryGetValue(id, out var account);
            return Task.FromResult<AccountMetadata?>(account);
        }
    }

    /// <inheritdoc />
    public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return Task.FromResult<AccountMetadata?>(null);

        string normalized = email.Trim();
        lock (_syncRoot)
        {
            var found = _accounts.Values.FirstOrDefault(a =>
                string.Equals(a.Email, normalized, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }
    }

    /// <inheritdoc />
    public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Email) || !input.Email.Contains('@'))
        {
            throw new ArgumentException("A valid email address is required.", nameof(input));
        }

        string normalizedEmail = input.Email.Trim();

        lock (_syncRoot)
        {
            var existing = _accounts.Values.FirstOrDefault(a =>
                string.Equals(a.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                throw new InvalidOperationException($"Account with email '{input.Email}' already exists.");
            }

            string now = DateTime.UtcNow.ToString("o");
            string id = $"acc_{Guid.NewGuid().ToString("N")[..8]}";
            int priority = input.Priority ?? (_accounts.Count + 1);

            var account = new AccountMetadata(
                Id: id,
                Email: normalizedEmail,
                Name: input.Name?.Trim(),
                Priority: priority,
                IsReserve: input.IsReserve ?? false,
                ValidationStatus: AccountValidationStatus.Unvalidated,
                HasVaultedSession: input.HasVaultedSession ?? false,
                CreatedAt: now,
                UpdatedAt: now,
                LastActiveAt: null,
                Notes: input.Notes
            );

            _accounts[id] = account;
            return Task.FromResult(account);
        }
    }

    /// <inheritdoc />
    public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult<AccountMetadata?>(null);
        ArgumentNullException.ThrowIfNull(updates);

        lock (_syncRoot)
        {
            if (!_accounts.TryGetValue(id, out var existing))
            {
                return Task.FromResult<AccountMetadata?>(null);
            }

            var updated = existing with
            {
                Name = updates.Name != null ? updates.Name.Trim() : existing.Name,
                Priority = updates.Priority ?? existing.Priority,
                IsReserve = updates.IsReserve ?? existing.IsReserve,
                ValidationStatus = updates.ValidationStatus ?? existing.ValidationStatus,
                HasVaultedSession = updates.HasVaultedSession ?? existing.HasVaultedSession,
                LastActiveAt = updates.LastActiveAt ?? existing.LastActiveAt,
                Notes = updates.Notes ?? existing.Notes,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };

            _accounts[id] = updated;
            return Task.FromResult<AccountMetadata?>(updated);
        }
    }

    /// <inheritdoc />
    public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(false);

        lock (_syncRoot)
        {
            bool removed = _accounts.Remove(id);
            if (removed && _activeAccountId == id)
            {
                _activeAccountId = null;
            }
            return Task.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            return Task.FromResult(_activeAccountId);
        }
    }

    /// <inheritdoc />
    public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (id != null && !_accounts.ContainsKey(id))
            {
                throw new InvalidOperationException($"Cannot set active account: account with id '{id}' not found.");
            }
            _activeAccountId = id;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Restores accounts and active ID from persistent storage.
    /// </summary>
    public void RestoreAccounts(IEnumerable<AccountMetadata> accounts, string? activeAccountId)
    {
        lock (_syncRoot)
        {
            _accounts.Clear();
            foreach (var account in accounts)
            {
                _accounts[account.Id] = account;
            }
            _activeAccountId = activeAccountId;
        }
    }
}
