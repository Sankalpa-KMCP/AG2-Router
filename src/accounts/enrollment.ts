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
import { VaultMutationReceipt } from '../vault/types.js';
import { withCoordinatedFileAccess } from '../persistence/file-coordination.js';
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

const enrollmentLocks = new Map<string, Promise<void>>();

async function withEnrollmentLock<T>(email: string, action: () => Promise<T>): Promise<T> {
  const key = email.trim().toLowerCase();
  const previous = enrollmentLocks.get(key) ?? Promise.resolve();
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const tail = previous.catch(() => undefined).then(() => gate);
  enrollmentLocks.set(key, tail);
  await previous.catch(() => undefined);
  try {
    return await action();
  } finally {
    release();
    if (enrollmentLocks.get(key) === tail) enrollmentLocks.delete(key);
  }
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

      const email = currentAccount.email.trim();
      return await withEnrollmentLock(email, async () => {
        const enrollmentResource = `${this.sessionVault.getVaultPath()}.enrollment`;
        return withCoordinatedFileAccess(enrollmentResource, async () => {
        // Recheck inside the identity lock so concurrent duplicate enrollment is idempotent.
        const existing = await this.accountStore.getAccountByEmail(email);
        const previousActiveId = await this.accountStore.getActiveAccountId();
        const isNew = existing === null;
        const pending = existing ?? await this.accountStore.addAccount({
          email,
          name: options.name ?? currentAccount.name ?? undefined,
          priority: options.priority,
          isReserve: options.isReserve,
          hasVaultedSession: false,
          notes: options.notes
        });

        let vaultReceipt: VaultMutationReceipt | null = null;
        let vaultCommitted = false;
        let account: AccountMetadata;
        try {
          // Vault-first: HasVaultedSession remains unchanged/false until this succeeds.
          vaultReceipt = await this.sessionVault.saveSessionWithReceipt(pending.id, cred.blob, cred.target);
          vaultCommitted = true;

          const committed = await this.accountStore.updateAccount(pending.id, {
            name: options.name,
            priority: options.priority,
            isReserve: options.isReserve,
            validationStatus: 'VALID',
            hasVaultedSession: true,
            lastActiveAt: new Date().toISOString(),
            notes: options.notes
          });
          if (!committed) throw new AccountEnrollmentError(`Failed to update account record for '${email}'.`);
          account = committed;
        } catch (error) {
          if (vaultCommitted) {
            try {
              await this.sessionVault.restoreIfCurrent(vaultReceipt!);
            } catch {}
          }
          if (isNew) {
            try { await this.accountStore.removeAccountIfUnchanged(pending); } catch {}
          }
          throw error;
        }

        // Core enrollment is committed. Preserve a concurrent active-account choice and avoid
        // compensating a later selection through a value-only CAS rollback.
        let activeUpdated = false;
        try {
          activeUpdated = await this.accountStore.compareExchangeActiveAccountId(previousActiveId, pending.id);
        } catch {}

        const baseMessage = isNew
          ? `Successfully enrolled new account '${email}' into encrypted vault.`
          : `Successfully updated session vault for existing account '${email}'.`;
        return {
          success: true,
          account,
          isNew,
          message: activeUpdated
            ? baseMessage
            : `${baseMessage} The current active-account selection was preserved.`
        };
        });
      });
    } catch (err) {
      const sanitized = sanitizeError(err);
      throw new AccountEnrollmentError(redactSensitiveText(sanitized.message));
    }
  }
}
