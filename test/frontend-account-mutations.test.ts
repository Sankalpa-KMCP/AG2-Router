import { it } from 'node:test';
import assert from 'node:assert/strict';
import { updateAlias, connectAccount } from '../frontend/src/lib/utils/account-mutations.js';
import { DashboardRefreshGate } from '../frontend/src/lib/utils/helpers.js';
import type { AccountMetadata } from '../frontend/src/lib/api/types.js';
import { readFileSync } from 'node:fs';
import { transpileModule, ModuleKind, ScriptTarget } from 'typescript';

const account: AccountMetadata = {
  id: 'synthetic', email: 'synthetic@example.test', alias: 'Before', priority: 1,
  isReserve: false, createdAt: '2026-01-01', hasVaultedSession: false, isActive: false
};
const input = { email: account.email, priority: 1, isReserve: false };

function fixture() {
  class CountingGate extends DashboardRefreshGate {
    begins = 0;
    ends = 0;
    override beginMutation() { super.beginMutation(); this.begins++; }
    override endMutation() { super.endMutation(); this.ends++; }
  }
  const refreshGate = new CountingGate();
  const activity: string[] = [];
  const errors: string[] = [];
  const published: Array<AccountMetadata['alias']> = [];
  let refreshes = 0;
  return {
    refreshGate, activity, errors, published,
    logActivity: (message: string) => { activity.push(message); },
    notifyError: (message: string) => { errors.push(message); },
    publishAlias: (id: string, alias: AccountMetadata['alias']) => {
      assert.equal(id, account.id); published.push(alias);
    },
    refreshAll: async () => {
      assert.equal(refreshGate.canPublish(refreshGate.beginRead('accounts')), true);
      refreshes++;
    },
    get refreshes() { return refreshes; }
  };
}

function assertReleased(f: ReturnType<typeof fixture>, count = 1) {
  assert.equal(f.refreshGate.begins, count);
  assert.equal(f.refreshGate.ends, count);
  assert.equal(f.refreshGate.canPublish(f.refreshGate.beginRead('accounts')), true);
}

it('alias rejection resolves with visible error, no publication, and permits successful retry', async () => {
  const f = fixture();
  const stale = f.refreshGate.beginRead('accounts');
  const result = await updateAlias(account.id, 'After', {
    ...f, updateAccountAlias: async () => { throw new Error('Alias is too long.'); }
  });
  assert.equal(result.success, false);
  assert.deepEqual(f.errors, ['Alias update failed: Alias is too long.']);
  assert.deepEqual(f.activity, f.errors);
  assert.deepEqual(f.published, []);
  assert.equal(f.refreshGate.canPublish(stale), false);
  assertReleased(f);
  assert.deepEqual(await updateAlias(account.id, 'After', {
    ...f, updateAccountAlias: async () => ({ success: true, account: { ...account, alias: 'After' } })
  }), { success: true });
  assert.deepEqual(f.published, ['After']);
  assert.equal(f.errors.length, 1);
  assert.equal(f.activity[1], `Updated alias for ${account.email} to "After".`);
  assertReleased(f, 2);
});

it('alias success publishes server-confirmed alias, preserves clearing, and has no success notification', async () => {
  const f = fixture();
  assert.deepEqual(await updateAlias(account.id, '', {
    ...f, updateAccountAlias: async (id, alias) => {
      assert.equal(id, account.id); assert.equal(alias, '');
      return { success: true, account: { ...account, alias: null } };
    }
  }), { success: true });
  assert.deepEqual(f.published, [null]);
  assert.deepEqual(f.errors, []);
  assert.deepEqual(f.activity, [`Updated alias for ${account.email} to "(cleared)".`]);
  assert.equal(f.refreshes, 0);
  assertReleased(f);
});

it('unconfirmed alias response reports failure and does not falsely publish', async () => {
  const f = fixture();
  assert.equal((await updateAlias(account.id, 'After', {
    ...f, updateAccountAlias: async () => ({ success: false, account })
  })).success, false);
  assert.equal(f.errors.length, 1);
  assert.deepEqual(f.published, []);
  assertReleased(f);
});

it('connect rejection resolves with visible error, skips success refresh, and permits retry', async () => {
  const f = fixture();
  assert.equal((await connectAccount(input, {
    ...f, createAccount: async () => { throw new Error('Account already exists.'); }
  })).success, false);
  assert.deepEqual(f.errors, ['Account connection failed: Account already exists.']);
  assert.deepEqual(f.activity, f.errors);
  assert.equal(f.refreshes, 0);
  assert.deepEqual(f.published, []);
  assertReleased(f);
  assert.deepEqual(await connectAccount(input, {
    ...f, createAccount: async () => ({ account })
  }), { success: true });
  assert.equal(f.refreshes, 1);
  assert.equal(f.errors.length, 1);
  assert.equal(f.activity[1], `Connected account metadata for ${account.email}.`);
  assertReleased(f, 2);
});

it('connect success preserves request, logs metadata connection, and refreshes after releasing gate', async () => {
  const f = fixture();
  assert.deepEqual(await connectAccount(input, {
    ...f, createAccount: async data => { assert.deepEqual(data, input); return { account }; }
  }), { success: true });
  assert.equal(f.refreshes, 1);
  assert.deepEqual(f.errors, []);
  assert.deepEqual(f.published, []);
  assert.deepEqual(f.activity, [`Connected account metadata for ${account.email}.`]);
  assertReleased(f);
});

