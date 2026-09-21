/**
 * Test: Account Metadata Store
 */

import { describe, it, beforeEach, afterEach } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import {
  InMemoryAccountStore,
  LocalMetadataAccountStore,
  resolveAccountMetadataPath
} from '../src/accounts/account-store.js';

describe('InMemoryAccountStore', () => {
  let store: InMemoryAccountStore;

  beforeEach(() => {
    store = new InMemoryAccountStore();
  });

  it('should start with an empty account list and null active account', async () => {
    const list = await store.listAccounts();
    assert.equal(list.length, 0);
    assert.equal(await store.getActiveAccountId(), null);
  });

  it('should register an account and retrieve it by id and email', async () => {
    const created = await store.addAccount({
      email: 'user1@example.com',
      name: 'User One',
      priority: 1,
      isReserve: false
    });

    assert.ok(created.id.startsWith('acc_'));
    assert.equal(created.email, 'user1@example.com');
    assert.equal(created.name, 'User One');
    assert.equal(created.priority, 1);
    assert.equal(created.isReserve, false);
    assert.equal(created.validationStatus, 'UNVALIDATED');

    const fetched = await store.getAccount(created.id);
    assert.deepEqual(fetched, created);

    const byEmail = await store.getAccountByEmail('USER1@example.com'); // Case-insensitive
    assert.deepEqual(byEmail, created);
  });

  it('should reject duplicate email registration', async () => {
    await store.addAccount({ email: 'duplicate@example.com' });
    await assert.rejects(
      async () => {
        await store.addAccount({ email: 'duplicate@example.com' });
      },
      /already exists/i
    );
  });

  it('should set and clear active account id', async () => {
    const acc = await store.addAccount({ email: 'active@example.com' });
    await store.setActiveAccountId(acc.id);
    assert.equal(await store.getActiveAccountId(), acc.id);

    await store.setActiveAccountId(null);
    assert.equal(await store.getActiveAccountId(), null);
  });

  it('should reject setting active account id for non-existent account', async () => {
    await assert.rejects(
      async () => {
        await store.setActiveAccountId('non_existent');
      },
      /not found/i
    );
  });

  it('should clear activeAccountId when active account is removed', async () => {
    const acc = await store.addAccount({ email: 'remove@example.com' });
    await store.setActiveAccountId(acc.id);

    const removed = await store.removeAccount(acc.id);
    assert.equal(removed, true);
    assert.equal(await store.getActiveAccountId(), null);
  });

  it('compare-exchange does not overwrite a newer active selection', async () => {
    const first = await store.addAccount({ email: 'first-cas@example.com' });
    const second = await store.addAccount({ email: 'second-cas@example.com' });
    assert.equal(await store.compareExchangeActiveAccountId(null, first.id), true);
    await store.setActiveAccountId(second.id);
    assert.equal(await store.compareExchangeActiveAccountId(first.id, null), false);
    assert.equal(await store.getActiveAccountId(), second.id);
  });

  it('should store, trim, and clear account alias', async () => {
    const created = await store.addAccount({
      email: 'alias@example.com',
      alias: '  Work Account  '
    });
    assert.equal(created.alias, 'Work Account');

    const updated = await store.updateAccount(created.id, {
      alias: 'Personal'
    });
    assert.equal(updated?.alias, 'Personal');

    const cleared = await store.updateAccount(created.id, {
      alias: '   '
    });
    assert.equal(cleared?.alias, undefined);
  });
});

