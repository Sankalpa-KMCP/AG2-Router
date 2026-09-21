using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Vault;

/// <summary>
/// Encrypted Multi-Account Session Vault.
/// Implements persistent, encrypted storage for Antigravity 2 account sessions.
/// Features:
/// - Single-layer DPAPI CurrentUser encryption per record.
/// - Internal identity framing validation (fails closed on record swapping).
/// - Atomic persistence via temp file swap.
/// - Fail-closed integrity: corrupted files are never overwritten.
/// </summary>
public class SessionVault : ISessionVault
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _vaultDir;
    private readonly string _vaultFilePath;
    private readonly IDpapiProvider _dpapiProvider;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public SessionVault(string? vaultDir = null, IDpapiProvider? dpapiProvider = null)
    {
        if (!string.IsNullOrWhiteSpace(vaultDir))
        {
            _vaultDir = vaultDir;
        }
        else
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                _vaultDir = Path.Combine(localAppData, "AG2-Router", "vault");
            }
            else
            {
                _vaultDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "AG2-Router",
                    "vault"
                );
            }
        }

        _vaultFilePath = Path.Combine(_vaultDir, "sessions.dat");
        _dpapiProvider = dpapiProvider ?? throw new ArgumentNullException(nameof(dpapiProvider));
    }

    /// <summary>
    /// Return the absolute path to the sessions.dat vault file.
    /// </summary>
    public string GetVaultPath() => _vaultFilePath;

    /// <summary>
    /// Return the vault directory path.
    /// </summary>
    public string GetVaultDir() => _vaultDir;

    /// <summary>
    /// Encrypt and store an account session into the vault.
    /// Atomically updates sessions.dat.
    /// </summary>
    public async Task SaveSessionAsync(
        string accountId,
        byte[] sessionBlob,
        string target = VaultConstants.DefaultAg2WinCredTarget,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new VaultException("accountId is required to save session");
        }

        if (sessionBlob == null || sessionBlob.Length == 0)
        {
            throw new VaultException("sessionBlob cannot be empty or null");
        }

        // 1. Prepare plaintext envelope with internal identity framing
        var plaintextPayload = new VaultedSessionPlaintext(
            Version: VaultConstants.SchemaVersion,
            AccountId: accountId,
            Target: target,
            CredentialBlobBase64: Convert.ToBase64String(sessionBlob),
            EnrolledAt: DateTime.UtcNow.ToString("o")
        );

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plaintextPayload, JsonOptions));

        // 2. Encrypt with Windows DPAPI (CurrentUser)
        byte[] encryptedBytes;
        try
        {
            encryptedBytes = await _dpapiProvider.EncryptAsync(plaintextBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Plaintext bytes zeroing
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }

        string encryptedPayloadBase64 = Convert.ToBase64String(encryptedBytes);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 3. Load current vault envelope (fails closed if existing file is corrupted)
            var envelope = ReadEnvelope();

            var now = DateTime.UtcNow.ToString("o");
            envelope.Records.TryGetValue(accountId, out var existing);

            var record = new VaultAccountRecord(
                AccountId: accountId,
                Target: target,
                EncryptedPayloadBase64: encryptedPayloadBase64,
                CreatedAt: existing?.CreatedAt ?? now,
                UpdatedAt: now
            );

            envelope.Records[accountId] = record;

            // 4. Save atomically
            WriteEnvelope(envelope);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Decrypt and retrieve an account session from the vault.
    /// Fails closed if identity framing does not match accountId or target.
    /// </summary>
    public async Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new VaultException("accountId is required to retrieve session");
        }

        VaultAccountRecord? record;
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = ReadEnvelope();
            if (!envelope.Records.TryGetValue(accountId, out record))
            {
                return null;
            }
        }
        finally
        {
            _fileLock.Release();
        }

        byte[] ciphertext = Convert.FromBase64String(record.EncryptedPayloadBase64);
        byte[] decryptedBytes;

        try
        {
            decryptedBytes = await _dpapiProvider.DecryptAsync(ciphertext, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new VaultCorruptionException(
                $"Failed to decrypt vaulted session for account '{accountId}': {ex.Message}", ex);
        }

        VaultedSessionPlaintext? plaintext;
        try
        {
            plaintext = JsonSerializer.Deserialize<VaultedSessionPlaintext>(decryptedBytes, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new VaultCorruptionException(
                $"Vault record for account '{accountId}' decrypted but contained malformed JSON payload: {ex.Message}", ex);
        }
        finally
        {
            // Best-effort memory hygiene
            CryptographicOperations.ZeroMemory(decryptedBytes);
        }

        if (plaintext == null)
        {
            throw new VaultCorruptionException($"Vault record for account '{accountId}' deserialized to null");
        }

        // Identity Framing Verification
        if (plaintext.Version != VaultConstants.SchemaVersion)
        {
            throw new VaultCorruptionException(
                $"Unsupported vaulted session version '{plaintext.Version}' for account '{accountId}'");
        }

        if (plaintext.AccountId != accountId)
        {
            throw new VaultCorruptionException(
                $"Identity framing violation: payload accountId '{plaintext.AccountId}' does not match record '{accountId}'");
        }

        if (plaintext.Target != record.Target)
        {
            throw new VaultCorruptionException(
                $"Target mismatch: payload target '{plaintext.Target}' does not match record target '{record.Target}'");
        }

        return Convert.FromBase64String(plaintext.CredentialBlobBase64);
    }

    /// <summary>
    /// Check if a session exists for the given accountId without decrypting it.
    /// </summary>
    public async Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return false;
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = ReadEnvelope();
            return envelope.Records.ContainsKey(accountId);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Remove a session from the vault.
    /// </summary>
    public async Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return false;
        }

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = ReadEnvelope();
            if (!envelope.Records.Remove(accountId))
            {
                return false;
            }

            WriteEnvelope(envelope);
            return true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// List all account IDs stored in the vault.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = ReadEnvelope();
            return envelope.Records.Keys.ToList();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private VaultFileEnvelope ReadEnvelope()
    {
        if (!File.Exists(_vaultFilePath))
        {
            return new VaultFileEnvelope(
                Magic: VaultConstants.Magic,
                SchemaVersion: VaultConstants.SchemaVersion,
                UpdatedAt: DateTime.UtcNow.ToString("o"),
                Records: new Dictionary<string, VaultAccountRecord>()
            );
        }

        string rawText;
        try
        {
            rawText = File.ReadAllText(_vaultFilePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            throw new VaultException($"Failed to read vault file: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new VaultCorruptionException($"Vault file at '{_vaultFilePath}' is empty or truncated");
        }

        VaultFileEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<VaultFileEnvelope>(rawText, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new VaultCorruptionException(
                $"Vault file at '{_vaultFilePath}' contains invalid JSON and cannot be parsed: {ex.Message}", ex);
        }

        if (envelope == null)
        {
            throw new VaultCorruptionException($"Vault file at '{_vaultFilePath}' deserialized to null");
        }

        if (envelope.Magic != VaultConstants.Magic)
        {
            throw new VaultCorruptionException(
                $"Vault file at '{_vaultFilePath}' does not have valid magic identifier '{VaultConstants.Magic}'");
        }

        if (envelope.SchemaVersion != VaultConstants.SchemaVersion)
        {
            throw new VaultCorruptionException(
                $"Unsupported vault schema version '{envelope.SchemaVersion}' (expected {VaultConstants.SchemaVersion})");
        }

        if (envelope.Records == null)
        {
            throw new VaultCorruptionException($"Vault file at '{_vaultFilePath}' missing valid records dictionary");
        }

        return envelope;
    }

    private void WriteEnvelope(VaultFileEnvelope envelope)
    {
        Directory.CreateDirectory(_vaultDir);

        var updatedEnvelope = envelope with
        {
            UpdatedAt = DateTime.UtcNow.ToString("o")
        };

        string serialized = JsonSerializer.Serialize(updatedEnvelope, JsonOptions);
        string tempPath = $"{_vaultFilePath}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{Guid.NewGuid().ToString("N")[..6]}.tmp";

        try
        {
            File.WriteAllText(tempPath, serialized, Encoding.UTF8);
            File.Move(tempPath, _vaultFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }

            throw new VaultException($"Failed to atomically write vault file: {ex.Message}", ex);
        }
    }
}
