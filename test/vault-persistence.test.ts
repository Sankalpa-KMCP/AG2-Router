import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, it } from 'node:test';
import { SessionVault } from '../src/vault/session-vault.js';
import { VaultCorruptionError, VAULT_MAGIC } from '../src/vault/types.js';
import { IDpapiProvider } from '../src/vault/dpapi.js';

// Mock DPAPI provider for deterministic unit testing without PowerShell latency
class MockDpapiProvider implements IDpapiProvider {
  public async encrypt(plaintext: Buffer): Promise<Buffer> {
    const copy = Buffer.from(plaintext);
    plaintext.fill(0); // simulate best-effort zeroing
    // Simple reversible mock cipher: prepend 'MOCK_ENC:'
    return Buffer.concat([Buffer.from('MOCK_ENC:'), copy]);
  }

  public async decrypt(ciphertext: Buffer): Promise<Buffer> {
    const prefix = 'MOCK_ENC:';
    if (!ciphertext.toString('utf8').startsWith(prefix)) {
      throw new Error('Corrupted or invalid mock ciphertext');
    }
    return Buffer.from(ciphertext.subarray(Buffer.from(prefix).length));
  }
}

describe('SessionVault (Encrypted Multi-Account Session Persistence)', () => {
  it('should save and retrieve an account session successfully', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-test-'));
    try {
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const rawSession = Buffer.from(JSON.stringify({ token: 'test-oauth-token-123', email: 'user@example.com' }), 'utf8');

      await vault.saveSession('acc-1', rawSession);

      assert.strictEqual(await vault.hasSession('acc-1'), true);
      assert.strictEqual(await vault.hasSession('acc-2'), false);

      const retrieved = await vault.getSession('acc-1');
      assert(retrieved !== null);
      assert.strictEqual(retrieved.toString('utf8'), rawSession.toString('utf8'));

      // Check stored file on disk
      const filePath = vault.getVaultPath();
      assert.strictEqual(fs.existsSync(filePath), true);
      const rawFile = JSON.parse(fs.readFileSync(filePath, 'utf8'));
      assert.strictEqual(rawFile.magic, VAULT_MAGIC);
      assert.strictEqual(rawFile.schemaVersion, 1);
      assert(rawFile.records['acc-1']);
      // Raw token should NOT appear in plaintext on disk
      assert.strictEqual(fs.readFileSync(filePath, 'utf8').includes('test-oauth-token-123'), false);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should support multiple stored accounts and update without duplicates', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-test-'));
    try {
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const sessionA = Buffer.from(JSON.stringify({ token: 'token-a' }), 'utf8');
      const sessionB = Buffer.from(JSON.stringify({ token: 'token-b' }), 'utf8');

      await vault.saveSession('acc-a', sessionA);
      await vault.saveSession('acc-b', sessionB);

      const storedIds = await vault.listStoredAccountIds();
      assert.strictEqual(storedIds.length, 2);
      assert(storedIds.includes('acc-a'));
      assert(storedIds.includes('acc-b'));

      // Update session A with new token
      const sessionA2 = Buffer.from(JSON.stringify({ token: 'token-a-updated' }), 'utf8');
      await vault.saveSession('acc-a', sessionA2);

      const storedIdsAfter = await vault.listStoredAccountIds();
      assert.strictEqual(storedIdsAfter.length, 2); // No duplicate keys
      const retrievedA2 = await vault.getSession('acc-a');
      assert(retrievedA2 !== null);
      assert.strictEqual(retrievedA2.toString('utf8'), sessionA2.toString('utf8'));
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should remove account sessions cleanly', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-test-'));
    try {
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      await vault.saveSession('acc-x', Buffer.from('payload-x'));

      assert.strictEqual(await vault.hasSession('acc-x'), true);
      const removed = await vault.removeSession('acc-x');
      assert.strictEqual(removed, true);
      assert.strictEqual(await vault.hasSession('acc-x'), false);

      const removedAgain = await vault.removeSession('acc-x');
      assert.strictEqual(removedAgain, false);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should fail closed and preserve corrupted vault files without wiping or overwriting', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-test-'));
    try {
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      const corruptedContent = 'THIS IS CORRUPTED DATA NOT JSON';
      fs.writeFileSync(vault.getVaultPath(), corruptedContent, 'utf8');

      // Attempting to read should throw VaultCorruptionError
      await assert.rejects(
        async () => {
          await vault.getSession('acc-1');
        },
        (err: unknown) => {
          assert(err instanceof VaultCorruptionError);
          return true;
        }
      );

      // Attempting to save should also fail closed and NOT overwrite the corrupted file
      await assert.rejects(
        async () => {
          await vault.saveSession('acc-2', Buffer.from('payload-2'));
        },
        (err: unknown) => {
          assert(err instanceof VaultCorruptionError);
          return true;
        }
      );

      // File content must remain intact for forensic recovery
      assert.strictEqual(fs.readFileSync(vault.getVaultPath(), 'utf8'), corruptedContent);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should fail closed when internal identity framing is violated (record swap detection)', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-test-'));
    try {
      const dpapi = new MockDpapiProvider();
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: dpapi });

      // Save acc-1
      await vault.saveSession('acc-1', Buffer.from('secret-payload-1'));

      // Artificially swap record key in the file: copy acc-1's ciphertext to acc-2
      const envelope = JSON.parse(fs.readFileSync(vault.getVaultPath(), 'utf8'));
      envelope.records['acc-2'] = {
        ...envelope.records['acc-1'],
        accountId: 'acc-2' // Changed envelope key/metadata, but internal ciphertext has 'acc-1'
      };
      fs.writeFileSync(vault.getVaultPath(), JSON.stringify(envelope));

      // Attempting to retrieve acc-2 should fail closed due to identity framing violation
      await assert.rejects(
        async () => {
          await vault.getSession('acc-2');
        },
        (err: unknown) => {
          assert(err instanceof VaultCorruptionError);
          assert((err as Error).message.includes('Identity framing violation'));
          return true;
        }
      );
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should work with real WindowsDpapiProvider in an isolated directory', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-real-test-'));
    try {
      const vault = new SessionVault({ vaultDir: tempDir });
      const payload = Buffer.from(JSON.stringify({ testKey: 'real-dpapi-test-value-123' }), 'utf8');

      await vault.saveSession('acc-real', payload);
      assert.strictEqual(await vault.hasSession('acc-real'), true);

      // Reload into a separate vault instance pointing to same dir
      const vault2 = new SessionVault({ vaultDir: tempDir });
      const retrieved = await vault2.getSession('acc-real');
      assert(retrieved !== null);
      assert.strictEqual(retrieved.toString('utf8'), payload.toString('utf8'));
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
