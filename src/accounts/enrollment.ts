/**
 * AG2 Router - Account Enrollment Service
 *
 * Coordinates safe, read-only enrollment of the currently authenticated
 * Antigravity 2 account into the encrypted session vault and account store.
 *
 * SAFETY BOUNDARY:
 * - Reads ONLY: reads live adapter telemetry and Windows Credential Manager.
 * - Under NO circumstances does this service mutate WinCred, switch accounts,
 *   or terminate/restart processes.
 */

import { IAG2Adapter } from '../ag2/adapter.js';
import { redactSensitiveText, sanitizeError } from '../ag2/security.js';
import { IWinCredReader, DEFAULT_AG2_WINCRED_TARGET } from '../ag2/wincred.js';
import { SessionVault } from '../vault/session-vault.js';
import { AccountMetadata, IAccountStore } from './types.js';

export interface EnrollmentOptions {
  readonly name?: string;
  readonly priority?: number;
  readonly isReserve?: boolean;
  readonly notes?: string;
}

export interface EnrollmentResult {
  readonly success: boolean;
  readonly account: AccountMetadata;
  readonly message: string;
  readonly isNew: boolean;
}

export class AccountEnrollmentError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'AccountEnrollmentError';
  }
}

export interface AccountEnrollmentServiceOptions {
  readonly adapter: IAG2Adapter;
  readonly wincredReader: IWinCredReader;
  readonly sessionVault: SessionVault;
  readonly accountStore: IAccountStore;
}

export class AccountEnrollmentService {
  private readonly adapter: IAG2Adapter;
  private readonly wincredReader: IWinCredReader;
  private readonly sessionVault: SessionVault;
  private readonly accountStore: IAccountStore;

  constructor(options: AccountEnrollmentServiceOptions) {
    this.adapter = options.adapter;
    this.wincredReader = options.wincredReader;
    this.sessionVault = options.sessionVault;
    this.accountStore = options.accountStore;
  }

  /**
   * Enrolls the currently active Antigravity 2 account session into the local vault.
   * Updates existing metadata without duplicates if the account is already registered.
   */
  public async enrollCurrentAccount(options: EnrollmentOptions = {}): Promise<EnrollmentResult> {
    try {
      // 1. Verify Antigravity 2 is running and has an authenticated account
      const currentAccount = await this.adapter.getCurrentAccount();
      if (!currentAccount || !currentAccount.email) {
        throw new AccountEnrollmentError(
          'Cannot enroll: Antigravity 2 is either offline or no authenticated account was detected.'
        );
      }

      // 2. Read live session blob from Windows Credential Manager
      const cred = await this.wincredReader.readCredential(DEFAULT_AG2_WINCRED_TARGET);
      if (!cred || !cred.blob || cred.blob.length === 0) {
        throw new AccountEnrollmentError(
          `Cannot enroll: No Windows Credential found for target '${DEFAULT_AG2_WINCRED_TARGET}'. Please ensure you are logged into Antigravity 2.`
        );
      }

      // 3. Inspect session blob structure safely
      try {
        const parsedBlob = JSON.parse(cred.blob.toString('utf8')) as { token?: string; auth_method?: string };
        if (!parsedBlob.token && !parsedBlob.auth_method) {
          throw new AccountEnrollmentError('Windows Credential blob does not contain valid Antigravity authentication data.');
        }
      } catch (parseErr) {
        if (parseErr instanceof AccountEnrollmentError) throw parseErr;
        throw new AccountEnrollmentError('Failed to parse Windows Credential JSON payload.');
      }

      // 4. Check if account already exists in AccountStore
      const email = currentAccount.email.trim();
      const existing = await this.accountStore.getAccountByEmail(email);

      let account: AccountMetadata;
      let isNew = false;

      if (existing) {
        // Update existing account
        const updated = await this.accountStore.updateAccount(existing.id, {
          name: options.name ?? existing.name ?? currentAccount.name ?? undefined,
          priority: options.priority ?? existing.priority,
          isReserve: options.isReserve ?? existing.isReserve,
          validationStatus: 'VALID',
          hasVaultedSession: true,
          lastActiveAt: new Date().toISOString(),
          notes: options.notes ?? existing.notes
        });

        if (!updated) {
          throw new AccountEnrollmentError(`Failed to update existing account record for '${email}'.`);
        }
        account = updated;
      } else {
        // Register new account
        isNew = true;
        account = await this.accountStore.addAccount({
          email,
          name: options.name ?? currentAccount.name ?? undefined,
          priority: options.priority,
          isReserve: options.isReserve,
          hasVaultedSession: true,
          notes: options.notes
        });

        // Set validationStatus to VALID
        const validated = await this.accountStore.updateAccount(account.id, {
          validationStatus: 'VALID',
          lastActiveAt: new Date().toISOString()
        });
        if (validated) {
          account = validated;
        }
      }

      // 5. Encrypt and save session into the session vault
      await this.sessionVault.saveSession(account.id, cred.blob, cred.target);

      // 6. Mark as active account in account store
      await this.accountStore.setActiveAccountId(account.id);

      return {
        success: true,
        account,
        isNew,
        message: isNew
          ? `Successfully enrolled new account '${email}' into encrypted vault.`
          : `Successfully updated session vault for existing account '${email}'.`
      };
    } catch (err) {
      const sanitized = sanitizeError(err);
      throw new AccountEnrollmentError(redactSensitiveText(sanitized.message));
    }
  }
}
