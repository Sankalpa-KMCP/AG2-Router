using System.Collections.Concurrent;
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

    public AccountEnrollmentService(
        IAG2Adapter adapter,
        IWinCredReader wincredReader,
        SessionVault sessionVault,
        IAccountStore accountStore)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _wincredReader = wincredReader ?? throw new ArgumentNullException(nameof(wincredReader));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
    }

    /// <summary>
    /// Enrolls the currently active Antigravity 2 account session into the local vault.
    /// Updates existing metadata without duplicates if the account is already registered.
    /// </summary>
    public async Task<EnrollmentResult> EnrollCurrentAccountAsync(
        EnrollmentOptions? options = null,
        CancellationToken cancellationToken = default)
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
            var cred = await _wincredReader.ReadCredentialAsync(VaultConstants.DefaultAg2WinCredTarget, cancellationToken).ConfigureAwait(false);
            if (cred == null || cred.Blob == null || cred.Blob.Length == 0)
            {
                throw new AccountEnrollmentException(
                    $"Cannot enroll: No Windows Credential found for target '{VaultConstants.DefaultAg2WinCredTarget}'. Please ensure you are logged into Antigravity 2."
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
            await enrollmentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string enrollmentResource = GetEnrollmentResourcePath();
                var resourceLock = PathLockRegistry.Get(enrollmentResource);
                await resourceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var enrollmentLease = await CrossProcessFileLease
                        .AcquireAsync(enrollmentResource, cancellationToken)
                        .ConfigureAwait(false);

                // Recheck inside the identity lock so duplicate concurrent enrollment is idempotent.
                var existing = await _accountStore.GetAccountByEmailAsync(email, cancellationToken).ConfigureAwait(false);
                string? previousActiveId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                bool isNew = existing == null;
                AccountMetadata pending = existing ?? await _accountStore.AddAccountAsync(new CreateAccountInput(
                    Email: email,
                    Name: options?.Name ?? currentAccount.Name,
                    Priority: options?.Priority,
                    IsReserve: options?.IsReserve,
                    HasVaultedSession: false,
                    Notes: options?.Notes
                ), cancellationToken).ConfigureAwait(false);

                VaultMutationReceipt? vaultReceipt = null;
                bool vaultCommitted = false;
                bool activeCommitted = false;
                try
                {
                    // The vault commits first. Metadata cannot truthfully advertise a session before this succeeds.
                    vaultReceipt = await _sessionVault.SaveSessionWithReceiptAsync(
                        pending.Id,
                        cred.Blob,
                        cred.Target,
                        cancellationToken).ConfigureAwait(false);
                    vaultCommitted = true;

                    // Active selection is committed before the final metadata truth flag so any failure
                    // can be compensated while HasVaultedSession is still unchanged/false.
                    bool activeUpdated = await _accountStore.CompareExchangeActiveAccountIdAsync(
                        previousActiveId,
                        pending.Id,
                        cancellationToken).ConfigureAwait(false);
                    if (!activeUpdated)
                    {
                        throw new AccountEnrollmentException(
                            "Active account changed while enrollment was in progress; retry safely.");
                    }
                    activeCommitted = true;

                    string now = DateTimeOffset.UtcNow.ToString("O");
                    var account = await _accountStore.UpdateAccountAsync(pending.Id, new UpdateAccountInput(
                        Name: options?.Name,
                        Priority: options?.Priority,
                        IsReserve: options?.IsReserve,
                        ValidationStatus: AccountValidationStatus.Valid,
                        HasVaultedSession: true,
                        LastActiveAt: now,
                        Notes: options?.Notes
                    ), cancellationToken).ConfigureAwait(false);

                    if (account == null)
                    {
                        throw new AccountEnrollmentException($"Failed to update account record for '{email}'.");
                    }

                    string message = isNew
                        ? $"Successfully enrolled new account '{email}' into encrypted vault."
                        : $"Successfully updated session vault for existing account '{email}'.";

                    return new EnrollmentResult(true, account, isNew, message);
                }
                catch
                {
                    // Compensation is best effort, but its ordering preserves the core invariant:
                    // metadata never became true before the vault commit. A failed cleanup may leave
                    // only a truthful false/orphan state that a retry can reconcile.
                    if (activeCommitted)
                    {
                        try
                        {
                            await _accountStore.CompareExchangeActiveAccountIdAsync(
                                pending.Id,
                                previousActiveId,
                                CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch { }
                    }

                    if (vaultCommitted)
                    {
                        try
                        {
                            await _sessionVault.RestoreIfCurrentAsync(
                                vaultReceipt!,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch { }
                    }

                    if (isNew)
                    {
                        try
                        {
                            await _accountStore.RemoveAccountAsync(pending.Id, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch { }
                    }

                    throw;
                }
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

    private string GetEnrollmentResourcePath() => $"{_sessionVault.GetVaultPath()}.enrollment";
}
