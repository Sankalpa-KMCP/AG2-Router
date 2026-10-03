import { describe, it, type TestContext } from 'node:test';
import assert from 'node:assert/strict';
import { ApiClient, SwitchRequestError } from '../frontend/src/lib/api/client.js';
import type { SwitchStatusDto } from '../frontend/src/lib/api/types.js';
import { DashboardRefreshGate } from '../frontend/src/lib/utils/helpers.js';
import { canExecuteLifecycleMutation } from '../frontend/src/lib/utils/recovery.js';
import { executeDashboardSwitch, getSwitchErrorMessage } from '../frontend/src/lib/utils/switching.js';

// The complete camelCase NativeSwitchResult shape, with synthetic identities only.
function nativeResult(code: string, message: string, manualRecoveryRequired = false) {
  return {
    transactionId: 'synthetic-transaction', success: code === 'SUCCESS', code,
    state: code === 'SUCCESS' ? 'COMPLETE' : code === 'SWITCH_FAILED_ROLLED_BACK' ? 'ROLLED_BACK' : 'FAILED',
    targetAccountId: 'synthetic-target', targetEmail: 'target@example.invalid',
    previousAccountId: 'synthetic-source', previousEmail: 'source@example.invalid',
    message, stagesCompleted: [], startedAt: '2026-01-01T00:00:00Z',
    finishedAt: '2026-01-01T00:00:01Z', manualRecoveryRequired
  };
}

const clearStatus: SwitchStatusDto = {
  activeTransactionId: null, currentState: 'IDLE', lastResult: null,
  quarantineActive: false, journalRecoveryState: 'NONE'
};

function mockSwitchFetch(t: TestContext, response: () => Promise<Response>, status = clearStatus) {
  t.mock.method(globalThis, 'fetch', async (input: Parameters<typeof fetch>[0], init?: RequestInit) => {
    const url = String(input);
    if (url === '/api/switching/intent') {
      assert.equal(init?.method, 'POST');
      assert.equal(new Headers(init?.headers).get('X-AG2-Intent-Request'), '1');
      return Response.json({ ready: true }, { headers: { 'X-AG2-Switch-Token': 'synthetic-token' } });
    }
    if (url === '/api/accounts/synthetic-target/switch') {
      assert.equal(init?.method, 'POST');
      assert.equal(new Headers(init?.headers).get('X-AG2-Switch-Token'), 'synthetic-token');
      assert.equal(new Headers(init?.headers).get('Content-Type'), 'application/json');
      assert.deepEqual(JSON.parse(String(init?.body)), { confirm: true });
      return response();
    }
    assert.equal(url, '/api/switching/status', 'No test request may reach a live endpoint');
    return Response.json({ status });
  });
}