describe('LocalMetadataAccountStore (Filesystem Persistence)', () => {
  let tmpDir: string;
  let tmpFile: string;

  beforeEach(async () => {
    tmpDir = await fs.mkdtemp(path.join(os.tmpdir(), 'ag2-store-test-'));
    tmpFile = path.join(tmpDir, 'accounts.json');
  });

  afterEach(async () => {
    try {
      await fs.rm(tmpDir, { recursive: true, force: true });
    } catch {}
  });

  it('should persist accounts to disk and reload them into a new store instance', async () => {
    const store1 = new LocalMetadataAccountStore(tmpFile);
    const acc = await store1.addAccount({
      email: 'persisted@example.com',
      name: 'Persisted User',
      priority: 2,
      isReserve: true
    });
    await store1.setActiveAccountId(acc.id);

    // Verify file was written
    const fileContent = await fs.readFile(tmpFile, 'utf-8');
    assert.ok(fileContent.includes('persisted@example.com'));

    // Create a new instance pointing to same file
    const store2 = new LocalMetadataAccountStore(tmpFile);
    const loaded = await store2.listAccounts();
    assert.equal(loaded.length, 1);
    assert.equal(loaded[0].email, 'persisted@example.com');
    assert.equal(loaded[0].isReserve, true);
    assert.equal(await store2.getActiveAccountId(), acc.id);
  });

  it('reads the shared TS-to-C# golden fixture', async () => {
    const fixture = path.resolve(process.cwd(), 'test', 'fixtures', 'accounts-v1.json');
    await fs.copyFile(fixture, tmpFile);
    const store = new LocalMetadataAccountStore(tmpFile);
    const loaded = await store.listAccounts();
    assert.equal(loaded.length, 2);
    const full = loaded.find((account) => account.id === 'acc_shared01');
    const minimal = loaded.find((account) => account.id === 'acc_minimal1');
    assert.equal(full?.name, 'Interoperability ✓');
    assert.equal(full?.alias, undefined);
    assert.equal(full?.hasVaultedSession, false);
    assert.equal(minimal?.name, undefined);
    assert.equal(minimal?.alias, undefined);
    assert.equal(minimal?.notes, undefined);
    assert.equal(minimal?.hasVaultedSession, undefined);
    assert.equal(await store.getActiveAccountId(), 'acc_shared01');
  });

  it('writes the exact camelCase shared schema without runtime-only fields', async () => {
    const store = new LocalMetadataAccountStore(tmpFile);
    const account = await store.addAccount({
      email: 'schema@example.com',
      name: 'Schema',
      priority: 0,
      isReserve: false,
      hasVaultedSession: false,
      notes: 'synthetic'
    });
    await store.setActiveAccountId(account.id);

    const parsed = JSON.parse(await fs.readFile(tmpFile, 'utf8')) as Record<string, unknown>;
    assert.deepEqual(Object.keys(parsed), ['version', 'activeAccountId', 'accounts']);
    const stored = (parsed.accounts as Array<Record<string, unknown>>)[0];
    assert.deepEqual(Object.keys(stored), [
      'id', 'email', 'name', 'priority', 'isReserve', 'validationStatus',
      'hasVaultedSession', 'createdAt', 'updatedAt', 'lastActiveAt', 'notes'
    ]);
    assert.equal('IsActive' in stored, false);
    assert.equal('isActive' in stored, false);
  });

  for (const [label, content] of [
    ['zero-byte', ''],
    ['whitespace', '  \r\n'],
    ['truncated', '{"version":1,"activeAccountId":null,"accounts":['],
    ['null', 'null'],
    ['wrong-version', '{"version":2,"activeAccountId":null,"accounts":[]}'],
    ['missing-accounts', '{"version":1,"activeAccountId":null}']
  ] as const) {
    it(`fails closed and preserves ${label} metadata`, async () => {
      await fs.writeFile(tmpFile, content, 'utf8');
      const before = await fs.readFile(tmpFile);
      const store = new LocalMetadataAccountStore(tmpFile);
      await assert.rejects(() => store.addAccount({ email: 'blocked@example.com' }));
      assert.deepEqual(await fs.readFile(tmpFile), before);
    });
  }

  it('does not publish in-memory mutation when persistence fails', async () => {
    const seed = new LocalMetadataAccountStore(tmpFile);
    const account = await seed.addAccount({ email: 'stable@example.com' });
    const before = await fs.readFile(tmpFile);
    const failing = new LocalMetadataAccountStore(tmpFile, async () => {
      throw new Error('injected pre-replacement failure');
    });

    await assert.rejects(() => failing.updateAccount(account.id, { name: 'must-not-publish' }));
    assert.deepEqual(await fs.readFile(tmpFile), before);
    assert.equal((await failing.getAccount(account.id))?.name, undefined);
  });

  it('serializes concurrent mutations from two instances without lost updates', async () => {
    const first = new LocalMetadataAccountStore(tmpFile);
    const second = new LocalMetadataAccountStore(tmpFile);
    await Promise.all(Array.from({ length: 20 }, (_, index) =>
      (index % 2 === 0 ? first : second).addAccount({ email: `concurrent-${index}@example.com` })));
    assert.equal((await new LocalMetadataAccountStore(tmpFile).listAccounts()).length, 20);
  });

  it('rebases concurrent update, delete, and add operations on the latest snapshot', async () => {
    const seed = new LocalMetadataAccountStore(tmpFile);
    const updateTarget = await seed.addAccount({ email: 'update@example.com' });
    const deleteTarget = await seed.addAccount({ email: 'delete@example.com' });
    const first = new LocalMetadataAccountStore(tmpFile);
    const second = new LocalMetadataAccountStore(tmpFile);
    const third = new LocalMetadataAccountStore(tmpFile);

    await Promise.all([
      first.updateAccount(updateTarget.id, { name: 'updated' }),
      second.removeAccount(deleteTarget.id),
      third.addAccount({ email: 'added@example.com' })
    ]);

    const accounts = await new LocalMetadataAccountStore(tmpFile).listAccounts();
    assert.equal(accounts.find((account) => account.id === updateTarget.id)?.name, 'updated');
    assert.equal(accounts.some((account) => account.id === deleteTarget.id), false);
    assert.equal(accounts.some((account) => account.email === 'added@example.com'), true);
  });

  it('compare-exchanges active id atomically across instances', async () => {
    const seed = new LocalMetadataAccountStore(tmpFile);
    const firstAccount = await seed.addAccount({ email: 'first-active@example.com' });
    const secondAccount = await seed.addAccount({ email: 'second-active@example.com' });
    assert.equal(await seed.compareExchangeActiveAccountId(null, firstAccount.id), true);

    const other = new LocalMetadataAccountStore(tmpFile);
    await other.setActiveAccountId(secondAccount.id);
    assert.equal(await seed.compareExchangeActiveAccountId(firstAccount.id, null), false);
    assert.equal(await seed.getActiveAccountId(), secondAccount.id);
  });

  it('conditionally removes only an unchanged account snapshot', async () => {
    const first = new LocalMetadataAccountStore(tmpFile);
    const expected = await first.addAccount({ email: 'conditional-remove@example.com' });
    const second = new LocalMetadataAccountStore(tmpFile);
    await second.updateAccount(expected.id, { notes: 'newer' });

    assert.equal(await first.removeAccountIfUnchanged(expected), false);
    assert.equal((await first.getAccount(expected.id))?.notes, 'newer');
    const current = await first.getAccount(expected.id);
    assert.ok(current);
    assert.equal(await first.removeAccountIfUnchanged(current), true);
    assert.equal(await second.getAccount(expected.id), null);
  });

  it('resolves DATA_DIR and cwd defaults identically to the .NET contract', () => {
    const cwd = path.join(tmpDir, 'cwd');
    const localRoot = path.join(tmpDir, 'local-app-data');
    assert.equal(
      resolveAccountMetadataPath('', cwd, localRoot),
      path.resolve(localRoot, 'AG2-Router', 'data', 'accounts.json'));
    assert.equal(
      resolveAccountMetadataPath('relative-data', cwd, localRoot),
      path.resolve(cwd, 'relative-data', 'accounts.json'));
    const absoluteData = path.join(tmpDir, 'absolute-data');
    assert.equal(
      resolveAccountMetadataPath(absoluteData, cwd, localRoot),
      path.resolve(absoluteData, 'accounts.json'));
  });

  it('persists account alias to disk and reloads across instances', async () => {
    const store1 = new LocalMetadataAccountStore(tmpFile);
    const acc = await store1.addAccount({
      email: 'alias-persisted@example.com',
      alias: 'Work'
    });
    assert.equal(acc.alias, 'Work');

    const parsed = JSON.parse(await fs.readFile(tmpFile, 'utf8')) as Record<string, unknown>;
    const stored = (parsed.accounts as Array<Record<string, unknown>>)[0];
    assert.equal(stored.alias, 'Work');

    const store2 = new LocalMetadataAccountStore(tmpFile);
    const loaded = await store2.getAccount(acc.id);
    assert.ok(loaded);
    assert.equal(loaded.alias, 'Work');
  });

  it('normalizes whitespace alias to undefined and omits it from JSON', async () => {
    const store = new LocalMetadataAccountStore(tmpFile);
    const acc = await store.addAccount({
      email: 'ws-alias@example.com',
      alias: '   '
    });
    assert.equal(acc.alias, undefined);

    const parsed = JSON.parse(await fs.readFile(tmpFile, 'utf8')) as Record<string, unknown>;
    const stored = (parsed.accounts as Array<Record<string, unknown>>)[0];
    assert.equal('alias' in stored, false);

    await store.updateAccount(acc.id, { alias: 'Temporary' });
    assert.equal((await store.getAccount(acc.id))?.alias, 'Temporary');

    await store.updateAccount(acc.id, { alias: '   ' });
    assert.equal((await store.getAccount(acc.id))?.alias, undefined);

    const parsedAfterClear = JSON.parse(await fs.readFile(tmpFile, 'utf8')) as Record<string, unknown>;
    const storedAfterClear = (parsedAfterClear.accounts as Array<Record<string, unknown>>)[0];
    assert.equal('alias' in storedAfterClear, false);
  });

  it('fails closed when account record contains unrecognized keys', async () => {
    const store = new LocalMetadataAccountStore(tmpFile);
    await store.addAccount({ email: 'seed@example.com' });
    const content = JSON.parse(await fs.readFile(tmpFile, 'utf8')) as Record<string, unknown>;
    const accounts = content.accounts as Array<Record<string, unknown>>;
    accounts[0].unrecognizedKey = 'fail';
    await fs.writeFile(tmpFile, JSON.stringify(content, null, 2), 'utf8');

    const failingStore = new LocalMetadataAccountStore(tmpFile);
    await assert.rejects(
      () => failingStore.listAccounts(),
      /violates schema version 1/i
    );
  });
});
