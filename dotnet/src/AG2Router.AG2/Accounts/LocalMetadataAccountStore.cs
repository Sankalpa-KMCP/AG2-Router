using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AG2Router.AG2.Persistence;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Accounts;

/// <summary>
/// File-backed non-secret account metadata store using the shared TypeScript schema.
/// Mutations are serialized by canonical path, rebase on the latest durable snapshot,
/// and are published to this instance only after physical flush and replacement succeed.
/// </summary>
public class LocalMetadataAccountStore : IAccountStore
{
    private sealed record StoredAccount(
        [property: JsonPropertyName("id"), JsonRequired] string Id,
        [property: JsonPropertyName("email"), JsonRequired] string Email,
        [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name,
        [property: JsonPropertyName("priority"), JsonRequired] int Priority,
        [property: JsonPropertyName("isReserve"), JsonRequired] bool IsReserve,
        [property: JsonPropertyName("validationStatus"), JsonRequired] string ValidationStatus,
        [property: JsonPropertyName("hasVaultedSession")] bool HasVaultedSession,
        [property: JsonPropertyName("createdAt"), JsonRequired] string CreatedAt,
        [property: JsonPropertyName("updatedAt"), JsonRequired] string UpdatedAt,
        [property: JsonPropertyName("lastActiveAt"), JsonRequired] string? LastActiveAt,
        [property: JsonPropertyName("notes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Notes
    );

    private sealed record StoredData(
        [property: JsonPropertyName("version"), JsonRequired] int Version,
        [property: JsonPropertyName("activeAccountId"), JsonRequired] string? ActiveAccountId,
        [property: JsonPropertyName("accounts"), JsonRequired] List<StoredAccount> Accounts
    );

    private sealed record StoreState(IReadOnlyList<AccountMetadata> Accounts, string? ActiveAccountId)
    {
        public static readonly StoreState Empty = new([], null);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly HashSet<string> ValidValidationStatuses =
    [
        AccountValidationStatus.Valid,
        AccountValidationStatus.Expired,
        AccountValidationStatus.Unvalidated,
        AccountValidationStatus.Failed
    ];

    private readonly string _filePath;
    private readonly SemaphoreSlim _pathLock;
    private readonly IDurableFileWriter _fileWriter;
    private StoreState _state = StoreState.Empty;
    private bool _hasObservedExistingFile;

    public LocalMetadataAccountStore(string? filePath = null)
        : this(filePath, new DurableFileWriter())
    {
    }

    internal LocalMetadataAccountStore(string? filePath, IDurableFileWriter fileWriter)
    {
        _filePath = Path.GetFullPath(string.IsNullOrWhiteSpace(filePath)
            ? ResolveDefaultFilePath()
            : filePath);
        _pathLock = PathLockRegistry.Get(_filePath);
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
    }

    /// <summary>
    /// Shared TypeScript/.NET metadata path contract: DATA_DIR when set, otherwise
    /// the stable per-user LocalAppData/AG2-Router/data directory.
    /// </summary>
    public static string ResolveDefaultFilePath(
        string? dataDirectory = null,
        string? currentDirectory = null,
        string? localApplicationData = null)
    {
        string cwd = string.IsNullOrWhiteSpace(currentDirectory)
            ? Environment.CurrentDirectory
            : currentDirectory;
        string? configured = dataDirectory ?? Environment.GetEnvironmentVariable("DATA_DIR");
        string directory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            directory = Path.GetFullPath(configured, cwd);
        }
        else
        {
            string? localRoot = localApplicationData ?? Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localRoot))
            {
                localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            directory = Path.Combine(localRoot, "AG2-Router", "data");
        }
        return Path.GetFullPath(Path.Combine(directory, "accounts.json"));
    }

    public string GetFilePath() => _filePath;

    public async Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        await WithCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        return _state.Accounts.OrderBy(static account => account.Priority).ToList();
    }

    public async Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        await WithCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        return _state.Accounts.FirstOrDefault(account => account.Id == id);
    }

