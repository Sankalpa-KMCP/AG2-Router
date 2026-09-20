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
  LocalMetadataAccountStore
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
});
