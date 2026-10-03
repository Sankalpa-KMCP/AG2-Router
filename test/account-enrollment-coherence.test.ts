import assert from 'node:assert/strict';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { describe, it } from 'node:test';
import { AG2AdapterFoundation } from '../src/ag2/adapter.js';
import { AG2AccountIdentity } from '../src/ag2/types.js';
import { WinCredEntry } from '../src/ag2/wincred.js';
import { AccountEnrollmentService } from '../src/accounts/enrollment.js';
import { LocalMetadataAccountStore } from '../src/accounts/account-store.js';
import { withCoordinatedFileAccess, writeFileAtomically } from '../src/persistence/file-coordination.js';
import { SessionVault } from '../src/vault/session-vault.js';

function barrier() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => { resolve = done; });
  return { promise, resolve };
}

function credential(token: string): WinCredEntry {
  return {
    target: 'gemini:antigravity', type: 1, userName: 'antigravity', persistence: 2,
    blob: Buffer.from(JSON.stringify({ token }))
  };
}

class LiveFixture extends AG2AdapterFoundation {
  public identity: AG2AccountIdentity | null = { email: 'a@example.test' };
  public credential = credential('synthetic-a');
  public identityReads = 0;
  public credentialReads = 0;
  public beforeIdentity?: (count: number) => Promise<void>;
  public afterIdentity?: (count: number) => Promise<void>;
  public beforeCredential?: (count: number) => Promise<void>;
  public override async getCurrentAccount(): Promise<AG2AccountIdentity | null> {
    const count = ++this.identityReads;
    await this.beforeIdentity?.(count);
    const snapshot = this.identity ? { ...this.identity } : null;
    await this.afterIdentity?.(count);
    return snapshot;
  }
  public async readCredential(): Promise<WinCredEntry> {
    const count = ++this.credentialReads;
    await this.beforeCredential?.(count);
    return this.credential;
  }
  public switchToB(): void {
    this.identity = { email: 'b@example.test' };
    this.credential = credential('synthetic-b');
  }
}

async function fixture() {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'ag2-enrollment-coherence-'));
  const live = new LiveFixture();
  const hooks: {
    afterVaultWrite?: () => Promise<void>;
    beforeMetadataWrite?: () => Promise<void>;
    afterMetadataWrite?: () => Promise<void>;
  } = {};
  const store = new LocalMetadataAccountStore(path.join(root, 'accounts.json'), async (file, content) => {
    const valid = JSON.parse(content).accounts.some((account: { validationStatus: string }) => account.validationStatus === 'VALID');
    if (valid) await hooks.beforeMetadataWrite?.();
    await writeFileAtomically(file, content);
    if (valid) await hooks.afterMetadataWrite?.();
  });
  const vault = new SessionVault({
    vaultDir: path.join(root, 'vault'),
    dpapiProvider: {
      encrypt: async (bytes) => Buffer.concat([Buffer.from('SYNTHETIC:'), bytes]),
      decrypt: async (bytes) => Buffer.from(bytes.subarray(Buffer.byteLength('SYNTHETIC:')))
    },
    atomicWriter: async (file, content) => {
      await writeFileAtomically(file, content);
      await hooks.afterVaultWrite?.();
    }
  });
  const service = new AccountEnrollmentService({ adapter: live, wincredReader: live, accountStore: store, sessionVault: vault });
  return {
    root, live, hooks, store, vault, service,
    async assertEmpty() {
      assert.deepEqual(await store.listAccounts(), []);
      assert.equal(await store.getActiveAccountId(), null);
      assert.deepEqual(await vault.listStoredAccountIds(), []);
    },
    cleanup: () => fs.rm(root, { recursive: true, force: true })
  };
}