for (const operation of ['alias', 'connect'] as const) {
  it(`${operation} fences pending mutation until deterministic API failure settles`, async () => {
    const f = fixture();
    let reject!: (error: Error) => void;
    const pending = new Promise<never>((_resolve, rejectPromise) => { reject = rejectPromise; });
    const task = operation === 'alias'
      ? updateAlias(account.id, 'After', { ...f, updateAccountAlias: () => pending })
      : connectAccount(input, { ...f, createAccount: () => pending });
    assert.equal(f.refreshGate.canPublish(f.refreshGate.beginRead('accounts')), false);
    assert.equal(f.refreshGate.ends, 0);
    reject(new Error('Request rejected.'));
    assert.equal((await task).success, false);
    assertReleased(f);
  });

  it(`${operation} bounds message text and excludes stack lines`, async () => {
    const f = fixture();
    const error = new Error('x'.repeat(10000) + '\n at internalFunction(secret)');
    error.stack = 'private internal stack';
    const result = operation === 'alias'
      ? await updateAlias(account.id, 'After', { ...f, updateAccountAlias: async () => { throw error; } })
      : await connectAccount(input, { ...f, createAccount: async () => { throw error; } });
    assert.equal(result.success, false);
    assert.equal(f.errors.length, 1);
    assert.ok(f.errors[0].length <= 283);
    assert.ok(!f.errors[0].includes('internal'));
    assert.deepEqual(f.activity, f.errors);
    assertReleased(f);
  });

  it(`${operation} does not stringify a thrown internal object`, async () => {
    const f = fixture();
    const fail = async (): Promise<never> => { throw { secret: 'private value', toString: () => { throw new Error('must not stringify'); } }; };
    const result = operation === 'alias'
      ? await updateAlias(account.id, 'After', { ...f, updateAccountAlias: fail })
      : await connectAccount(input, { ...f, createAccount: fail });
    assert.equal(result.success, false);
    assert.ok(f.errors[0].endsWith('Request failed. Please try again.'));
    assert.ok(!f.errors[0].includes('private'));
    assertReleased(f);
  });
}

// Execute the authored component scripts with identity rune shims. This exercises
// their actual event handlers/form state without modeling them again in the tests.
// Rendering and browser-console behavior are outside this script-level harness.
function componentScript(file: string, props: object, expose: string): Record<string, (...args: unknown[]) => any> {
  const source = readFileSync(`frontend/src/lib/components/${file}.svelte`, 'utf8');
  const script = source.match(/<script lang="ts">([\s\S]*?)<\/script>/)![1]
    .replace(/^\s*import[^\n]+\n/gm, '');
  const compiled = transpileModule(script + '\nreturn ' + expose + ';', {
    compilerOptions: { target: ScriptTarget.ES2022, module: ModuleKind.None }
  }).outputText;
  return new Function('$props', '$state', '$derived', compiled)(
    () => props, (value: unknown) => value, (value: unknown) => value
  );
}

it('real alias editor retains input on handled failure, unlocks controls, then closes on successful retry', async () => {
  const f = fixture();
  let fails = true;
  const component = componentScript('AccountsTable', {
    lifecycleMutationsAllowed: true,
    onUpdateAlias: (id: string, alias: string) => updateAlias(id, alias, {
      ...f, updateAccountAlias: async () => {
        if (fails) throw new Error('Try another alias.');
        return { success: true, account: { ...account, alias } };
      }
    })
  }, '{ startEditing, saveAlias, state: () => ({ editingId, editValue, isSavingAlias, aliasError }) }');
  component.startEditing(account);
  await component.saveAlias(account.id);
  assert.deepEqual(component.state(), {
    editingId: account.id, editValue: 'Before', isSavingAlias: false,
    aliasError: 'Alias update failed: Try another alias.'
  });
  assert.deepEqual(f.published, []);
  fails = false;
  await component.saveAlias(account.id);
  assert.equal(component.state().editingId, null);
  assert.equal(component.state().isSavingAlias, false);
  assert.deepEqual(f.published, ['Before']);
  assertReleased(f, 2);
});

it('real connect form retains fields and stays open on handled failure, then resets/closes on successful retry', async () => {
  const f = fixture();
  let fails = true;
  let closes = 0;
  const component = componentScript('ConnectAccountModal', {
    isOpen: true,
    onClose: () => { closes++; },
    onSubmit: (data: typeof input) => connectAccount(data, {
      ...f, createAccount: async () => {
        if (fails) throw new Error('Try again.');
        return { account };
      }
    })
  }, '{ handleSubmit, enter: () => { email = "synthetic@example.test"; name = "Synthetic"; alias = "Work"; }, state: () => ({ email, name, alias, isSubmitting, error }) }');
  component.enter();
  await component.handleSubmit({ preventDefault() {} });
  assert.deepEqual(component.state(), {
    email: account.email, name: 'Synthetic', alias: 'Work', isSubmitting: false,
    error: 'Account connection failed: Try again.'
  });
  assert.equal(closes, 0);
  assert.equal(f.refreshes, 0);
  fails = false;
  await component.handleSubmit({ preventDefault() {} });
  assert.deepEqual(component.state(), { email: '', name: '', alias: '', isSubmitting: false, error: null });
  assert.equal(closes, 1);
  assert.equal(f.refreshes, 1);
  assertReleased(f, 2);
});
