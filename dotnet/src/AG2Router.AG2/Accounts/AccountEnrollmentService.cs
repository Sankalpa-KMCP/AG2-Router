using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Security;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Accounts;

/// <summary>
/// Coordinates safe, read-only enrollment of the currently authenticated
/// Antigravity 2 account into the encrypted session vault and account store.
/// 
/// SAFETY BOUNDARY:
/// - Reads ONLY: reads live adapter telemetry and Windows Credential Manager.
/// - Under NO circumstances does this service mutate WinCred, switch accounts,
///   or terminate/restart processes.
/// </summary>
public class AccountEnrollmentService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> EnrollmentLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IAG2Adapter _adapter;
    private readonly IWinCredReader _wincredReader;
    private readonly SessionVault _sessionVault;
    private readonly IAccountStore _accountStore;
    private readonly TimeSpan _completionTimeout;
    private readonly RecoveryQuarantine _lifecycleQuarantine;

    public AccountEnrollmentService(
        IAG2Adapter adapter,
        IWinCredReader wincredReader,
        SessionVault sessionVault,
        IAccountStore accountStore,
        TimeSpan? completionTimeout = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _wincredReader = wincredReader ?? throw new ArgumentNullException(nameof(wincredReader));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _completionTimeout = completionTimeout ?? TimeSpan.FromSeconds(15);
        if (_completionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(completionTimeout));
        _lifecycleQuarantine = RecoveryQuarantineRegistry.Get(GetEnrollmentResourcePath());
    }

    /// <summary>
    /// Enrolls the currently active Antigravity 2 account session into the local vault.
    /// Updates existing metadata without duplicates if the account is already registered.
    /// </summary>
    public async Task<EnrollmentResult> EnrollCurrentAccountAsync(
        EnrollmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureLifecycleAvailable();
        Task<EnrollmentResult> operation = EnrollCoreAsync(options, cancellationToken);
        try
        {
            return await operation.WaitAsync(_completionTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (operation.IsCompleted) return await operation.ConfigureAwait(false);
            _lifecycleQuarantine.Mark();
            _sessionVault.QuarantineUnresolvedMutation();
            _ = operation.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new AccountEnrollmentException(
                "Account enrollment did not settle within its deadline; manual recovery is required.");
        }
    }

    private async Task<EnrollmentResult> EnrollCoreAsync(
        EnrollmentOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Verify Antigravity 2 is running and has an authenticated account
            var currentAccount = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            if (currentAccount == null || string.IsNullOrWhiteSpace(currentAccount.Email))
            {
                throw new AccountEnrollmentException(
                    "Cannot enroll: Antigravity 2 is either offline or no authenticated account was detected."
                );
            }

            // 2. Read live session blob from Windows Credential Manager
            WinCredEntry? cred = await _wincredReader.ReadCredentialAsync(VaultConstants.DefaultAg2WinCredTarget, cancellationToken).ConfigureAwait(false);
            if (cred == null || cred.Blob == null || cred.Blob.Length == 0)
            {
                throw new AccountEnrollmentException(
                    $"Cannot enroll: No Windows Credential found for target '{VaultConstants.DefaultAg2WinCredTarget}'. Please ensure you are logged into Antigravity 2."
                );
            }

            try
            {
                if (string.IsNullOrWhiteSpace(cred.UserName) ||
                    cred.UserName.Length > VaultConstants.MaxAg2WinCredUserNameLength ||
                    cred.UserName.IndexOf('\0') >= 0)
                {
                    throw new AccountEnrollmentException(
                        "Windows Credential username is structurally invalid."
                    );
                }

                // 3. Inspect session blob structure safely
                try
                {
                    using var doc = JsonDocument.Parse(cred.Blob);
                    var root = doc.RootElement;
                    bool hasToken = root.TryGetProperty("token", out _);
                    bool hasAuthMethod = root.TryGetProperty("auth_method", out _);

                    if (!hasToken && !hasAuthMethod)
                    {
                        throw new AccountEnrollmentException(
                            "Windows Credential blob does not contain valid Antigravity authentication data."
                        );
                    }
                }
                catch (AccountEnrollmentException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new AccountEnrollmentException($"Failed to parse Windows Credential JSON payload: {ex.Message}", ex);
                }

                // Serialize same-identity enrollment across service instances and requests.
                string email = currentAccount.Email.Trim();
                var enrollmentLock = EnrollmentLocks.GetOrAdd(email, static _ => new SemaphoreSlim(1, 1));
                if (!await enrollmentLock.WaitAsync(_completionTimeout, cancellationToken).ConfigureAwait(false))
                    throw new AccountEnrollmentException(
                        "Account enrollment ownership did not become available; manual recovery is required.");
                try
                {
                    string enrollmentResource = GetEnrollmentResourcePath();
                    var resourceLock = PathLockRegistry.Get(enrollmentResource);
                    if (!await resourceLock.WaitAsync(_completionTimeout, cancellationToken).ConfigureAwait(false))
                        throw new AccountEnrollmentException(
                            "Account lifecycle ownership did not become available; manual recovery is required.");
                    try
                    {
                        EnsureLifecycleAvailable();
                        await using var enrollmentLease = await CrossProcessFileLease
                            .AcquireAsync(enrollmentResource, cancellationToken)
                            .ConfigureAwait(false);
                    EnsureLifecycleAvailable();

                    // Recheck inside the identity lock so duplicate concurrent enrollment is idempotent.
                    var existing = await _accountStore.GetAccountByEmailAsync(email, cancellationToken).ConfigureAwait(false);
                    string? previousActiveId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                    bool isNew = existing == null;
                    EnsureLifecycleAvailable();
                    AccountMetadata pending = existing ?? await _accountStore.AddAccountAsync(new CreateAccountInput(
                        Email: email,
                        Name: options?.Name ?? currentAccount.Name,
                        Priority: options?.Priority,
                        IsReserve: options?.IsReserve,
                        HasVaultedSession: false,
                        Notes: options?.Notes,
                        Alias: options?.Alias
                    ), cancellationToken).ConfigureAwait(false);

                    VaultMutationReceipt? vaultReceipt = null;
                    bool vaultCommitted = false;
                    AccountMetadata account;
                    try
                    {
                        // 4. Save session to vault under lock
                        EnsureLifecycleAvailable();
                        vaultReceipt = await _sessionVault.SaveSessionWithReceiptAsync(
                            pending.Id,
                            cred.Blob,
                            VaultConstants.DefaultAg2WinCredTarget,
                            cancellationToken).ConfigureAwait(false);
                        vaultCommitted = true;

                        // 5. Update account metadata to record vaulted status
                        string now = DateTimeOffset.UtcNow.ToString("O");
                        using var completion = new CancellationTokenSource(_completionTimeout);
                        EnsureLifecycleAvailable();
                        var committed = await _accountStore.UpdateAccountAsync(
                            pending.Id,
                            new UpdateAccountInput(
                                Name: options?.Name ?? pending.Name,
                                Priority: options?.Priority ?? pending.Priority,
                                IsReserve: options?.IsReserve ?? pending.IsReserve,
                                ValidationStatus: AccountValidationStatus.Valid,
                                HasVaultedSession: true,
                                LastActiveAt: now,
                                Notes: options?.Notes ?? pending.Notes,
                                Alias: options?.Alias ?? pending.Alias
                            ),
                            completion.Token).ConfigureAwait(false);
                        if (committed == null)
                        {
                            throw new AccountEnrollmentException($"Failed to update account record for '{email}'.");
                        }
                        account = committed;
                    }
                    catch (VaultMutationUncertainException ex)
                    {
                        _lifecycleQuarantine.Mark();
                        throw new RecoveryUncertainEnrollmentException(
                            "Vault save outcome is uncertain; manual recovery is required.", ex);
                    }
                    catch (OperationCanceledException ex) when (vaultCommitted)
                    {
                        throw new RecoveryUncertainEnrollmentException(
                            "Account metadata commit did not complete within its deadline; manual recovery is required.", ex);
                    }
                    catch
                    {
                        bool originallyVaulted = existing?.HasVaultedSession == true;
                        using var recovery = new CancellationTokenSource(_completionTimeout);
                        AccountMetadata? currentMetadata;
                        try
                        {
                            currentMetadata = await _accountStore.GetAccountAsync(pending.Id, recovery.Token)
                                .WaitAsync(recovery.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            throw new RecoveryUncertainEnrollmentException(
                                "Account metadata could not be read after a failed enrollment commit; manual recovery is required.");
                        }

                        if (currentMetadata == null)
                            throw new RecoveryUncertainEnrollmentException(
                                "Account metadata disappeared after a failed enrollment commit; manual recovery is required.");

                        bool metadataHasVaultedSession = currentMetadata.HasVaultedSession;
                        bool metadataUnchanged = currentMetadata == pending;

                        if (!metadataHasVaultedSession || originallyVaulted)
                        {
                            if (vaultCommitted)
                            {
                                try
                                {
                                    bool restored = await _sessionVault.RestoreIfCurrentAsync(
                                            vaultReceipt!, recovery.Token)
                                        .ConfigureAwait(false);
                                    if (!restored)
                                        throw new RecoveryUncertainEnrollmentException(
                                            "Vault restoration could not be proven after a failed enrollment commit; manual recovery is required.");
                                }
                                catch (AccountEnrollmentException) { throw; }
                                catch
                                {
                                    throw new RecoveryUncertainEnrollmentException(
                                        "Vault restoration failed after a failed enrollment commit; manual recovery is required.");
                                }
                            }

                            if (isNew && metadataUnchanged)
                            {
                                try
                                {
                                    bool removed = await _accountStore.RemoveAccountIfUnchangedAsync(pending, recovery.Token)
                                        .ConfigureAwait(false);
                                    if (!removed)
                                        throw new RecoveryUncertainEnrollmentException(
                                            "Account metadata cleanup could not be proven after a failed enrollment commit; manual recovery is required.");
                                }
                                catch (AccountEnrollmentException) { throw; }
                                catch
                                {
                                    throw new RecoveryUncertainEnrollmentException(
                                        "Account metadata cleanup failed after a failed enrollment commit; manual recovery is required.");
                                }
                            }
                        }

                        if (!metadataUnchanged)
                            throw new RecoveryUncertainEnrollmentException(
                                "Account metadata changed after a failed enrollment commit; manual recovery is required.");

                        throw;
                    }

                    // CRITICAL INVARIANT (V021-005): Obtain fresh authoritative identity and credential evidence
                    // immediately before asserting ACTIVE identity in metadata.
                    bool liveIdentityStillCurrent = false;
                    try
                    {
                        var freshAccount = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
                        var freshCred = await _wincredReader.ReadCredentialAsync(VaultConstants.DefaultAg2WinCredTarget, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            if (freshAccount != null &&
                                string.Equals(freshAccount.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase) &&
                                freshCred != null &&
                                string.Equals(freshCred.UserName, cred.UserName, StringComparison.Ordinal) &&
                                freshCred.Blob is { Length: > 0 } freshBlob &&
                                freshBlob.Length == cred.Blob.Length &&
                                CryptographicOperations.FixedTimeEquals(freshBlob, cred.Blob))
                            {
                                liveIdentityStillCurrent = true;
                            }
                        }
                        finally
                        {
                            if (freshCred?.Blob is { Length: > 0 })
                            {
                                System.Security.Cryptography.CryptographicOperations.ZeroMemory(freshCred.Blob);
                            }
                        }
                    }
                    catch
                    {
                        liveIdentityStillCurrent = false;
                    }

                    if (!liveIdentityStillCurrent)
                    {
                        using var recovery = new CancellationTokenSource(_completionTimeout);
                        try
                        {
                            if (!await _sessionVault.RestoreIfCurrentAsync(vaultReceipt!, recovery.Token)
                                    .ConfigureAwait(false))
                                throw new RecoveryUncertainEnrollmentException("The active session changed and vault compensation was uncertain; manual recovery is required.");
                            bool metadataRestored = isNew
                                ? await _accountStore.RemoveAccountIfUnchangedAsync(account, recovery.Token)
                                    .ConfigureAwait(false)
                                : await _accountStore.RestoreAccountIfUnchangedAsync(account, existing!, recovery.Token)
                                    .ConfigureAwait(false);
                            if (!metadataRestored)
                                throw new RecoveryUncertainEnrollmentException("The active session changed and account metadata could not be restored safely; manual recovery is required.");
                        }
                        catch (AccountEnrollmentException) { throw; }
                        catch (Exception ex)
                        {
                            throw new RecoveryUncertainEnrollmentException(
                                "The active session changed and compensation could not be proven; manual recovery is required.", ex);
                        }
                        throw new AccountEnrollmentException("The active session changed during capture; current-session enrollment was not committed.");
                    }

                    bool activeUpdated = false;
                    bool activeSelectionPreserved = false;
                    if (liveIdentityStillCurrent)
                    {
                        using var completion = new CancellationTokenSource(_completionTimeout);
                        try
                        {
                            EnsureLifecycleAvailable();
                            activeUpdated = await _accountStore.CompareExchangeActiveAccountIdAsync(
                                previousActiveId,
                                pending.Id,
                                completion.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            // A writer can durably replace the active marker and then
                            // throw. The exception alone cannot prove preservation.
                        }
                        if (!activeUpdated)
                        {
                            using var reconciliation = new CancellationTokenSource(_completionTimeout);
                            string? authoritativeActiveId;
                            try
                            {
                                authoritativeActiveId = await _accountStore.GetActiveAccountIdAsync(reconciliation.Token)
                                    .WaitAsync(reconciliation.Token).ConfigureAwait(false);
                            }
                            catch
                            {
                                throw new RecoveryUncertainEnrollmentException(
                                    "Active-account selection could not be verified after enrollment; manual recovery is required.");
                            }
                            activeUpdated = string.Equals(authoritativeActiveId, pending.Id, StringComparison.Ordinal);
                            activeSelectionPreserved = string.Equals(authoritativeActiveId, previousActiveId,
                                StringComparison.Ordinal);
                        }
                    }

                    string message = isNew
                        ? $"Successfully enrolled new account '{email}' into encrypted vault."
                        : $"Successfully updated session vault for existing account '{email}'.";
                    if (!activeUpdated)
                    {
                        message += activeSelectionPreserved
                            ? " The current active-account selection was preserved."
                            : " The active-account selection changed externally and was not overwritten.";
                    }

                    return new EnrollmentResult(true, account, isNew, message);
                    }
                    finally
                    {
                        resourceLock.Release();
                    }
                }
                finally
                {
                    enrollmentLock.Release();
                }
            }
            finally
            {
                if (cred?.Blob is { Length: > 0 })
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(cred.Blob);
                }
            }
        }
        catch (RecoveryUncertainEnrollmentException ex)
        {
            _lifecycleQuarantine.Mark();
            _sessionVault.QuarantineUnresolvedMutation();
            // Keep the existing public exception type while using an internal
            // marker to distinguish uncertain persistence from ordinary failure.
            throw new AccountEnrollmentException(ex.Message, ex.InnerException ?? ex);
        }
        catch (AccountEnrollmentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string sanitized = AG2Security.SanitizeError(ex);
            throw new AccountEnrollmentException(sanitized, ex);
        }
    }

    private void EnsureLifecycleAvailable()
    {
        if (_lifecycleQuarantine.IsMarked || _sessionVault.IsQuarantined)
            throw new AccountEnrollmentException(
                "Account lifecycle is unresolved; manual recovery is required.");
    }

    private string GetEnrollmentResourcePath() => $"{_sessionVault.GetVaultPath()}.switch";

    private sealed class RecoveryUncertainEnrollmentException : AccountEnrollmentException
    {
        public RecoveryUncertainEnrollmentException(string message) : base(message) { }
        public RecoveryUncertainEnrollmentException(string message, Exception inner) : base(message, inner) { }
    }
}