describe('Node enrollment coherence', () => {
  const invalidCredentials: Record<string, Partial<WinCredEntry>> = {
    'foreign target': { target: 'foreign-synthetic-target' },
    'non-generic type': { type: 2 },
    'blank username': { userName: ' ' },
    'embedded NUL': { userName: 'bad\0username' },
    'oversized username': { userName: 'x'.repeat(514) },
    'invalid persistence': { persistence: 0 },
    'non-object payload': { blob: Buffer.from('null') },
    'non-string authentication field': { blob: Buffer.from('{"token":{"unexpected":true}}') }
  };
  for (const [name, updates] of Object.entries(invalidCredentials)) {
    it(`rejects structurally invalid ${name} before persistence`, async () => {
      const f = await fixture();
      try {
        f.live.credential = { ...f.live.credential, ...updates };
        await assert.rejects(f.service.enrollCurrentAccount());
        await f.assertEmpty();
      } finally { await f.cleanup(); }
    });
  }
  it('rejects A-to-B identity change before credential capture without any durable binding', { timeout: 10000 }, async () => {
    const f = await fixture();
    const entered = barrier();
    const resume = barrier();
    f.live.beforeCredential = async (count) => {
      if (count === 1) { entered.resolve(); await resume.promise; }
    };
    const enrollment = f.service.enrollCurrentAccount();
    const rejected = assert.rejects(enrollment, /changed/);
    try {
      await entered.promise;
      f.live.switchToB();
      resume.resolve();
      await rejected;
      await f.assertEmpty();
    } finally { resume.resolve(); await rejected; await f.cleanup(); }
  });

  it('rejects an identity changed while waiting for the enrollment lease before reading credentials', { timeout: 10000 }, async () => {
    const f = await fixture();
    const owned = barrier();
    const initialRead = barrier();
    const release = barrier();
    const holder = withCoordinatedFileAccess(`${f.vault.getVaultPath()}.enrollment`, async () => {
      owned.resolve(); await release.promise;
    });
    await owned.promise;
    f.live.afterIdentity = async (count) => { if (count === 1) initialRead.resolve(); };
    const enrollment = f.service.enrollCurrentAccount();
    const rejected = assert.rejects(enrollment, /changed/);
    try {
      await initialRead.promise;
      // The adapter captured A, but the actual enrollment lease is unavailable.
      assert.equal(f.live.credentialReads, 0);
      f.live.switchToB();
      release.resolve();
      await holder;
      await rejected;
      assert.equal(f.live.credentialReads, 0);
      await f.assertEmpty();
    } finally { release.resolve(); await holder; await rejected; await f.cleanup(); }
  });

  it('rejects same-identity credential rotation during final metadata persistence and restores active state', { timeout: 10000 }, async () => {
    const f = await fixture();
    const entered = barrier();
    const resume = barrier();
    f.hooks.beforeMetadataWrite = async () => {
      f.hooks.beforeMetadataWrite = undefined;
      entered.resolve(); await resume.promise;
    };
    const enrollment = f.service.enrollCurrentAccount();
    const rejected = assert.rejects(enrollment, /changed/);
    try {
      await entered.promise;
      f.live.credential = credential('synthetic-rotated');
      resume.resolve();
      await rejected;
      await f.assertEmpty();
    } finally { resume.resolve(); await rejected; await f.cleanup(); }
  });

  it('commits a stable identity and exact captured bytes without clearing reader-owned buffers', async () => {
    const f = await fixture();
    try {
      const original = Buffer.from(f.live.credential.blob);
      const result = await f.service.enrollCurrentAccount({ alias: 'stable account' });
      assert.equal(result.success, true);
      assert.equal(result.account.email, 'a@example.test');
      assert.equal(result.account.validationStatus, 'VALID');
      assert.equal(result.account.hasVaultedSession, true);
      assert.equal(await f.store.getActiveAccountId(), result.account.id);
      assert.deepEqual(await f.vault.getSession(result.account.id), original);
      assert.deepEqual(f.live.credential.blob, original);
    } finally { await f.cleanup(); }
  });

  it('detects credential rotation during the final identity lookup before returning success', async () => {
    const f = await fixture();
    try {
      f.hooks.afterMetadataWrite = async () => {
        f.hooks.afterMetadataWrite = undefined;
        const finalIdentityRead = f.live.identityReads + 2;
        f.live.afterIdentity = async (count) => {
          if (count === finalIdentityRead) f.live.credential = credential('synthetic-rotated');
        };
      };
      await assert.rejects(f.service.enrollCurrentAccount(), /changed/);
      await f.assertEmpty();
    } finally { await f.cleanup(); }
  });

  for (const phase of ['vault', 'metadata'] as const) {
    for (const existing of [false, true]) {
      it(`compensates ${phase} persistence followed by identity drift (${existing ? 'existing' : 'new'} account)`, { timeout: 10000 }, async () => {
        const f = await fixture();
        const priorActive = await f.store.addAccount({ email: 'prior@example.test' });
        await f.store.setActiveAccountId(priorActive.id);
        const priorAccount = existing ? await f.store.addAccount({ email: 'a@example.test', hasVaultedSession: true }) : null;
        if (priorAccount) await f.vault.saveSession(priorAccount.id, Buffer.from('{"token":"legitimate-prior"}'));
        if (priorAccount) await f.store.updateAccount(priorAccount.id, { validationStatus: 'VALID' });
        const priorMetadata = await f.store.listAccounts();
        const entered = barrier();
        const resume = barrier();
        const stop = async () => {
          f.hooks.afterVaultWrite = undefined;
          f.hooks.afterMetadataWrite = undefined;
          entered.resolve(); await resume.promise;
        };
        if (phase === 'vault') f.hooks.afterVaultWrite = stop;
        else f.hooks.afterMetadataWrite = stop;
        const enrollment = f.service.enrollCurrentAccount();
        const rejected = assert.rejects(enrollment, /changed/);
        try {
          await entered.promise;
          f.live.switchToB();
          resume.resolve();
          await rejected;
          assert.equal(await f.store.getActiveAccountId(), priorActive.id);
          assert.deepEqual(await f.store.listAccounts(), priorMetadata);
          assert.deepEqual(await f.vault.listStoredAccountIds(), priorAccount ? [priorAccount.id] : []);
          if (priorAccount) assert.equal((await f.vault.getSession(priorAccount.id))?.toString(), '{"token":"legitimate-prior"}');
        } finally { resume.resolve(); await rejected; await f.cleanup(); }
      });
    }
  }

  it('aborts stale concurrent enrollment and then enrolls B without cross-binding credentials', { timeout: 10000 }, async () => {
    const f = await fixture();
    const persisted = barrier();
    const resume = barrier();
    f.hooks.afterVaultWrite = async () => {
      f.hooks.afterVaultWrite = undefined;
      persisted.resolve(); await resume.promise;
    };
    const first = f.service.enrollCurrentAccount();
    const firstRejected = assert.rejects(first, /changed/);
    const secondInitial = barrier();
    let secondRejected: Promise<void> | undefined;
    try {
      await persisted.promise;
      const reads = f.live.identityReads;
      f.live.afterIdentity = async (count) => { if (count === reads + 1) secondInitial.resolve(); };
      secondRejected = assert.rejects(f.service.enrollCurrentAccount(), /changed/);
      await secondInitial.promise;
      f.live.switchToB();
      resume.resolve();
      await Promise.all([firstRejected, secondRejected]);
      await f.assertEmpty();
      f.live.afterIdentity = undefined;
      const valid = await f.service.enrollCurrentAccount();
      assert.equal(valid.account.email, 'b@example.test');
      assert.deepEqual(await f.vault.getSession(valid.account.id), credential('synthetic-b').blob);
      assert.equal(await f.store.getActiveAccountId(), valid.account.id);
    } finally { resume.resolve(); await firstRejected; await secondRejected; await f.cleanup(); }
  });

  it('preserves a newer vault record when coherence compensation loses its receipt', { timeout: 10000 }, async () => {
    const f = await fixture();
    const account = await f.store.addAccount({ email: 'a@example.test', hasVaultedSession: true });
    const prior = Buffer.from('{"token":"prior-legitimate"}');
    await f.vault.saveSession(account.id, prior);
    const priorMetadata = await f.store.getAccount(account.id);
    const entered = barrier();
    const resume = barrier();
    f.hooks.afterVaultWrite = async () => {
      f.hooks.afterVaultWrite = undefined;
      const postVaultIdentityRead = f.live.identityReads + 1;
      f.live.beforeIdentity = async (count) => {
        if (count === postVaultIdentityRead) { entered.resolve(); await resume.promise; }
      };
    };
    const enrollment = f.service.enrollCurrentAccount();
    const rejected = assert.rejects(enrollment, /manual recovery/);
    try {
      await entered.promise;
      const newer = Buffer.from('{"token":"newer-legitimate"}');
      await f.vault.saveSession(account.id, newer);
      f.live.credential = credential('synthetic-rotated');
      resume.resolve();
      await rejected;
      assert.deepEqual(await f.vault.getSession(account.id), newer);
      assert.deepEqual(await f.store.getAccount(account.id), priorMetadata);
      assert.equal(await f.store.getActiveAccountId(), null);
    } finally { resume.resolve(); await rejected; await f.cleanup(); }
  });

  it('recovers the receipt when a vault writer throws after replacement and still rejects drift', async () => {
    const f = await fixture();
    try {
      f.hooks.afterVaultWrite = async () => {
        f.hooks.afterVaultWrite = undefined;
        f.live.switchToB();
        throw new Error('Synthetic writer failure after replacement.');
      };
      await assert.rejects(f.service.enrollCurrentAccount(), /changed/);
      await f.assertEmpty();
    } finally { await f.cleanup(); }
  });

  it('restores a metadata write whose writer throws after replacement', async () => {
    const f = await fixture();
    try {
      f.hooks.afterMetadataWrite = async () => {
        f.hooks.afterMetadataWrite = undefined;
        throw new Error('Synthetic metadata failure after replacement.');
      };
      await assert.rejects(f.service.enrollCurrentAccount(), /Synthetic metadata failure/);
      await f.assertEmpty();
    } finally { await f.cleanup(); }
  });

  it('preserves an active choice queued behind failed finalization instead of rolling it back', { timeout: 10000 }, async () => {
    const f = await fixture();
    const previous = await f.store.addAccount({ email: 'previous@example.test' });
    const newer = await f.store.addAccount({ email: 'newer@example.test' });
    await f.store.setActiveAccountId(previous.id);
    const persisted = barrier();
    const resume = barrier();
    f.hooks.afterMetadataWrite = async () => {
      f.hooks.afterMetadataWrite = undefined;
      persisted.resolve(); await resume.promise;
    };
    const enrollment = f.service.enrollCurrentAccount();
    const rejected = assert.rejects(enrollment, /changed/);
    let choice: Promise<void> | undefined;
    try {
      await persisted.promise;
      const otherStore = new LocalMetadataAccountStore(f.store.getFilePath());
      choice = otherStore.setActiveAccountId(newer.id);
      f.live.switchToB();
      resume.resolve();
      await Promise.all([rejected, choice]);
      assert.equal(await f.store.getActiveAccountId(), newer.id);
      assert.deepEqual((await f.store.listAccounts()).map((account) => account.id), [previous.id, newer.id]);
      assert.deepEqual(await f.vault.listStoredAccountIds(), []);
    } finally { resume.resolve(); await rejected; await choice; await f.cleanup(); }
  });

  for (const mutation of ['username', 'unavailable', 'read-error'] as const) {
    it(`rejects ${mutation} after a durable vault write and preserves a safe empty state`, async () => {
      const f = await fixture();
      try {
        f.hooks.afterVaultWrite = async () => {
          f.hooks.afterVaultWrite = undefined;
          if (mutation === 'username') f.live.credential = { ...f.live.credential, userName: 'changed-user' };
          if (mutation === 'unavailable') f.live.identity = null;
          if (mutation === 'read-error') f.live.beforeCredential = async () => { throw new Error('Synthetic credential read failed.'); };
        };
        await assert.rejects(f.service.enrollCurrentAccount());
        await f.assertEmpty();
      } finally { await f.cleanup(); }
    });
  }

  it('rejects enrollment when session vault contains invalid records container (array) and preserves safe empty state', async () => {
    const f = await fixture();
    try {
      await fs.mkdir(f.vault.getVaultDir(), { recursive: true });
      const corruptedContent = JSON.stringify({
        magic: 'AG2_ROUTER_SESSION_VAULT',
        schemaVersion: 1,
        records: []
      });
      await fs.writeFile(f.vault.getVaultPath(), corruptedContent, 'utf8');
      const beforeBytes = await fs.readFile(f.vault.getVaultPath());

      await assert.rejects(
        f.service.enrollCurrentAccount(),
        /missing valid records dictionary/
      );

      // Verify safe empty state: metadata not committed with hasVaultedSession: true, active selection null
      assert.deepEqual(await f.store.listAccounts(), []);
      assert.equal(await f.store.getActiveAccountId(), null);
      // Verify vault file was not overwritten or repaired; original bytes strictly preserved
      const afterBytes = await fs.readFile(f.vault.getVaultPath());
      assert.deepEqual(afterBytes, beforeBytes);
    } finally {
      await f.cleanup();
    }
  });

  it('rejects enrollment when session vault contains invalid record entry shape and preserves safe empty state', async () => {
    const f = await fixture();
    try {
      await fs.mkdir(f.vault.getVaultDir(), { recursive: true });
      const corruptedContent = JSON.stringify({
        magic: 'AG2_ROUTER_SESSION_VAULT',
        schemaVersion: 1,
        records: {
          'acc-1': []
        }
      });
      await fs.writeFile(f.vault.getVaultPath(), corruptedContent, 'utf8');
      const beforeBytes = await fs.readFile(f.vault.getVaultPath());

      await assert.rejects(
        f.service.enrollCurrentAccount(),
        /contains invalid record entry for account 'acc-1'/
      );

      // Verify safe empty state: metadata not committed with hasVaultedSession: true, active selection null
      assert.deepEqual(await f.store.listAccounts(), []);
      assert.equal(await f.store.getActiveAccountId(), null);
      // Verify vault file was not overwritten or repaired; original bytes strictly preserved
      const afterBytes = await fs.readFile(f.vault.getVaultPath());
      assert.deepEqual(afterBytes, beforeBytes);
    } finally {
      await f.cleanup();
    }
  });
});
