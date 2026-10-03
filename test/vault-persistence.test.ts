import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, it } from 'node:test';
import { SessionVault } from '../src/vault/session-vault.js';
import { VaultCorruptionError, VaultMutationUncertainError, VAULT_MAGIC } from '../src/vault/types.js';
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

  const structurallyInvalidVaultContents: Array<[string, string]> = [
    ['zero-byte', ''],
    ['whitespace', '  \r\n'],
    ['truncated', '{"magic":"AG2_ROUTER_SESSION_VAULT"'],
    ['root-array', '[]'],
    ['root-string', '"not-an-envelope"'],
    ['root-number', '12345'],
    ['root-null', 'null'],
    ['magic-mismatch', JSON.stringify({ magic: 'WRONG_MAGIC', schemaVersion: 1, records: {} })],
    ['schema-version-mismatch', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 99, records: {} })],
    ['records-array', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1, records: [] })],
    ['records-null', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1, records: null })],
    ['records-string', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1, records: 'not-an-object' })],
    ['records-number', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1, records: 42 })],
    ['records-boolean', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1, records: true })],
    ['records-missing', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1 })],
    ['record-entry-array', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1, records: { 'acc-1': [] } })],
    ['record-entry-null', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1, records: { 'acc-1': null } })],
    ['record-entry-primitive', JSON.stringify({ magic: VAULT_MAGIC, schemaVersion: 1, records: { 'acc-1': 'string-record' } })],
    ['record-entry-missing-target', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'acc-1': { accountId: 'acc-1', encryptedPayloadBase64: 'dGVzdA==', createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z' } }
    })],
    ['record-entry-missing-payload', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'acc-1': { accountId: 'acc-1', target: 'gemini:antigravity', createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z' } }
    })],
    ['record-entry-missing-created-at', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'acc-1': { accountId: 'acc-1', target: 'gemini:antigravity', encryptedPayloadBase64: 'dGVzdA==', updatedAt: '2026-01-01T00:00:00.000Z' } }
    })],
    ['record-entry-missing-updated-at', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'acc-1': { accountId: 'acc-1', target: 'gemini:antigravity', encryptedPayloadBase64: 'dGVzdA==', createdAt: '2026-01-01T00:00:00.000Z' } }
    })],
    ['record-entry-non-string-field', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'acc-1': { accountId: 'acc-1', target: 123, encryptedPayloadBase64: 'dGVzdA==', createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z' } }
    })],
    ['record-entry-invalid-date', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'acc-1': { accountId: 'acc-1', target: 'gemini:antigravity', encryptedPayloadBase64: 'dGVzdA==', createdAt: 'not-a-valid-date', updatedAt: '2026-01-01T00:00:00.000Z' } }
    })],
    ['record-entry-mismatched-key', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'acc-1': { accountId: 'acc-different', target: 'gemini:antigravity', encryptedPayloadBase64: 'dGVzdA==', createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z' } }
    })],
    ['record-entry-forbidden-prototype-key',
      '{"magic":"AG2_ROUTER_SESSION_VAULT","schemaVersion":1,"records":{"__proto__":{"accountId":"__proto__","target":"gemini:antigravity","encryptedPayloadBase64":"dGVzdA==","createdAt":"2026-01-01T00:00:00.000Z","updatedAt":"2026-01-01T00:00:00.000Z"}}}'
    ],
    ['record-entry-forbidden-constructor-key', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'constructor': { accountId: 'constructor', target: 'gemini:antigravity', encryptedPayloadBase64: 'dGVzdA==', createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z' } }
    })],
    ['record-entry-forbidden-prototype-prop-key', JSON.stringify({
      magic: VAULT_MAGIC, schemaVersion: 1,
      records: { 'prototype': { accountId: 'prototype', target: 'gemini:antigravity', encryptedPayloadBase64: 'dGVzdA==', createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z' } }
    })]
  ];

  for (const [label, content] of structurallyInvalidVaultContents) {
    it(`should fail closed, throw VaultCorruptionError, and preserve bytes for ${label} vault`, async () => {
      const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-invalid-test-'));
      try {
        const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
        fs.writeFileSync(vault.getVaultPath(), content, 'utf8');
        const before = fs.readFileSync(vault.getVaultPath());

        // Read fails closed
        await assert.rejects(() => vault.getSession('acc-1'), (error: unknown) => error instanceof VaultCorruptionError);
        // Save fails closed
        await assert.rejects(() => vault.saveSession('acc-blocked', Buffer.from('synthetic')),
          (error: unknown) => error instanceof VaultCorruptionError);
        // Has session fails closed
        await assert.rejects(() => vault.hasSession('acc-1'), (error: unknown) => error instanceof VaultCorruptionError);
        // List stored accounts fails closed
        await assert.rejects(() => vault.listStoredAccountIds(), (error: unknown) => error instanceof VaultCorruptionError);
        // Remove session fails closed
        await assert.rejects(() => vault.removeSession('acc-1'), (error: unknown) => error instanceof VaultCorruptionError);

        // Bytes must remain strictly unchanged for forensic recovery
        assert.deepEqual(fs.readFileSync(vault.getVaultPath()), before);
      } finally {
        fs.rmSync(tempDir, { recursive: true, force: true });
      }
    });
  }

  it('creates a proper object dictionary records container when initializing a new vault file', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-new-test-'));
    try {
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      assert.strictEqual(fs.existsSync(vault.getVaultPath()), false);

      await vault.saveSession('acc-new', Buffer.from('payload-new'));

      assert.strictEqual(fs.existsSync(vault.getVaultPath()), true);
      const rawText = fs.readFileSync(vault.getVaultPath(), 'utf8');
      const parsed = JSON.parse(rawText);
      assert.strictEqual(parsed.magic, VAULT_MAGIC);
      assert.strictEqual(parsed.schemaVersion, 1);
      assert.strictEqual(typeof parsed.records, 'object');
      assert.strictEqual(parsed.records !== null, true);
      assert.strictEqual(Array.isArray(parsed.records), false);
      assert.strictEqual(parsed.records['acc-new'].accountId, 'acc-new');
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('fails with VaultCorruptionError and never returns success if atomicWriter writes an array records container', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-corrupt-writer-'));
    try {
      const vault = new SessionVault({
        vaultDir: tempDir,
        dpapiProvider: new MockDpapiProvider(),
        atomicWriter: async (filePath, content) => {
          // Simulate a writer that writes an array for records
          const obj = JSON.parse(content);
          obj.records = [];
          fs.writeFileSync(filePath, JSON.stringify(obj), 'utf8');
        }
      });

      await assert.rejects(
        () => vault.saveSessionWithReceipt('acc-1', Buffer.from('payload-1')),
        (err: unknown) => err instanceof VaultCorruptionError
      );
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('fails with VaultMutationUncertainError and never returns success if atomicWriter drops the saved record', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-drop-writer-'));
    try {
      const vault = new SessionVault({
        vaultDir: tempDir,
        dpapiProvider: new MockDpapiProvider(),
        atomicWriter: async (filePath, content) => {
          // Simulate a writer that drops the record from records dictionary
          const obj = JSON.parse(content);
          delete obj.records['acc-1'];
          fs.writeFileSync(filePath, JSON.stringify(obj), 'utf8');
        }
      });

      await assert.rejects(
        () => vault.saveSessionWithReceipt('acc-1', Buffer.from('payload-1')),
        (err: unknown) => err instanceof VaultMutationUncertainError
      );
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('fails with VaultMutationUncertainError and never returns success if atomicWriter alters the saved record', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-alter-writer-'));
    try {
      const vault = new SessionVault({
        vaultDir: tempDir,
        dpapiProvider: new MockDpapiProvider(),
        atomicWriter: async (filePath, content) => {
          const obj = JSON.parse(content);
          obj.records['acc-1'] = {
            ...obj.records['acc-1'],
            encryptedPayloadBase64: 'YWx0ZXJlZC1wYXlsb2Fk'
          };
          fs.writeFileSync(filePath, JSON.stringify(obj), 'utf8');
        }
      });

      await assert.rejects(
        () => vault.saveSessionWithReceipt('acc-1', Buffer.from('payload-1')),
        (err: unknown) => err instanceof VaultMutationUncertainError
      );
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should preserve the previous snapshot when persistence fails', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-failure-test-'));
    try {
      const dpapi = new MockDpapiProvider();
      const seed = new SessionVault({ vaultDir: tempDir, dpapiProvider: dpapi });
      await seed.saveSession('acc-stable', Buffer.from('stable-session'));
      const before = fs.readFileSync(seed.getVaultPath());
      const failing = new SessionVault({
        vaultDir: tempDir,
        dpapiProvider: dpapi,
        atomicWriter: async () => { throw new Error('injected pre-replacement failure'); }
      });

      await assert.rejects(() => failing.saveSession('acc-new', Buffer.from('new-session')));
      assert.deepEqual(fs.readFileSync(seed.getVaultPath()), before);
      assert.equal(await seed.hasSession('acc-new'), false);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should serialize concurrent saves from two instances without lost updates', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-concurrency-test-'));
    try {
      const dpapi = new MockDpapiProvider();
      const first = new SessionVault({ vaultDir: tempDir, dpapiProvider: dpapi });
      const second = new SessionVault({ vaultDir: tempDir, dpapiProvider: dpapi });
      await Promise.all(Array.from({ length: 20 }, (_, index) =>
        (index % 2 === 0 ? first : second).saveSession(`acc-${index}`, Buffer.from(`session-${index}`))));
      assert.equal((await first.listStoredAccountIds()).length, 20);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('conditionally restores only when the enrollment write is still current', async () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-vault-receipt-test-'));
    try {
      const vault = new SessionVault({ vaultDir: tempDir, dpapiProvider: new MockDpapiProvider() });
      await vault.saveSession('acc-receipt', Buffer.from('original'));
      const receipt = await vault.saveSessionWithReceipt('acc-receipt', Buffer.from('candidate'));
      await vault.saveSession('acc-receipt', Buffer.from('newer'));

      assert.equal(await vault.restoreIfCurrent(receipt), false);
      assert.equal((await vault.getSession('acc-receipt'))?.toString(), 'newer');
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should work with real WindowsDpapiProvider in an isolated directory', { skip: process.platform !== 'win32' ? 'Windows DPAPI is only supported on Windows' : false }, async () => {
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
