import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, it } from 'node:test';
import { AccountEnrollmentError, AccountEnrollmentService } from '../src/accounts/enrollment.js';
import { InMemoryAccountStore } from '../src/accounts/account-store.js';
import { AccountMetadata, CreateAccountInput, EnrollmentAccountCommit, IAccountStore, UpdateAccountInput } from '../src/accounts/types.js';
import { SessionVault } from '../src/vault/session-vault.js';
import { IDpapiProvider } from '../src/vault/dpapi.js';
import { IAG2Adapter } from '../src/ag2/adapter.js';
import {
  AG2DiscoveryResult,
  AG2AccountIdentity,
  QuotaSnapshot,
  ActivitySnapshot,
  SwitchRequest,
  SwitchResult
} from '../src/ag2/types.js';
import { IWinCredReader, WinCredEntry } from '../src/ag2/wincred.js';

class MockDpapiProvider implements IDpapiProvider {
  public async encrypt(plaintext: Buffer): Promise<Buffer> {
    return Buffer.concat([Buffer.from('MOCK_ENC:'), plaintext]);
  }
  public async decrypt(ciphertext: Buffer): Promise<Buffer> {
    const prefix = 'MOCK_ENC:';
    return Buffer.from(ciphertext.subarray(Buffer.from(prefix).length));
  }
}

class MockAG2Adapter implements IAG2Adapter {
  constructor(private accountIdentity: AG2AccountIdentity | null = null) {}

  public async discover(): Promise<AG2DiscoveryResult> {
    return { isRunning: Boolean(this.accountIdentity), status: this.accountIdentity ? 'HEALTHY' : 'OFFLINE', processInfo: null };
  }
  public async getCurrentAccount(): Promise<AG2AccountIdentity | null> {
    return this.accountIdentity;
  }
  public async getQuota(): Promise<QuotaSnapshot | null> {
    return null;
  }
  public async getActivityState(): Promise<ActivitySnapshot> {
    return { state: 'IDLE', totalTrajectories: 0, runningTrajectories: 0, timestamp: new Date().toISOString() };
  }
  public async switchAccount(_request: SwitchRequest): Promise<SwitchResult> {
    throw new Error('Not implemented');
  }
  public async verifyAccount(): Promise<boolean> {
    return true;
  }
}

class MockWinCredReader implements IWinCredReader {
  constructor(private entry: WinCredEntry | null = null) {}
  public async readCredential(): Promise<WinCredEntry | null> {
    return this.entry;
  }
}

class FailUpdateAccountStore implements IAccountStore {
  public async finalizeEnrollment(): Promise<EnrollmentAccountCommit> {
    throw new Error('injected metadata commit failure');
  }
  constructor(private readonly inner: IAccountStore) {}
  public listAccounts(): Promise<AccountMetadata[]> { return this.inner.listAccounts(); }
  public getAccount(id: string): Promise<AccountMetadata | null> { return this.inner.getAccount(id); }
  public getAccountByEmail(email: string): Promise<AccountMetadata | null> { return this.inner.getAccountByEmail(email); }
  public addAccount(input: CreateAccountInput): Promise<AccountMetadata> { return this.inner.addAccount(input); }
  public async updateAccount(_id: string, _updates: UpdateAccountInput): Promise<AccountMetadata | null> {
    throw new Error('injected metadata commit failure');
  }
  public removeAccount(id: string): Promise<boolean> { return this.inner.removeAccount(id); }
  public removeAccountIfUnchanged(expected: AccountMetadata): Promise<boolean> {
    return this.inner.removeAccountIfUnchanged(expected);
  }
  public getActiveAccountId(): Promise<string | null> { return this.inner.getActiveAccountId(); }
  public setActiveAccountId(id: string | null): Promise<void> { return this.inner.setActiveAccountId(id); }
  public compareExchangeActiveAccountId(expectedId: string | null, newId: string | null): Promise<boolean> {
    return this.inner.compareExchangeActiveAccountId(expectedId, newId);
  }
}

