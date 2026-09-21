using System.Text.Json;
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

            // 4. Check if account already exists in AccountStore
            string email = currentAccount.Email.Trim();
            var existing = await _accountStore.GetAccountByEmailAsync(email, cancellationToken).ConfigureAwait(false);

            AccountMetadata account;
            bool isNew = false;
            string now = DateTime.UtcNow.ToString("o");

            if (existing != null)
            {
                // Update existing account
                var updated = await _accountStore.UpdateAccountAsync(existing.Id, new UpdateAccountInput(
                    Name: options?.Name ?? existing.Name ?? currentAccount.Name,
                    Priority: options?.Priority ?? existing.Priority,
                    IsReserve: options?.IsReserve ?? existing.IsReserve,
                    ValidationStatus: AccountValidationStatus.Valid,
                    HasVaultedSession: true,
                    LastActiveAt: now,
                    Notes: options?.Notes ?? existing.Notes
                ), cancellationToken).ConfigureAwait(false);

                if (updated == null)
                {
                    throw new AccountEnrollmentException($"Failed to update existing account record for '{email}'.");
                }

                account = updated;
            }
            else
            {
                // Register new account
                isNew = true;
                var created = await _accountStore.AddAccountAsync(new CreateAccountInput(
                    Email: email,
                    Name: options?.Name ?? currentAccount.Name,
                    Priority: options?.Priority,
                    IsReserve: options?.IsReserve,
                    HasVaultedSession: true,
                    Notes: options?.Notes
                ), cancellationToken).ConfigureAwait(false);

                var validated = await _accountStore.UpdateAccountAsync(created.Id, new UpdateAccountInput(
                    ValidationStatus: AccountValidationStatus.Valid,
                    LastActiveAt: now
                ), cancellationToken).ConfigureAwait(false);

                account = validated ?? created;
            }

            // 5. Encrypt and save session into the session vault
            await _sessionVault.SaveSessionAsync(account.Id, cred.Blob, cred.Target, cancellationToken).ConfigureAwait(false);

            // 6. Mark as active account in account store
            await _accountStore.SetActiveAccountIdAsync(account.Id, cancellationToken).ConfigureAwait(false);

            string message = isNew
                ? $"Successfully enrolled new account '{email}' into encrypted vault."
                : $"Successfully updated session vault for existing account '{email}'.";

            return new EnrollmentResult(
                Success: true,
                Account: account,
                IsNew: isNew,
                Message: message
            );
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
}
