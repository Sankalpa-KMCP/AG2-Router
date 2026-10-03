import { validateAccountText } from './text-validation.js';
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

import { timingSafeEqual } from 'node:crypto';
import { IAG2Adapter } from '../ag2/adapter.js';
import { AG2AccountIdentity } from '../ag2/types.js';
import { redactSensitiveText, sanitizeError } from '../ag2/security.js';
import { IWinCredReader, WinCredEntry, DEFAULT_AG2_WINCRED_TARGET } from '../ag2/wincred.js';
import { SessionVault } from '../vault/session-vault.js';
import { VaultMutationReceipt, VaultMutationUncertainError } from '../vault/types.js';
import { withCoordinatedFileAccess } from '../persistence/file-coordination.js';
import { AccountMetadata, EnrollmentAccountCommit, EnrollmentCommitUncertainError, IAccountStore } from './types.js';

export interface EnrollmentOptions {
  readonly name?: string;
  readonly alias?: string;
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
   * Enrolls the currently active Antigravity 2 account session into the local vault (R03 Coherence Protocol).
   * Updates existing metadata without duplicates if the account is already registered.
   *
   * ARCHITECTURAL COHERENCE & MUTATION INVARIANTS (R03):
   * 1. Dual-tier lock: in-memory lock (withEnrollmentLock) + cross-process lease (.enrollment).
   * 2. Identity & credential revalidation: evaluated before lock, re-checked under lock.
   * 3. Multi-phase verifyCoherence: evaluated across every stage using timingSafeEqual byte checks.
   * 4. Conditional rollback: restoreIfCurrent ensures newer concurrent state is never clobbered.
   */
  public async enrollCurrentAccount(options: EnrollmentOptions = {}): Promise<EnrollmentResult> {
    const capture: { entry: WinCredEntry | null } = { entry: null };
    try {
      validateAccountText(options);
      const initial = await this.adapter.getCurrentAccount();
      const email = this.requireIdentity(initial);
      if (!this.accountStore.finalizeEnrollment)
        throw new AccountEnrollmentError('Account store does not support guarded enrollment finalization.');

      return await withEnrollmentLock(email, async () =>
        withCoordinatedFileAccess(`${this.sessionVault.getVaultPath()}.enrollment`, async () => {
          // The pre-lock observation selects the identity, but never authorizes capture.
          const current = await this.adapter.getCurrentAccount();
          if (this.requireIdentity(current).toLowerCase() !== email.toLowerCase())
            throw new AccountEnrollmentError('The active account changed before enrollment ownership was acquired.');
          const credential = await this.wincredReader.readCredential(DEFAULT_AG2_WINCRED_TARGET);
          this.validateCredential(credential);
          capture.entry = { ...credential!, blob: Buffer.from(credential!.blob) };
          const expected = capture.entry;
          let vaultReceipt: VaultMutationReceipt | null = null;
          let vaultOwned = false;
          // Coherence verification: executed before vault save, after vault save, and during metadata commit.
          // Confirms that neither live identity (getCurrentAccount) nor underlying Windows Credential
          // changed while holding the lock. Uses timingSafeEqual (bytesEqual) to prevent timing side-channels.
          const verifyCoherence = async (): Promise<void> => {
            if (vaultReceipt && !vaultOwned) {
              const persisted = await this.sessionVault.getSession(vaultReceipt.accountId);
              try {
                if (!persisted || !this.bytesEqual(persisted, expected.blob))
                  throw new AccountEnrollmentError('The vaulted session changed during enrollment.');
              } finally { persisted?.fill(0); }
            }
            const before = await this.adapter.getCurrentAccount();
            const fresh = await this.wincredReader.readCredential(DEFAULT_AG2_WINCRED_TARGET);
            const after = await this.adapter.getCurrentAccount();
            const finalCredential = await this.wincredReader.readCredential(DEFAULT_AG2_WINCRED_TARGET);
            this.validateCredential(fresh);
            this.validateCredential(finalCredential);
            if (this.requireIdentity(before).toLowerCase() !== email.toLowerCase() ||
                this.requireIdentity(after).toLowerCase() !== email.toLowerCase() ||
                fresh!.target !== expected.target || fresh!.type !== expected.type ||
                fresh!.userName !== expected.userName || fresh!.persistence !== expected.persistence ||
                !this.bytesEqual(fresh!.blob, expected.blob) ||
                finalCredential!.target !== expected.target || finalCredential!.type !== expected.type ||
                finalCredential!.userName !== expected.userName || finalCredential!.persistence !== expected.persistence ||
                !this.bytesEqual(finalCredential!.blob, expected.blob))
              throw new AccountEnrollmentError('The active identity or credential changed during enrollment.');
          };

          await verifyCoherence();
          const existing = await this.accountStore.getAccountByEmail(email);
          const previousActiveId = await this.accountStore.getActiveAccountId();
          const isNew = existing === null;
          const pending = existing ?? await this.accountStore.addAccount({
            email, name: options.name ?? current?.name, alias: options.alias,
            priority: options.priority, isReserve: options.isReserve,
            hasVaultedSession: false, notes: options.notes
          });
          let commit: EnrollmentAccountCommit;
          try {
            await verifyCoherence();
            vaultReceipt = await this.sessionVault.saveSessionWithReceipt(pending.id, expected.blob, expected.target);
            await verifyCoherence();
            commit = await this.sessionVault.withCurrentReceipt(vaultReceipt, async () => {
              vaultOwned = true;
              try {
                return await this.accountStore.finalizeEnrollment!(pending, {
                  name: options.name, alias: options.alias, priority: options.priority,
                  isReserve: options.isReserve, validationStatus: 'VALID',
                  hasVaultedSession: true, lastActiveAt: new Date().toISOString(), notes: options.notes
                }, previousActiveId, verifyCoherence);
              } finally { vaultOwned = false; }
            });
          } catch (error) {
            // Uncertain metadata must retain its session until an operator reconciles it.
            if (error instanceof EnrollmentCommitUncertainError || error instanceof VaultMutationUncertainError)
              throw error;
            if (vaultReceipt) {
              try {
                if (!await this.sessionVault.restoreIfCurrent(vaultReceipt))
                  throw new Error('Vault record changed.');
              } catch {
                throw new AccountEnrollmentError('Enrollment vault compensation could not be proved; manual recovery is required.');
              }
            }
            if (isNew) {
              try {
                if (!await this.accountStore.removeAccountIfUnchanged(pending))
                  throw new Error('Placeholder changed.');
              } catch {
                throw new AccountEnrollmentError('Enrollment metadata compensation could not be proved; manual recovery is required.');
              }
            }
            throw error;
          }
          const baseMessage = isNew
            ? `Successfully enrolled new account '${email}' into encrypted vault.`
            : `Successfully updated session vault for existing account '${email}'.`;
          return {
            success: true, account: commit.account, isNew,
            message: commit.activeUpdated ? baseMessage
              : `${baseMessage} The current active-account selection was preserved.`
          };
        }));
    } catch (err) {
      throw new AccountEnrollmentError(redactSensitiveText(sanitizeError(err).message));
    } finally {
      capture.entry?.blob.fill(0);
    }
  }

