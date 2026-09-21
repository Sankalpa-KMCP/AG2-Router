using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Accounts;

/// <summary>
/// File-backed non-secret account metadata store.
/// Stores accounts.json with atomic temp file swap and serialized writes.
/// </summary>
public class LocalMetadataAccountStore : IAccountStore
{
    private record StoredData(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("activeAccountId")] string? ActiveAccountId,
        [property: JsonPropertyName("accounts")] List<AccountMetadata> Accounts
    );

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly InMemoryAccountStore _inMemory = new();
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _isLoaded;

    public LocalMetadataAccountStore(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _filePath = filePath;
        }
        else
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string dir = !string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(localAppData, "AG2-Router", "data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AG2-Router", "data");
            _filePath = Path.Combine(dir, "accounts.json");
        }
    }

    public string GetFilePath() => _filePath;

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_isLoaded) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isLoaded) return;

            if (File.Exists(_filePath))
            {
                string content = await File.ReadAllTextAsync(_filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    var data = JsonSerializer.Deserialize<StoredData>(content, JsonOptions);
                    if (data?.Accounts != null)
                    {
                        _inMemory.RestoreAccounts(data.Accounts, data.ActiveAccountId);
                    }
                }
            }

            _isLoaded = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accounts = (await _inMemory.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var activeAccountId = await _inMemory.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);

            var data = new StoredData(
                Version: 1,
                ActiveAccountId: activeAccountId,
                Accounts: accounts
            );

            string dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            string tempFile = $"{_filePath}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{Guid.NewGuid().ToString("N")[..6]}.tmp";
            string json = JsonSerializer.Serialize(data, JsonOptions);

            try
            {
                await File.WriteAllTextAsync(tempFile, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                File.Move(tempFile, _filePath, overwrite: true);
            }
            catch (Exception)
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { /* ignore */ }
                }
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return await _inMemory.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return await _inMemory.GetAccountAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return await _inMemory.GetAccountByEmailAsync(email, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var created = await _inMemory.AddAccountAsync(input, cancellationToken).ConfigureAwait(false);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    /// <inheritdoc />
    public async Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var updated = await _inMemory.UpdateAccountAsync(id, updates, cancellationToken).ConfigureAwait(false);
        if (updated != null)
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        return updated;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        bool removed = await _inMemory.RemoveAccountAsync(id, cancellationToken).ConfigureAwait(false);
        if (removed)
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        return removed;
    }

    /// <inheritdoc />
    public async Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return await _inMemory.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _inMemory.SetActiveAccountIdAsync(id, cancellationToken).ConfigureAwait(false);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }
}