class BlockingFailUpdateAccountStore implements IAccountStore {
  public async finalizeEnrollment(): Promise<EnrollmentAccountCommit> {
    this.signalEntered();
    await this.release;
    throw new Error('injected delayed metadata commit failure');
  }
  public readonly updateEntered: Promise<void>;
  private signalEntered!: () => void;
  private readonly release: Promise<void>;
  private signalRelease!: () => void;

  constructor(private readonly inner: IAccountStore) {
    this.updateEntered = new Promise((resolve) => { this.signalEntered = resolve; });
    this.release = new Promise((resolve) => { this.signalRelease = resolve; });
  }
  public releaseFailure(): void { this.signalRelease(); }
  public listAccounts(): Promise<AccountMetadata[]> { return this.inner.listAccounts(); }
  public getAccount(id: string): Promise<AccountMetadata | null> { return this.inner.getAccount(id); }
  public getAccountByEmail(email: string): Promise<AccountMetadata | null> { return this.inner.getAccountByEmail(email); }
  public addAccount(input: CreateAccountInput): Promise<AccountMetadata> { return this.inner.addAccount(input); }
  public async updateAccount(_id: string, _updates: UpdateAccountInput): Promise<AccountMetadata | null> {
    this.signalEntered();
    await this.release;
    throw new Error('injected delayed metadata commit failure');
  }
  public removeAccount(id: string): Promise<boolean> { return this.inner.removeAccount(id); }
  public removeAccountIfUnchanged(expected: AccountMetadata): Promise<boolean> {
    return this.inner.removeAccountIfUnchanged(expected);
  }
  public getActiveAccountId(): Promise<string | null> { return this.inner.getActiveAccountId(); }
  public setActiveAccountId(id: string | null): Promise<void> { return this.inner.setActiveAccountId(id); }
  public compareExchangeActiveAccountId(expectedId: string | null, newId: string | null): Promise<boolean> {
    return this.inner.compareExchangeActiveAccountId(expectedId, newId);
  }
}

class RejectActiveCasAccountStore implements IAccountStore {
  public finalizeEnrollment(expected: AccountMetadata, updates: UpdateAccountInput,
    _expectedActiveId: string | null, verifyCoherence: () => Promise<void>): Promise<EnrollmentAccountCommit> {
    return this.inner.finalizeEnrollment!(expected, updates, 'synthetic-concurrent-selection', verifyCoherence);
  }
  constructor(private readonly inner: IAccountStore) {}
  public listAccounts(): Promise<AccountMetadata[]> { return this.inner.listAccounts(); }
  public getAccount(id: string): Promise<AccountMetadata | null> { return this.inner.getAccount(id); }
  public getAccountByEmail(email: string): Promise<AccountMetadata | null> { return this.inner.getAccountByEmail(email); }
  public addAccount(input: CreateAccountInput): Promise<AccountMetadata> { return this.inner.addAccount(input); }
  public updateAccount(id: string, updates: UpdateAccountInput): Promise<AccountMetadata | null> {
    return this.inner.updateAccount(id, updates);
  }
  public removeAccount(id: string): Promise<boolean> { return this.inner.removeAccount(id); }
  public removeAccountIfUnchanged(expected: AccountMetadata): Promise<boolean> {
    return this.inner.removeAccountIfUnchanged(expected);
  }
  public getActiveAccountId(): Promise<string | null> { return this.inner.getActiveAccountId(); }
  public setActiveAccountId(id: string | null): Promise<void> { return this.inner.setActiveAccountId(id); }
  public async compareExchangeActiveAccountId(): Promise<boolean> { return false; }
}