public sealed record AccountRemovalResult(bool VaultRecordDeleted);

public sealed class AccountRemovalConflictException(string message) : Exception(message);

/// <summary>Serializes deletion with enrollment and switching and compensates cross-store failure.</summary>
public sealed class AccountRemovalService
{
    private readonly IAccountStore accountStore;
    private readonly SessionVault sessionVault;
    private readonly TimeSpan completionTimeout;
    private readonly RecoveryQuarantine lifecycleQuarantine;

    public AccountRemovalService(IAccountStore accountStore, SessionVault sessionVault,
        TimeSpan? completionTimeout = null)
    {
        this.accountStore = accountStore;
        this.sessionVault = sessionVault;
        this.completionTimeout = completionTimeout ?? TimeSpan.FromSeconds(15);
        if (this.completionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(completionTimeout));
        lifecycleQuarantine = RecoveryQuarantineRegistry.Get(sessionVault.GetVaultPath() + ".switch");
    }

    public async Task<AccountRemovalResult> RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        EnsureLifecycleAvailable();
        Task<AccountRemovalResult> operation = RemoveCoreAsync(accountId, cancellationToken);
        try
        {
            return await operation.WaitAsync(completionTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (operation.IsCompleted) return await operation.ConfigureAwait(false);
            lifecycleQuarantine.Mark();
            sessionVault.QuarantineUnresolvedMutation();
            _ = operation.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw new InvalidOperationException(
                "Account removal did not settle within its deadline; manual recovery is required.");
        }
    }