    public async Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        await WithCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        string normalized = email.Trim();
        return _state.Accounts.FirstOrDefault(account =>
            string.Equals(account.Email, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Email) || !input.Email.Contains('@'))
        {
            throw new ArgumentException("A valid email address is required.", nameof(input));
        }

        return MutateAsync(state =>
        {
            string email = input.Email.Trim();
            if (state.Accounts.Any(account =>
                string.Equals(account.Email, email, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Account with email '{input.Email}' already exists.");
            }

            string now = DateTimeOffset.UtcNow.ToString("O");
            var account = new AccountMetadata(
                Id: $"acc_{Guid.NewGuid():N}"[..12],
                Email: email,
                Name: input.Name?.Trim(),
                Priority: input.Priority ?? (state.Accounts.Count + 1),
                IsReserve: input.IsReserve ?? false,
                ValidationStatus: AccountValidationStatus.Unvalidated,
                HasVaultedSession: input.HasVaultedSession ?? false,
                CreatedAt: now,
                UpdatedAt: now,
                LastActiveAt: null,
                Notes: input.Notes);

            var accounts = state.Accounts.Append(account).ToList();
            return (new StoreState(accounts, state.ActiveAccountId), account);
        }, cancellationToken);
    }

    public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult<AccountMetadata?>(null);

        return MutateAsync<AccountMetadata?>(state =>
        {
            var accounts = state.Accounts.ToList();
            int index = accounts.FindIndex(account => account.Id == id);
            if (index < 0) return (state, null);

            var existing = accounts[index];
            string validationStatus = updates.ValidationStatus ?? existing.ValidationStatus;
            if (!ValidValidationStatuses.Contains(validationStatus))
            {
                throw new ArgumentException($"Unsupported validation status '{validationStatus}'.", nameof(updates));
            }

            var updated = existing with
            {
                Name = updates.Name != null ? updates.Name.Trim() : existing.Name,
                Priority = updates.Priority ?? existing.Priority,
                IsReserve = updates.IsReserve ?? existing.IsReserve,
                ValidationStatus = validationStatus,
                HasVaultedSession = updates.HasVaultedSession ?? existing.HasVaultedSession,
                LastActiveAt = updates.LastActiveAt ?? existing.LastActiveAt,
                Notes = updates.Notes ?? existing.Notes,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
            };

            accounts[index] = updated;
            return (new StoreState(accounts, state.ActiveAccountId), updated);
        }, cancellationToken);
    }

    public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(false);