describe('AccountEnrollmentService', () => {
  it('should reject enrollment when Antigravity 2 is offline', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-test-'));
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const adapter = new MockAG2Adapter(null); // Offline
      const wincred = new MockWinCredReader({
        target: 'gemini:antigravity',
        type: 1,
        userName: 'antigravity',
        persistence: 2,
        blob: Buffer.from(JSON.stringify({ token: 'tok_123' }))
      });

      const service = new AccountEnrollmentService({ adapter, wincredReader: wincred, sessionVault: vault, accountStore: store });

      await assert.rejects(
        async () => {
          await service.enrollCurrentAccount();
        },
        (err: unknown) => {
          assert(err instanceof AccountEnrollmentError);
          assert((err as Error).message.includes('Antigravity 2 is either offline'));
          return true;
        }
      );
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should reject enrollment when Windows Credential is not found', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-test-'));
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const adapter = new MockAG2Adapter({ email: 'user@example.com', name: 'User' });
      const wincred = new MockWinCredReader(null); // No credential in WinCred

      const service = new AccountEnrollmentService({ adapter, wincredReader: wincred, sessionVault: vault, accountStore: store });

      await assert.rejects(
        async () => {
          await service.enrollCurrentAccount();
        },
        (err: unknown) => {
          assert(err instanceof AccountEnrollmentError);
          assert((err as Error).message.includes('No Windows Credential found'));
          return true;
        }
      );
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should enroll a new account cleanly and store session in vault', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-test-'));
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const adapter = new MockAG2Adapter({ email: 'dev@example.com', name: 'Developer', tierName: 'PRO' });
      const rawPayload = JSON.stringify({ token: 'secret-oauth-tok-999', auth_method: 'oauth' });
      const wincred = new MockWinCredReader({
        target: 'gemini:antigravity',
        type: 1,
        userName: 'antigravity',
        persistence: 2,
        blob: Buffer.from(rawPayload, 'utf8')
      });

      const service = new AccountEnrollmentService({ adapter, wincredReader: wincred, sessionVault: vault, accountStore: store });

      const result = await service.enrollCurrentAccount({ priority: 1, isReserve: false });
      assert.strictEqual(result.success, true);
      assert.strictEqual(result.isNew, true);
      assert.strictEqual(result.account.email, 'dev@example.com');
      assert.strictEqual(result.account.hasVaultedSession, true);
      assert.strictEqual(result.account.validationStatus, 'VALID');

      // Check active account
      const activeId = await store.getActiveAccountId();
      assert.strictEqual(activeId, result.account.id);

      // Verify session exists in vault
      const storedBlob = await vault.getSession(result.account.id);
      assert(storedBlob !== null);
      assert.strictEqual(storedBlob.toString('utf8'), rawPayload);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should enroll a new account with alias and persist in store', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-test-'));
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const adapter = new MockAG2Adapter({ email: 'work@example.com', name: 'Work User', tierName: 'PRO' });
      const rawPayload = JSON.stringify({ token: 'work-tok-123', auth_method: 'oauth' });
      const wincred = new MockWinCredReader({
        target: 'gemini:antigravity',
        type: 1,
        userName: 'antigravity',
        persistence: 2,
        blob: Buffer.from(rawPayload, 'utf8')
      });

      const service = new AccountEnrollmentService({ adapter, wincredReader: wincred, sessionVault: vault, accountStore: store });

      const result = await service.enrollCurrentAccount({ alias: 'Work Account', priority: 1 });
      assert.strictEqual(result.success, true);
      assert.strictEqual(result.account.alias, 'Work Account');

      const retrieved = await store.getAccount(result.account.id);
      assert.ok(retrieved);
      assert.strictEqual(retrieved.alias, 'Work Account');
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should update existing account when enrolling duplicate email without creating a second record', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-test-'));
    try {
      const store = new InMemoryAccountStore();
      // Pre-register account
      const preExisting = await store.addAccount({ email: 'existing@example.com', priority: 5 });

      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const adapter = new MockAG2Adapter({ email: 'existing@example.com', name: 'Existing User' });
      const updatedPayload = JSON.stringify({ token: 'new-refreshed-token-444', auth_method: 'oauth' });
      const wincred = new MockWinCredReader({
        target: 'gemini:antigravity',
        type: 1,
        userName: 'antigravity',
        persistence: 2,
        blob: Buffer.from(updatedPayload, 'utf8')
      });

      const service = new AccountEnrollmentService({ adapter, wincredReader: wincred, sessionVault: vault, accountStore: store });

      const result = await service.enrollCurrentAccount({ priority: 2 });
      assert.strictEqual(result.success, true);
      assert.strictEqual(result.isNew, false);
      assert.strictEqual(result.account.id, preExisting.id); // Same ID!
      assert.strictEqual(result.account.priority, 2);
      assert.strictEqual(result.account.hasVaultedSession, true);

      // Verify total accounts is still 1
      const allAccounts = await store.listAccounts();
      assert.strictEqual(allAccounts.length, 1);

      // Verify updated session is in vault
      const storedBlob = await vault.getSession(preExisting.id);
      assert(storedBlob !== null);
      assert.strictEqual(storedBlob.toString('utf8'), updatedPayload);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('does not expose vaulted metadata when vault persistence fails', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-failure-test-'));
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({
        vaultDir: tempDir,
        dpapiProvider: new MockDpapiProvider(),
        atomicWriter: async () => { throw new Error('injected vault persistence failure'); }
      });
      const service = new AccountEnrollmentService({
        adapter: new MockAG2Adapter({ email: 'vault-failure@example.com' }),
        wincredReader: new MockWinCredReader({
          target: 'gemini:antigravity', type: 1, userName: 'synthetic', persistence: 2,
          blob: Buffer.from('{"token":"synthetic"}')
        }),
        sessionVault: vault,
        accountStore: store
      });

      await assert.rejects(() => service.enrollCurrentAccount());
      assert.equal((await store.listAccounts()).length, 0);
      assert.equal(await store.getActiveAccountId(), null);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('compensates metadata failure after vault commit and permits retry', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-compensation-test-'));
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const adapter = new MockAG2Adapter({ email: 'metadata-failure@example.com' });
      const wincred = new MockWinCredReader({
        target: 'gemini:antigravity', type: 1, userName: 'synthetic', persistence: 2,
        blob: Buffer.from('{"token":"synthetic"}')
      });
      const failing = new AccountEnrollmentService({
        adapter, wincredReader: wincred, sessionVault: vault,
        accountStore: new FailUpdateAccountStore(store)
      });

      await assert.rejects(() => failing.enrollCurrentAccount());
      assert.equal((await store.listAccounts()).length, 0);
      assert.equal((await vault.listStoredAccountIds()).length, 0);

      const retry = await new AccountEnrollmentService({
        adapter, wincredReader: wincred, sessionVault: vault, accountStore: store
      }).enrollCurrentAccount();
      assert.equal(retry.account.hasVaultedSession, true);
      assert.equal(await vault.hasSession(retry.account.id), true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('serializes concurrent duplicate enrollment into one account', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-concurrency-test-'));
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const service = new AccountEnrollmentService({
        adapter: new MockAG2Adapter({ email: 'concurrent@example.com' }),
        wincredReader: new MockWinCredReader({
          target: 'gemini:antigravity', type: 1, userName: 'synthetic', persistence: 2,
          blob: Buffer.from('{"token":"synthetic"}')
        }),
        sessionVault: vault,
        accountStore: store
      });

      const results = await Promise.all([service.enrollCurrentAccount(), service.enrollCurrentAccount()]);
      assert.equal((await store.listAccounts()).length, 1);
      assert.equal(results.filter((result) => result.isNew).length, 1);
      assert.equal((await vault.listStoredAccountIds()).length, 1);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('serializes different-account enrollment through one commit lane', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-different-race-test-'));
    let currentEncryptions = 0;
    let maxConcurrentEncryptions = 0;
    const trackingDpapi: IDpapiProvider = {
      async encrypt(plaintext: Buffer): Promise<Buffer> {
        currentEncryptions += 1;
        maxConcurrentEncryptions = Math.max(maxConcurrentEncryptions, currentEncryptions);
        try {
          await new Promise((resolve) => setTimeout(resolve, 25));
          return Buffer.concat([Buffer.from('TRACK:'), plaintext]);
        } finally {
          currentEncryptions -= 1;
        }
      },
      async decrypt(ciphertext: Buffer): Promise<Buffer> {
        return Buffer.from(ciphertext.subarray(Buffer.byteLength('TRACK:')));
      }
    };
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: trackingDpapi });
      const credential = new MockWinCredReader({
        target: 'gemini:antigravity', type: 1, userName: 'synthetic', persistence: 2,
        blob: Buffer.from('{"token":"synthetic"}')
      });
      const first = new AccountEnrollmentService({
        adapter: new MockAG2Adapter({ email: 'first-lane@example.com' }),
        wincredReader: credential, sessionVault: vault, accountStore: store
      });
      const second = new AccountEnrollmentService({
        adapter: new MockAG2Adapter({ email: 'second-lane@example.com' }),
        wincredReader: credential, sessionVault: vault, accountStore: store
      });

      const results = await Promise.all([first.enrollCurrentAccount(), second.enrollCurrentAccount()]);
      assert.equal(results.length, 2);
      assert.equal((await store.listAccounts()).length, 2);
      assert.equal((await vault.listStoredAccountIds()).length, 2);
      assert.equal(maxConcurrentEncryptions, 1);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('does not overwrite a newer vault session during compensation', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-vault-race-test-'));
    try {
      const store = new InMemoryAccountStore();
      const account = await store.addAccount({ email: 'vault-race@example.com', hasVaultedSession: true });
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      await vault.saveSession(account.id, Buffer.from('{"token":"original-synthetic"}'));
      const blockingStore = new BlockingFailUpdateAccountStore(store);
      const service = new AccountEnrollmentService({
        adapter: new MockAG2Adapter({ email: 'vault-race@example.com' }),
        wincredReader: new MockWinCredReader({
          target: 'gemini:antigravity', type: 1, userName: 'synthetic', persistence: 2,
          blob: Buffer.from('{"token":"enrollment-synthetic"}')
        }),
        sessionVault: vault,
        accountStore: blockingStore
      });

      const enrollment = service.enrollCurrentAccount();
      await blockingStore.updateEntered;
      const newer = Buffer.from('{"token":"newer-synthetic"}');
      // Finalization owns the vault; this writer may proceed only after failure
      // releases that ownership. Conditional compensation must preserve its result.
      const newerWrite = vault.saveSession(account.id, newer);
      const rejected = assert.rejects(enrollment);
      blockingStore.releaseFailure();

      await Promise.all([rejected, newerWrite]);
      assert.deepEqual(await vault.getSession(account.id), newer);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('preserves a concurrent active choice without rolling back the core enrollment commit', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-active-choice-test-'));
    try {
      const store = new InMemoryAccountStore();
      const previous = await store.addAccount({ email: 'active-choice@example.com' });
      await store.setActiveAccountId(previous.id);
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const service = new AccountEnrollmentService({
        adapter: new MockAG2Adapter({ email: 'committed-enrollment@example.com' }),
        wincredReader: new MockWinCredReader({
          target: 'gemini:antigravity', type: 1, userName: 'synthetic', persistence: 2,
          blob: Buffer.from('{"token":"synthetic"}')
        }),
        sessionVault: vault,
        accountStore: new RejectActiveCasAccountStore(store)
      });

      const result = await service.enrollCurrentAccount();

      assert.equal(result.success, true);
      assert.equal(result.account.hasVaultedSession, true);
      assert.equal(await vault.hasSession(result.account.id), true);
      assert.equal(await store.getActiveAccountId(), previous.id);
      assert.match(result.message, /preserved/i);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('does not delete a placeholder changed by concurrent CRUD during compensation', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-enroll-placeholder-race-test-'));
    try {
      const store = new InMemoryAccountStore();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const blockingStore = new BlockingFailUpdateAccountStore(store);
      const service = new AccountEnrollmentService({
        adapter: new MockAG2Adapter({ email: 'placeholder-race@example.com' }),
        wincredReader: new MockWinCredReader({
          target: 'gemini:antigravity', type: 1, userName: 'synthetic', persistence: 2,
          blob: Buffer.from('{"token":"synthetic"}')
        }),
        sessionVault: vault,
        accountStore: blockingStore
      });

      const enrollment = service.enrollCurrentAccount();
      await blockingStore.updateEntered;
      const pending = await store.getAccountByEmail('placeholder-race@example.com');
      assert(pending);
      await store.updateAccount(pending.id, { notes: 'concurrent-crud' });
      blockingStore.releaseFailure();

      await assert.rejects(() => enrollment);
      const preserved = await store.getAccount(pending.id);
      assert(preserved);
      assert.equal(preserved.notes, 'concurrent-crud');
      assert.equal(preserved.hasVaultedSession, false);
      assert.equal(await vault.hasSession(pending.id), false);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
