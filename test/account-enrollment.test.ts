import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, it } from 'node:test';
import { AccountEnrollmentError, AccountEnrollmentService } from '../src/accounts/enrollment.js';
import { InMemoryAccountStore } from '../src/accounts/account-store.js';
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
});