        return MutateAsync(state =>
        {
            var accounts = state.Accounts.Where(account => account.Id != id).ToList();
            if (accounts.Count == state.Accounts.Count) return (state, false);

            string? activeAccountId = state.ActiveAccountId == id ? null : state.ActiveAccountId;
            return (new StoreState(accounts, activeAccountId), true);
        }, cancellationToken);
    }

    public async Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
    {
        await WithCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        return _state.ActiveAccountId;
    }

    public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default)
    {
        return MutateAsync<object?>(state =>
        {
            if (id != null && state.Accounts.All(account => account.Id != id))
            {
                throw new InvalidOperationException(
                    $"Cannot set active account: account with id '{id}' not found.");
            }

            return (new StoreState(state.Accounts.ToList(), id), null);
        }, cancellationToken);
    }

    public Task<bool> CompareExchangeActiveAccountIdAsync(
        string? expectedId,
        string? newId,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(state =>
        {
            if (newId != null && state.Accounts.All(account => account.Id != newId))
            {
                throw new InvalidOperationException(
                    $"Cannot set active account: account with id '{newId}' not found.");
            }

            if (!string.Equals(state.ActiveAccountId, expectedId, StringComparison.Ordinal))
            {
                return (state, false);
            }

            if (string.Equals(expectedId, newId, StringComparison.Ordinal)) return (state, true);
            return (new StoreState(state.Accounts.ToList(), newId), true);
        }, cancellationToken);
    }

    private async Task WithCurrentStateAsync(CancellationToken cancellationToken)
    {
        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease.AcquireAsync(_filePath, cancellationToken)
                .ConfigureAwait(false);
            _state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pathLock.Release();
        }
    }

    private async Task<TResult> MutateAsync<TResult>(
        Func<StoreState, (StoreState NextState, TResult Result)> mutation,
        CancellationToken cancellationToken)
    {
        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease.AcquireAsync(_filePath, cancellationToken)
                .ConfigureAwait(false);

            StoreState current = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            _state = current;
            var (next, result) = mutation(current);
            if (ReferenceEquals(next, current)) return result;

            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            _state = next;
            _hasObservedExistingFile = true;
            return result;
        }
        finally
        {
            _pathLock.Release();
        }
    }

    private async Task<StoreState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            if (_hasObservedExistingFile)
            {
                throw new IOException($"Account metadata store '{_filePath}' disappeared after it was loaded.");
            }

            return StoreState.Empty;
        }

        _hasObservedExistingFile = true;
        string content = await File.ReadAllTextAsync(_filePath, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException($"Account metadata store '{_filePath}' is empty or truncated.");
        }

        StoredData data;
        try
        {
            data = JsonSerializer.Deserialize<StoredData>(content, JsonOptions)
                ?? throw new InvalidDataException($"Account metadata store '{_filePath}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Account metadata store '{_filePath}' is malformed or violates schema version 1.", ex);
        }

        Validate(data);
        return new StoreState(
            data.Accounts.Select(static account => new AccountMetadata(
                account.Id,
                account.Email,
                account.Name,
                account.Priority,
                account.IsReserve,
                account.ValidationStatus,
                account.HasVaultedSession,
                account.CreatedAt,
                account.UpdatedAt,
                account.LastActiveAt,
                account.Notes)).ToList(),
            data.ActiveAccountId);
    }

    private async Task PersistStateAsync(StoreState state, CancellationToken cancellationToken)
    {
        var data = new StoredData(
            Version: 1,
            ActiveAccountId: state.ActiveAccountId,
            Accounts: state.Accounts.Select(static account => new StoredAccount(
                account.Id,
                account.Email,
                account.Name,
                account.Priority,
                account.IsReserve,
                account.ValidationStatus,
                account.HasVaultedSession,
                account.CreatedAt,
                account.UpdatedAt,
                account.LastActiveAt,
                account.Notes)).ToList());

        string json = JsonSerializer.Serialize(data, JsonOptions);
        await _fileWriter.WriteAtomicAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(StoredData data)
    {
        if (data.Version != 1)
        {
            throw new InvalidDataException(
                $"Unsupported account metadata schema version '{data.Version}' (expected 1).");
        }

        if (data.Accounts == null)
        {
            throw new InvalidDataException("Account metadata store is missing its accounts array.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in data.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Id) ||
                string.IsNullOrWhiteSpace(account.Email) ||
                !account.Email.Contains('@') ||
                string.IsNullOrWhiteSpace(account.CreatedAt) ||
                string.IsNullOrWhiteSpace(account.UpdatedAt) ||
                !DateTimeOffset.TryParse(account.CreatedAt, out _) ||
                !DateTimeOffset.TryParse(account.UpdatedAt, out _) ||
                (account.LastActiveAt != null && !DateTimeOffset.TryParse(account.LastActiveAt, out _)) ||
                !ValidValidationStatuses.Contains(account.ValidationStatus))
            {
                throw new InvalidDataException(
                    $"Account metadata record '{account.Id}' contains invalid required fields.");
            }

            if (!ids.Add(account.Id) || !emails.Add(account.Email))
            {
                throw new InvalidDataException(
                    $"Account metadata store contains duplicate id or email for '{account.Id}'.");
            }
        }

        if (data.ActiveAccountId != null && !ids.Contains(data.ActiveAccountId))
        {
            throw new InvalidDataException(
                $"Active account id '{data.ActiveAccountId}' does not reference a stored account.");
        }
    }
}