  private requireIdentity(identity: AG2AccountIdentity | null): string {
    const email = identity?.email;
    if (typeof email !== 'string' || !/^[^\s@]+@[^\s@]+$/.test(email.trim()))
      throw new AccountEnrollmentError('Cannot enroll: Antigravity 2 is either offline or no valid authenticated account was detected.');
    return email.trim();
  }

  private validateCredential(credential: WinCredEntry | null): void {
    if (!credential || !Buffer.isBuffer(credential.blob) || credential.blob.length === 0)
      throw new AccountEnrollmentError('Cannot enroll: No Windows Credential found for the Antigravity target.');
    if (credential.target !== DEFAULT_AG2_WINCRED_TARGET || credential.type !== 1 ||
        typeof credential.userName !== 'string' || !credential.userName.trim() ||
        credential.userName.length > 513 || credential.userName.includes('\0') ||
        ![1, 2, 3].includes(credential.persistence))
      throw new AccountEnrollmentError('Cannot enroll: the current Windows credential is missing or structurally invalid.');
    try {
      const payload: unknown = JSON.parse(credential.blob.toString('utf8'));
      if (!payload || typeof payload !== 'object' || Array.isArray(payload))
        throw new Error('Invalid authentication payload.');
      const fields = payload as Record<string, unknown>;
      if (![fields.token, fields.auth_method].some((value) => typeof value === 'string' && value.trim().length > 0))
        throw new Error('Invalid authentication payload.');
    } catch {
      throw new AccountEnrollmentError('Windows credential does not contain valid Antigravity authentication data.');
    }
  }

  private bytesEqual(left: Buffer, right: Buffer): boolean {
    return left.length === right.length && timingSafeEqual(left, right);
  }
}
