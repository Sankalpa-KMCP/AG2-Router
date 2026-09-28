using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Vault;

internal sealed record VaultMutationReceipt(
    string AccountId,
    VaultAccountRecord CommittedRecord,
    VaultAccountRecord? PreviousRecord);

internal sealed record VaultRemovalReceipt(string AccountId, VaultAccountRecord RemovedRecord);

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
    private readonly SemaphoreSlim _pathLock;
    private readonly RecoveryQuarantine _recoveryQuarantine;
    private readonly IDurableFileWriter _fileWriter;
    internal TimeSpan AdmissionTimeout { get; set; } = TimeSpan.FromSeconds(15);
    internal TimeSpan MutationTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public SessionVault(string? vaultDir = null, IDpapiProvider? dpapiProvider = null)
        : this(vaultDir, dpapiProvider, new DurableFileWriter())
    {
    }

    internal SessionVault(
        string? vaultDir,
        IDpapiProvider? dpapiProvider,
        IDurableFileWriter fileWriter)
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
        _pathLock = PathLockRegistry.Get(_vaultFilePath);
        _recoveryQuarantine = RecoveryQuarantineRegistry.Get(_vaultFilePath);
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
    }

    internal void QuarantineUnresolvedMutation()
    {
        _recoveryQuarantine.Mark();
        RecoveryQuarantineRegistry.Get(_vaultFilePath + ".switch").Mark();
    }
    internal bool IsQuarantined => _recoveryQuarantine.IsMarked;

    private void EnsureVaultAvailable()
    {
        if (_recoveryQuarantine.IsMarked)
            throw new VaultMutationUncertainException(
                "Vault outcome is unresolved; manual recovery is required before vault access.");
    }

    private async Task<T> ObserveMutationAsync<T>(Task<T> operation)
    {
        try
        {
            return await operation.WaitAsync(MutationTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (operation.IsCompleted)
            {
                try { return await operation.ConfigureAwait(false); }
                catch (VaultMutationUncertainException)
                {
                    QuarantineUnresolvedMutation();
                    throw;
                }
            }
            QuarantineUnresolvedMutation();
            _ = operation.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new VaultMutationUncertainException(
                "Vault mutation did not settle within its deadline; manual recovery is required.");
        }
        catch (VaultMutationUncertainException)
        {
            QuarantineUnresolvedMutation();
            throw;
        }
    }

    private async Task EnterPathAsync(CancellationToken cancellationToken)
    {
        EnsureVaultAvailable();
        if (!await _pathLock.WaitAsync(AdmissionTimeout, cancellationToken).ConfigureAwait(false))
            throw new VaultMutationUncertainException(
                "Vault ownership did not become available within its deadline; manual recovery is required.");
        if (_recoveryQuarantine.IsMarked)
        {
            _pathLock.Release();
            throw new VaultMutationUncertainException(
                "Vault mutation is unresolved; manual recovery is required before vault access.");
        }
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
        _ = await SaveSessionWithReceiptAsync(accountId, sessionBlob, target, cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task<VaultMutationReceipt> SaveSessionWithReceiptAsync(
        string accountId,
        byte[] sessionBlob,
        string target = VaultConstants.DefaultAg2WinCredTarget,
        CancellationToken cancellationToken = default) =>
        ObserveMutationAsync(SaveSessionCoreAsync(accountId, sessionBlob, target, cancellationToken));

    private async Task<VaultMutationReceipt> SaveSessionCoreAsync(
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

        await EnterPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease
                .AcquireAsync(_vaultFilePath, cancellationToken)
                .ConfigureAwait(false);
            EnsureVaultAvailable();

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
            EnsureVaultAvailable();
            try
            {
                await WriteEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception writeError)
            {
                VaultAccountRecord? authoritative;
                try
                {
                    ReadEnvelope().Records.TryGetValue(accountId, out authoritative);
                }
                catch (Exception readError)
                {
                    throw new VaultMutationUncertainException(
                        "Vault save outcome could not be read; manual recovery is required.",
                        new AggregateException(writeError, readError));
                }

                if (authoritative == record)
                    return new VaultMutationReceipt(accountId, record, existing);
                if (authoritative != existing)
                    throw new VaultMutationUncertainException(
                        "Vault save outcome changed unexpectedly; manual recovery is required.", writeError);
                throw;
            }
            return new VaultMutationReceipt(accountId, record, existing);
        }
        finally
        {
            _pathLock.Release();
        }
    }

    internal Task<bool> RestoreIfCurrentAsync(
        VaultMutationReceipt receipt,
        CancellationToken cancellationToken = default) =>
        ObserveMutationAsync(RestoreIfCurrentCoreAsync(receipt, cancellationToken));

    private async Task<bool> RestoreIfCurrentCoreAsync(
        VaultMutationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        await EnterPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease
                .AcquireAsync(_vaultFilePath, cancellationToken)
                .ConfigureAwait(false);
            EnsureVaultAvailable();
            var envelope = ReadEnvelope();
            if (!envelope.Records.TryGetValue(receipt.AccountId, out var current) ||
                current != receipt.CommittedRecord)
            {
                return false;
            }

            if (receipt.PreviousRecord == null)
            {
                envelope.Records.Remove(receipt.AccountId);
            }
            else
            {
                envelope.Records[receipt.AccountId] = receipt.PreviousRecord;
            }

            EnsureVaultAvailable();
            try { await WriteEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                throw new VaultMutationUncertainException(
                    "Vault restoration outcome could not be proven; manual recovery is required.", ex);
            }
            return true;
        }
        finally
        {
            _pathLock.Release();
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
        await EnterPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease
                .AcquireAsync(_vaultFilePath, cancellationToken)
                .ConfigureAwait(false);
            EnsureVaultAvailable();
            var envelope = ReadEnvelope();
            if (!envelope.Records.TryGetValue(accountId, out record))
            {
                return null;
            }
        }
        finally
        {
            _pathLock.Release();
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
    /// Read-only recovery retrieval of an account session during quarantine.
    /// Bypasses EnsureVaultAvailable() to permit proof-based reconciliation,
    /// but strictly validates path lock, file lease, and identity framing without performing any mutations.
    /// </summary>
    internal async Task<byte[]?> GetSessionForRecoveryAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new VaultException("accountId is required");
        }

        VaultAccountRecord? record;
        if (!await _pathLock.WaitAsync(AdmissionTimeout, cancellationToken).ConfigureAwait(false))
        {
            throw new VaultMutationUncertainException("Vault ownership did not become available.");
        }

        try
        {
            await using var lease = await CrossProcessFileLease.AcquireAsync(_vaultFilePath, cancellationToken).ConfigureAwait(false);
            var envelope = ReadEnvelope();
            if (!envelope.Records.TryGetValue(accountId, out record))
            {
                return null;
            }
        }
        finally
        {
            _pathLock.Release();
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

        await EnterPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease
                .AcquireAsync(_vaultFilePath, cancellationToken)
                .ConfigureAwait(false);
            EnsureVaultAvailable();
            var envelope = ReadEnvelope();
            return envelope.Records.ContainsKey(accountId);
        }
        finally
        {
            _pathLock.Release();
        }
    }

    /// <summary>
    /// Remove a session from the vault.
    /// </summary>
    public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default) =>
        ObserveMutationAsync(RemoveSessionCoreAsync(accountId, cancellationToken));

    private async Task<bool> RemoveSessionCoreAsync(string accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return false;
        }

        await EnterPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease
                .AcquireAsync(_vaultFilePath, cancellationToken)
                .ConfigureAwait(false);
            EnsureVaultAvailable();
            var envelope = ReadEnvelope();
            if (!envelope.Records.Remove(accountId))
            {
                return false;
            }

            EnsureVaultAvailable();
            try { await WriteEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                throw new VaultMutationUncertainException(
                    "Vault removal outcome could not be proven; manual recovery is required.", ex);
            }
            return true;
        }
        finally
        {
            _pathLock.Release();
        }
    }

    internal Task<VaultRemovalReceipt?> RemoveSessionWithReceiptAsync(
        string accountId, CancellationToken cancellationToken = default) =>
        ObserveMutationAsync(RemoveSessionWithReceiptCoreAsync(accountId, cancellationToken));

    private async Task<VaultRemovalReceipt?> RemoveSessionWithReceiptCoreAsync(
        string accountId, CancellationToken cancellationToken = default)
    {
        await EnterPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease.AcquireAsync(_vaultFilePath, cancellationToken)
                .ConfigureAwait(false);
            EnsureVaultAvailable();
            var envelope = ReadEnvelope();
            if (!envelope.Records.Remove(accountId, out var removed)) return null;
            EnsureVaultAvailable();
            try
            {
                await WriteEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A failed writer may have replaced the file before surfacing an error.
                // Restore the preimage under the same vault ownership before reporting failure.
                VaultFileEnvelope current;
                try { current = ReadEnvelope(); }
                catch (Exception readError)
                {
                    throw new VaultMutationUncertainException(
                        "Vault removal outcome could not be read; manual recovery is required.", readError);
                }
                if (current.Records.TryGetValue(accountId, out var authoritative))
                {
                    if (authoritative != removed)
                        throw new VaultMutationUncertainException(
                            "Vault removal changed the session unexpectedly; manual recovery is required.");
                }
                else
                {
                    current.Records.Add(accountId, removed);
                    try
                    {
                        // The first writer may have outlived its caller. Do not start a
                        // compensating write after that caller has quarantined the path.
                        EnsureVaultAvailable();
                        await WriteEnvelopeAsync(current, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        throw new VaultMutationUncertainException(
                            "Vault removal compensation could not be proven; manual recovery is required.", ex);
                    }
                }
                throw;
            }
            return new VaultRemovalReceipt(accountId, removed);
        }
        finally
        {
            _pathLock.Release();
        }
    }

    internal Task<bool> RestoreRemovedIfAbsentAsync(
        VaultRemovalReceipt receipt, CancellationToken cancellationToken = default) =>
        ObserveMutationAsync(RestoreRemovedIfAbsentCoreAsync(receipt, cancellationToken));

    private async Task<bool> RestoreRemovedIfAbsentCoreAsync(
        VaultRemovalReceipt receipt, CancellationToken cancellationToken = default)
    {
        await EnterPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease.AcquireAsync(_vaultFilePath, cancellationToken)
                .ConfigureAwait(false);
            EnsureVaultAvailable();
            var envelope = ReadEnvelope();
            if (envelope.Records.ContainsKey(receipt.AccountId)) return false;
            envelope.Records.Add(receipt.AccountId, receipt.RemovedRecord);
            EnsureVaultAvailable();
            try { await WriteEnvelopeAsync(envelope, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                throw new VaultMutationUncertainException(
                    "Vault restoration outcome could not be proven; manual recovery is required.", ex);
            }
            return true;
        }
        finally
        {
            _pathLock.Release();
        }
    }

    /// <summary>
    /// List all account IDs stored in the vault.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default)
    {
        await EnterPathAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease
                .AcquireAsync(_vaultFilePath, cancellationToken)
                .ConfigureAwait(false);
            EnsureVaultAvailable();
            var envelope = ReadEnvelope();
            return envelope.Records.Keys.ToList();
        }
        finally
        {
            _pathLock.Release();
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

    private async Task WriteEnvelopeAsync(
        VaultFileEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var updatedEnvelope = envelope with
        {
            UpdatedAt = DateTime.UtcNow.ToString("o")
        };

        string serialized = JsonSerializer.Serialize(updatedEnvelope, JsonOptions);
        try
        {
            await _fileWriter.WriteAtomicAsync(_vaultFilePath, serialized, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new VaultException($"Failed to atomically write vault file: {ex.Message}", ex);
        }
    }
}