    private async Task<AccountRemovalResult> RemoveCoreAsync(string accountId, CancellationToken cancellationToken)
    {
        string switchResource = sessionVault.GetVaultPath() + ".switch";
        var switchLock = PathLockRegistry.Get(switchResource);
        if (!await switchLock.WaitAsync(completionTimeout, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Account lifecycle ownership did not become available; manual recovery is required.");
        try
        {
            EnsureLifecycleAvailable();
            await using var lease = await CrossProcessFileLease.AcquireAsync(switchResource, cancellationToken)
                .ConfigureAwait(false);
            EnsureLifecycleAvailable();

            var expected = await accountStore.GetAccountAsync(accountId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Account not found.");
            var activeId = await accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(activeId, accountId, StringComparison.Ordinal))
                throw new AccountRemovalConflictException("Cannot delete currently active account. Switch to another account first.");

            VaultRemovalReceipt? receipt;
            EnsureLifecycleAvailable();
            using (var vaultCompletion = new CancellationTokenSource(completionTimeout))
            {
                try
                {
                    receipt = await sessionVault.RemoveSessionWithReceiptAsync(accountId, vaultCompletion.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (vaultCompletion.IsCancellationRequested)
                {
                    // The vault operation retains its own file ownership until it actually
                    // completes. The lifecycle lease can be released without allowing a
                    // later stale vault write to overtake a new vault mutation.
                    throw new InvalidOperationException(
                        "Vault removal did not complete within its deadline; manual recovery is required.", ex);
                }
            }
            try
            {
                using var completion = new CancellationTokenSource(completionTimeout);
                EnsureLifecycleAvailable();
                if (!await accountStore.RemoveAccountIfUnchangedAsync(expected, completion.Token)
                        .ConfigureAwait(false))
                    throw new AccountRemovalConflictException("Account metadata changed during removal.");
                return new AccountRemovalResult(receipt != null);
            }
            catch
            {
                if (receipt != null)
                {
                    // INVARIANT (V021-010): Only restore the removed session if metadata still contains the account.
                    // If the metadata writer successfully replaced accounts.json with the account removed,
                    // restoring the vault would create an orphan session without an account.
                    AccountMetadata? currentMetadata = null;
                    using var recovery = new CancellationTokenSource(completionTimeout);
                    try
                    {
                        currentMetadata = await accountStore.GetAccountAsync(accountId, recovery.Token)
                            .WaitAsync(recovery.Token).ConfigureAwait(false);
                    }
                    catch (Exception readError)
                    {
                        try
                        {
                            await sessionVault.RestoreRemovedIfAbsentAsync(receipt, recovery.Token)
                                .ConfigureAwait(false);
                        }
                        catch (Exception restoreError)
                        {
                            QuarantineUncertainRemoval();
                            throw new InvalidOperationException(
                                "Account removal outcome is unknown and vault restoration failed; manual recovery is required.",
                                new AggregateException(readError, restoreError));
                        }
                        QuarantineUncertainRemoval();
                        throw new InvalidOperationException(
                            "Account removal outcome is unknown after metadata readback failed; manual review is required.", readError);
                    }

                    if (currentMetadata != null)
                    {
                        try
                        {
                            if (!await sessionVault.RestoreRemovedIfAbsentAsync(receipt, recovery.Token)
                                    .ConfigureAwait(false))
                                throw new InvalidOperationException("Account removal compensation could not restore the vaulted session; manual recovery is required.");
                        }
                        catch (Exception restoreError)
                        {
                            QuarantineUncertainRemoval();
                            throw new InvalidOperationException(
                                "Account removal compensation could not be proven; manual recovery is required.", restoreError);
                        }
                    }
                }
                throw;
            }
        }
        finally
        {
            switchLock.Release();
        }
    }

    private void EnsureLifecycleAvailable()
    {
        if (lifecycleQuarantine.IsMarked || sessionVault.IsQuarantined)
            throw new InvalidOperationException("Account lifecycle is unresolved; manual recovery is required.");
    }

    private void QuarantineUncertainRemoval()
    {
        lifecycleQuarantine.Mark();
        sessionVault.QuarantineUnresolvedMutation();
    }
}