describe('native switch failures through the real frontend client and confirmation flow', () => {
  for (const [httpStatus, code, message, recovery] of [
    [409, 'SWITCH_IN_PROGRESS', 'Another switch is already in progress.', false],
    [409, 'AG2_BUSY', 'Antigravity is busy.', false],
    [409, 'TARGET_NOT_VAULTED', 'The target has no vaulted session.', false],
    [404, 'TARGET_NOT_FOUND', 'The target account is unavailable.', false],
    [500, 'SWITCH_FAILED_ROLLED_BACK', 'Switch failed; previous identity was restored.', false],
    [500, 'SWITCH_FAILED_ROLLBACK_FAILED', 'State could not be proven.', true]
  ] as const) {
    it(`${code} preserves native fields, displays the outcome, and immediately reconciles safety`, async t => {
      const result = nativeResult(code, message, recovery);
      const authoritativeStatus: SwitchStatusDto = recovery
        ? { ...clearStatus, currentState: 'FAILED', quarantineActive: true, journalRecoveryState: 'ACTION_REQUIRED' }
        : clearStatus;
      const client = new ApiClient();
      const gate = new DashboardRefreshGate();
      const oldTicket = gate.beginRead('switch');
      let safetyAuthoritative = true;
      let status: SwitchStatusDto | null = clearStatus;
      let reconciliations = 0;
      mockSwitchFetch(t, async () => {
        assert.equal(canExecuteLifecycleMutation(safetyAuthoritative, status), false);
        return Response.json(result, { status: httpStatus });
      }, authoritativeStatus);

      await assert.rejects(executeDashboardSwitch('synthetic-target', {
        client, refreshGate: gate,
        invalidateSafety: () => { safetyAuthoritative = false; status = null; },
        refreshAll: async () => {
          reconciliations++;
          assert.equal(canExecuteLifecycleMutation(safetyAuthoritative, status), false);
          assert.equal(gate.canPublish(oldTicket), false);
          const ticket = gate.beginRead('switch');
          status = (await client.getSwitchStatus()).status;
          assert.equal(gate.canPublish(ticket), true, 'Mutation fence ends before fresh reads');
          safetyAuthoritative = true;
        }
      }), error => {
        assert.ok(error instanceof SwitchRequestError);
        assert.equal(error.code, code);
        assert.equal(error.message, message);
        assert.equal(error.manualRecoveryRequired, recovery);
        assert.equal(error.httpStatus, httpStatus);
        assert.match(getSwitchErrorMessage(error), new RegExp(code));
        assert.ok(getSwitchErrorMessage(error).includes(message));
        if (recovery) assert.match(getSwitchErrorMessage(error), /Manual recovery is required/);
        return true;
      });
      assert.equal(reconciliations, 1);
      assert.equal(canExecuteLifecycleMutation(safetyAuthoritative, status), !recovery);
    });
  }

  it('successful switching still reconciles once before completing', async t => {
    const client = new ApiClient();
    const gate = new DashboardRefreshGate();
    const events: string[] = [];
    mockSwitchFetch(t, async () => {
      events.push('switch');
      return Response.json(nativeResult('SUCCESS', 'Target identity verified.'));
    });
    const result = await executeDashboardSwitch('synthetic-target', {
      client, refreshGate: gate,
      invalidateSafety: () => { events.push('invalidate'); },
      refreshAll: async () => {
        assert.equal(gate.canPublish(gate.beginRead('switch')), true);
        assert.deepEqual((await client.getSwitchStatus()).status, clearStatus);
        events.push('refresh');
      }
    });
    assert.equal(result.success, true);
    assert.equal(result.manualRecoveryRequired, false);
    assert.deepEqual(events, ['invalidate', 'switch', 'refresh']);
  });

  for (const [name, response, expected] of [
    ['non-JSON', () => new Response('<html>failed</html>', { status: 500 }), 'HTTP 500'],
    ['unstructured', () => Response.json({ error: 'Explicit switch authorization is required.' }, { status: 403 }), 'Explicit switch authorization is required.'],
    ['missing fields', () => Response.json({ message: 'incomplete' }, { status: 409 }), 'HTTP 409'],
    ['wrong field types', () => Response.json({ ...nativeResult('AG2_BUSY', 'Busy'), manualRecoveryRequired: 'true' }, { status: 409 }), 'HTTP 409'],
    ['contradictory HTTP success', () => Response.json(nativeResult('SUCCESS', 'Completed'), { status: 500 }), 'HTTP 500'],
    ['malformed success', () => Response.json({ success: true }), 'Invalid switch response; inspect switching status before retrying.']
  ] as const) {
    it(`${name} has a safe fallback and still refreshes`, async t => {
      mockSwitchFetch(t, async () => response());
      let refreshed = 0;
      await assert.rejects(executeDashboardSwitch('synthetic-target', {
        client: new ApiClient(), refreshGate: new DashboardRefreshGate(), invalidateSafety: () => {},
        refreshAll: async () => { refreshed++; }
      }), error => {
        assert.ok(error instanceof Error);
        assert.equal(error instanceof SwitchRequestError, false);
        assert.equal(getSwitchErrorMessage(error), expected);
        return true;
      });
      assert.equal(refreshed, 1);
    });
  }

  it('network failure revokes prior safety and reconciles without asserting a switch outcome', async t => {
    const networkError = new Error('Synthetic network failure');
    mockSwitchFetch(t, async () => { throw networkError; });
    let safetyAuthoritative = true;
    let refreshed = false;
    await assert.rejects(executeDashboardSwitch('synthetic-target', {
      client: new ApiClient(), refreshGate: new DashboardRefreshGate(),
      invalidateSafety: () => { safetyAuthoritative = false; },
      // A failed authoritative read cannot restore the previous clear safety snapshot.
      refreshAll: async () => { refreshed = true; }
    }), error => error === networkError);
    assert.equal(refreshed, true);
    assert.equal(canExecuteLifecycleMutation(safetyAuthoritative, clearStatus), false);
  });

  it('intent-bootstrap failure also reconciles and preserves the request error', async t => {
    t.mock.method(globalThis, 'fetch', async (input: Parameters<typeof fetch>[0]) => {
      assert.equal(String(input), '/api/switching/intent');
      return Response.json({ error: 'Intent unavailable' }, { status: 503 });
    });
    let refreshed = 0;
    await assert.rejects(executeDashboardSwitch('synthetic-target', {
      client: new ApiClient(), refreshGate: new DashboardRefreshGate(), invalidateSafety: () => {},
      refreshAll: async () => { refreshed++; }
    }), /Intent unavailable/);
    assert.equal(refreshed, 1);
  });

  it('waits for reconciliation and prevents pre-attempt or overlapping older reads from publishing', async t => {
    let finishRefresh!: () => void;
    const refreshPending = new Promise<void>(resolve => { finishRefresh = resolve; });
    let enterRefresh!: () => void;
    const refreshEntered = new Promise<void>(resolve => { enterRefresh = resolve; });
    const gate = new DashboardRefreshGate();
    const oldTicket = gate.beginRead('switch');
    let settled = false;
    mockSwitchFetch(t, async () => Response.json(nativeResult('SUCCESS', 'Verified')));
    const attempt = executeDashboardSwitch('synthetic-target', {
      client: new ApiClient(), refreshGate: gate, invalidateSafety: () => {},
      refreshAll: async () => {
        const olderRefresh = gate.beginRead('switch');
        const newerRefresh = gate.beginRead('switch');
        assert.equal(gate.canPublish(oldTicket), false);
        assert.equal(gate.canPublish(olderRefresh), false);
        assert.equal(gate.canPublish(newerRefresh), true);
        enterRefresh();
        await refreshPending;
      }
    }).then(() => { settled = true; });
    await refreshEntered;
    assert.equal(settled, false);
    finishRefresh();
    await attempt;
    assert.equal(settled, true);
  });
});
