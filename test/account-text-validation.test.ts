import { describe, it, before, after } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { InMemoryAccountStore, LocalMetadataAccountStore } from '../src/accounts/account-store.js';
import { ACCOUNT_TEXT_LIMITS } from '../src/accounts/text-validation.js';
import { AppServer } from '../src/server/server.js';
import { QuotaRouter } from '../src/router/router.js';
import { AG2AdapterFoundation } from '../src/ag2/adapter.js';
import { AccountEnrollmentService } from '../src/accounts/enrollment.js';
import { SessionVault } from '../src/vault/session-vault.js';

for (const field of ['name', 'alias', 'notes'] as const) {
  for (const durable of [false, true]) for (const unicode of [false, true]) {
    it(`store ${field}: boundaries, atomic create/update/finalize; durable=${durable}, unicode=${unicode}`, async () => {
      const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'ag2-text-'));
      try {
        const file = path.join(dir, 'accounts.json');
        const store = durable ? new LocalMetadataAccountStore(file) : new InMemoryAccountStore();
        const max = ACCOUNT_TEXT_LIMITS[field];
        const value = unicode ? '😀'.repeat(max / 2) : 'x'.repeat(max);
        const account = await store.addAccount({ email: 'bounds@example.com', [field]: value });
        assert.equal(account[field], value);
        assert.equal((await store.updateAccount(account.id, { [field]: value }))?.[field], value);
        await store.setActiveAccountId(account.id);
        const current = (await store.getAccount(account.id))!;
        assert.equal((await store.finalizeEnrollment(current, { [field]: value }, account.id, async () => {})).account[field], value);
        for (const invalid of [value + 'x', 'z'.repeat(16000), ' '.repeat(max + 1)]) {
          const before = await store.listAccounts();
          const bytes = durable ? await fs.readFile(file) : undefined;
          for (const operation of [
            async () => store.addAccount({ email: 'rejected@example.com', [field]: invalid }),
            async () => store.updateAccount(account.id, { [field]: invalid }),
            async () => store.finalizeEnrollment((await store.getAccount(account.id))!, { [field]: invalid }, account.id, async () => {})
          ]) {
            await assert.rejects(operation, (error: Error) => {
              assert.match(error.message, /UTF-16 code units/);
              assert.ok(error.message.length < 100);
              assert.ok(!error.message.includes(invalid));
              return true;
            });
            assert.deepEqual(await store.listAccounts(), before);
            assert.equal(await store.getActiveAccountId(), account.id);
            if (durable) assert.deepEqual(await fs.readFile(file), bytes);
          }
        }
      } finally { await fs.rm(dir, { recursive: true, force: true }); }
    });
  }
  it(`legacy oversized ${field} remains readable, preservable, and replaceable`, async () => {
    const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'ag2-text-legacy-'));
    try {
      const file = path.join(dir, 'accounts.json');
      let store = new LocalMetadataAccountStore(file);
      const account = await store.addAccount({ email: 'legacy@example.com' });
      const document = JSON.parse(await fs.readFile(file, 'utf8'));
      document.accounts[0][field] = 'z'.repeat(16000);
      await fs.writeFile(file, JSON.stringify(document));
      store = new LocalMetadataAccountStore(file);
      assert.equal((await store.getAccount(account.id))?.[field]?.length, 16000);
      await store.updateAccount(account.id, { priority: 3 });
      assert.equal((await store.getAccount(account.id))?.[field]?.length, 16000);
      assert.equal((await store.updateAccount(account.id, { [field]: 'valid' }))?.[field], 'valid');
    } finally { await fs.rm(dir, { recursive: true, force: true }); }
  });
}

describe('account HTTP text limits', () => {
  const port = 39173;
  let dir: string;
  let store: LocalMetadataAccountStore;
  let vault: SessionVault;
  let server: AppServer;
  const adapter = new AG2AdapterFoundation();
  adapter.getCurrentAccount = async () => ({ email: 'enroll-bounds@example.com', name: 'Provider name' });
  const config = { autoSwitchEnabled: false, lowQuotaThresholdPercent: 15, minimumCandidateQuotaPercent: 30, pollingIntervalMs: 10000 };
  before(async () => {
    dir = await fs.mkdtemp(path.join(os.tmpdir(), 'ag2-text-api-'));
    store = new LocalMetadataAccountStore(path.join(dir, 'accounts.json'));
    vault = new SessionVault({ vaultDir: dir, dpapiProvider: { encrypt: async b => Buffer.from(b), decrypt: async b => Buffer.from(b) } });
    const enrollment = new AccountEnrollmentService({ adapter, accountStore: store, sessionVault: vault,
      wincredReader: { readCredential: async () => ({ target: 'gemini:antigravity', type: 1, userName: 'antigravity', persistence: 2,
        blob: Buffer.from(JSON.stringify({ token: 'synthetic-token', auth_method: 'oauth' })) }) } });
    server = new AppServer({ host: '127.0.0.1', port, storageDir: dir, uiDir: path.resolve('src/ui'), router: config, isDev: true },
      store, adapter, new QuotaRouter(store, adapter, config), enrollment, vault);
    await server.start();
  });
  after(async () => { await server.stop(); await fs.rm(dir, { recursive: true, force: true }); });
  async function send(route: string, method: string, body: object) {
    return fetch(`http://127.0.0.1:${port}${route}`, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  }
  for (const field of ['name', 'notes', 'alias'] as const) for (const unicode of [false, true]) {
    it(`HTTP ${field} max/max+1/huge boundaries, unicode=${unicode}`, async () => {
      const max = ACCOUNT_TEXT_LIMITS[field];
      const value = unicode ? '😀'.repeat(max / 2) : 'x'.repeat(max);
      const created = await store.addAccount({ email: `${field}-${unicode}@example.com` });
      const routes = field === 'alias' ? [`/api/accounts/${created.id}`] : ['/api/accounts', '/api/accounts/enroll-current'];
      for (const route of routes) {
        const method = field === 'alias' ? 'PATCH' : 'POST';
        const payload = { email: `http-${field}-${unicode}@example.com`, [field]: value };
        const accepted = await send(route, method, payload);
        assert.equal(accepted.status, route === '/api/accounts' ? 201 : 200);
        assert.equal((await accepted.json() as { account: Record<string, string> }).account[field], value);
        const before = await fs.readFile(store.getFilePath());
        const vaultFile = vault.getVaultPath();
        let vaultBefore: Buffer | undefined;
        try { vaultBefore = await fs.readFile(vaultFile); } catch { }
        for (const invalid of [value + 'x', 'z'.repeat(16000), ' '.repeat(max + 1)]) {
          const rejected = await send(route, method, { ...payload, email: 'rejected@example.com', [field]: invalid });
          assert.equal(rejected.status, 400);
          const error = await rejected.text();
          assert.match(error, /UTF-16 code units/);
          assert.ok(error.length < 200);
          assert.ok(!error.includes(invalid));
          assert.deepEqual(await fs.readFile(store.getFilePath()), before);
          if (vaultBefore) assert.deepEqual(await fs.readFile(vaultFile), vaultBefore);
        }
        if (field === 'alias') for (const alias of [null, '', '  ']) {
          assert.equal((await send(route, method, { alias })).status, 200);
          assert.equal((await store.getAccount(created.id))?.alias, undefined);
        }
      }
    });
  }
});
