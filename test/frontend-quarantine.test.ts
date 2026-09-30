import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  mapJournalRecoveryCopy,
  getBannerCopy,
  isValidJournalRecoveryState,
  getRecoveryActivityLogMessage,
  canExecuteLifecycleMutation,
  reconcileRecoveryCopy
} from '../frontend/src/lib/utils/recovery.js';
import { ApiClient, FAIL_CLOSED_RESOLUTION_RESULT } from '../frontend/src/lib/api/client.js';
import type { JournalResolutionResult, SwitchStatusDto } from '../frontend/src/lib/api/types.js';

describe('Frontend Switch Journal Recovery & Quarantine Business Logic', () => {
  describe('authoritative recovery completion reconciliation', () => {
    it('cleanup failure then retry success remains blocked while quarantine survives', () => {
      const failed = { status: 'PersistenceFailure', restartRequired: false } as const;
      const initial: SwitchStatusDto = { currentState: 'FAILED', quarantineActive: true, journalRecoveryState: 'ACTION_REQUIRED' };
      assert.equal(canExecuteLifecycleMutation(true, initial), false);
      assert.equal(reconcileRecoveryCopy(failed, initial, true).alertType, 'error');
      // Accept both the corrected backend flag and an older backend's false flag.
      for (const restartRequired of [true, false]) {
        const after: SwitchStatusDto = { currentState: 'IDLE', quarantineActive: true, journalRecoveryState: 'NONE' };
        const copy = reconcileRecoveryCopy({ status: 'CleanCleanupCompleted', restartRequired }, after, true);
        assert.equal(canExecuteLifecycleMutation(true, after), false);
        assert.equal(copy.isRestartRequired, true);
        assert.match(copy.description, /switching remains blocked/i);
        assert.match(copy.description, /system tray/i);
        assert.doesNotMatch(copy.description, /No further recovery action is required/i);
        assert.notEqual(copy.alertType, 'success');
      }
    });

    it('only genuinely clear authoritative status permits full completion guidance', () => {
      const clear: SwitchStatusDto = { currentState: 'IDLE', quarantineActive: false, journalRecoveryState: 'NONE' };
      const result = { status: 'CleanCleanupCompleted', restartRequired: false } as const;
      assert.match(reconcileRecoveryCopy(result, clear, true).description, /No further recovery action is required/);
      assert.equal(canExecuteLifecycleMutation(true, clear), true);
      for (const status of [null, { ...clear, activeTransactionId: 'synthetic' }, { ...clear, journalRecoveryState: 'UNKNOWN' } as SwitchStatusDto]) {
        assert.notEqual(reconcileRecoveryCopy(result, status, true).alertType, 'success');
      }
      assert.notEqual(reconcileRecoveryCopy(result, clear, false).alertType, 'success');
      assert.equal(reconcileRecoveryCopy({ ...result, restartRequired: true }, clear, true).isRestartRequired, true);
      for (const journalRecoveryState of ['ACTION_REQUIRED', 'NOT_RESOLVABLE', 'UNKNOWN', 'RESTART_REQUIRED'] as const) {
        const status = { ...clear, journalRecoveryState };
        assert.equal(canExecuteLifecycleMutation(true, status), false);
        assert.notEqual(reconcileRecoveryCopy(result, status, true).alertType, 'success');
      }
    });
  });
  describe('getBannerCopy', () => {
    it('returns null when quarantine is not active and recovery state is NONE', () => {
      const banner = getBannerCopy(false, 'NONE');
      assert.equal(banner, null);
    });

    it('returns actionable warning banner when journalRecoveryState is ACTION_REQUIRED', () => {
      const banner = getBannerCopy(true, 'ACTION_REQUIRED');
      assert.notEqual(banner, null);
      assert.equal(banner!.bannerType, 'warning');
      assert.equal(banner!.title, 'Safety Quarantine Active: Switch Recovery Required');
      assert.equal(banner!.showActionButton, true);
      assert.equal(banner!.actionLabel, 'Resolve Journal...');
      assert.equal(banner!.isRestartRequired, false);
    });

    it('returns restart notice when journalRecoveryState is RESTART_REQUIRED', () => {
      const banner = getBannerCopy(true, 'RESTART_REQUIRED');
      assert.notEqual(banner, null);
      assert.equal(banner!.bannerType, 'info');
      assert.equal(banner!.title, 'Switch Journal Resolved: Restart Required');
      assert.equal(banner!.showActionButton, false);
      assert.equal(banner!.isRestartRequired, true);
    });

    it('returns error banner with restart guidance when journalRecoveryState is UNKNOWN', () => {
      const banner = getBannerCopy(true, 'UNKNOWN');
      assert.notEqual(banner, null);
      assert.equal(banner!.bannerType, 'error');
      assert.equal(banner!.title, 'Safety Quarantine Active: Recovery State Unknown');
      assert.equal(banner!.showActionButton, false);
      assert.equal(banner!.isRestartRequired, true);
    });

    it('returns error banner without action button or restart guidance when journalRecoveryState is NOT_RESOLVABLE', () => {
      const banner = getBannerCopy(true, 'NOT_RESOLVABLE');
      assert.notEqual(banner, null);
      assert.equal(banner!.bannerType, 'error');
      assert.equal(banner!.title, 'Safety Quarantine Active: Journal Not Resolvable');
      assert.equal(banner!.showActionButton, false);
      assert.equal(banner!.isRestartRequired, false);
    });

    it('distinguishes non-journal quarantine (NONE with quarantineActive=true)', () => {
      const banner = getBannerCopy(true, 'NONE');
      assert.notEqual(banner, null);
      assert.equal(banner!.bannerType, 'warning');
      assert.equal(banner!.title, 'Safety Quarantine Active');
      assert.equal(banner!.showActionButton, false);
      assert.equal(banner!.isRestartRequired, true);
    });
  });

  describe('mapJournalRecoveryCopy', () => {
    it('handles null, undefined, or malformed result objects safely', () => {
      const copy1 = mapJournalRecoveryCopy(null);
      assert.equal(copy1.title, 'Resolution Incomplete');
      assert.equal(copy1.alertType, 'error');
      assert.equal(copy1.canRetry, true);

      const copy2 = mapJournalRecoveryCopy({});
      assert.equal(copy2.title, 'Resolution Incomplete');
      assert.equal(copy2.alertType, 'error');
      assert.equal(copy2.canRetry, true);
    });

    it('maps CleanCleanupCompleted correctly', () => {
      const copy = mapJournalRecoveryCopy({
        status: 'CleanCleanupCompleted',
        message: 'Cleaned up',
        restartRequired: false
      });
      assert.equal(copy.title, 'Switch Journal Cleaned Up');
      assert.equal(copy.alertType, 'success');
      assert.equal(copy.canRetry, false);
      assert.equal(copy.isRestartRequired, false);
    });

    it('maps ResolvedRestartRequired correctly', () => {
      const copy = mapJournalRecoveryCopy({
        status: 'ResolvedRestartRequired',
        message: 'Resolved',
        restartRequired: true,
        coherentAccountId: 'acc-1'
      });
      assert.equal(copy.title, 'Recovery Completed — Restart Required');
      assert.equal(copy.alertType, 'info');
      assert.equal(copy.canRetry, false);
      assert.equal(copy.isRestartRequired, true);
      assert.match(copy.description, /Windows system tray/i);
    });

    it('maps NoJournal correctly', () => {
      const copy = mapJournalRecoveryCopy({
        status: 'NoJournal',
        message: 'No journal',
        restartRequired: false
      });
      assert.equal(copy.title, 'No Journal Present');
      assert.equal(copy.alertType, 'info');
      assert.equal(copy.canRetry, false);
    });

    describe('NotResolvable reason codes', () => {
      it('maps CORRUPT_JOURNAL correctly', () => {
        const copy = mapJournalRecoveryCopy({
          status: 'NotResolvable',
          reasonCode: 'CORRUPT_JOURNAL'
        });
        assert.equal(copy.title, 'Corrupt Journal Detected');
        assert.equal(copy.alertType, 'error');
        assert.equal(copy.canRetry, false);
      });

      it('maps UNSUPPORTED_VERSION correctly', () => {
        const copy = mapJournalRecoveryCopy({
          status: 'NotResolvable',
          reasonCode: 'UNSUPPORTED_VERSION'
        });
        assert.equal(copy.title, 'Unsupported Journal Schema');
        assert.equal(copy.alertType, 'error');
        assert.equal(copy.canRetry, false);
      });

      it('maps fallback NotResolvable correctly', () => {
        const copy = mapJournalRecoveryCopy({
          status: 'NotResolvable',
          reasonCode: 'OTHER'
        });
        assert.equal(copy.title, 'Automatic Resolution Not Available');
        assert.equal(copy.alertType, 'error');
        assert.equal(copy.canRetry, false);
      });
    });

    describe('ProofFailed reason codes', () => {
      const testCases: Array<{
        reasonCode: string;
        expectedTitle: string;
        expectedCanRetry: boolean;
      }> = [
        { reasonCode: 'LIVE_IDENTITY_UNAVAILABLE', expectedTitle: 'Live Identity Unavailable', expectedCanRetry: true },
        { reasonCode: 'LIVE_IDENTITY_MISMATCH', expectedTitle: 'Identity Mismatch', expectedCanRetry: true },
        { reasonCode: 'NO_ACTIVE_ACCOUNT', expectedTitle: 'No Active Account', expectedCanRetry: false },
        { reasonCode: 'ACTIVE_ACCOUNT_NOT_FOUND', expectedTitle: 'Active Account Not Found', expectedCanRetry: false },
        { reasonCode: 'ACCOUNT_NOT_ENROLLED', expectedTitle: 'Account Not Validly Enrolled', expectedCanRetry: false },
        { reasonCode: 'CREDENTIAL_MISSING', expectedTitle: 'Credential Missing', expectedCanRetry: false },
        { reasonCode: 'VAULT_SESSION_MISSING', expectedTitle: 'Vault Session Missing', expectedCanRetry: false },
        { reasonCode: 'CREDENTIAL_MISMATCH', expectedTitle: 'Credential Mismatch', expectedCanRetry: true },
        { reasonCode: 'CONCURRENT_MUTATION', expectedTitle: 'Concurrent Change Detected', expectedCanRetry: true },
        { reasonCode: 'UNKNOWN_REASON', expectedTitle: 'Coherence Verification Failed', expectedCanRetry: true }
      ];

      for (const tc of testCases) {
        it(`maps ProofFailed with ${tc.reasonCode}`, () => {
          const copy = mapJournalRecoveryCopy({
            status: 'ProofFailed',
            reasonCode: tc.reasonCode
          });
          assert.equal(copy.title, tc.expectedTitle);
          assert.equal(copy.alertType, 'warning');
          assert.equal(copy.canRetry, tc.expectedCanRetry);
          assert.equal(copy.isRestartRequired, false);
        });
      }
    });

    describe('PersistenceFailure reason codes', () => {
      const testCases: Array<{
        reasonCode: string;
        expectedTitle: string;
        expectedCanRetry: boolean;
      }> = [
        { reasonCode: 'LOCK_TIMEOUT', expectedTitle: 'Lock Contention Timeout', expectedCanRetry: true },
        { reasonCode: 'LEASE_ACQUISITION_FAILED', expectedTitle: 'Cross-Process Contention', expectedCanRetry: true },
        { reasonCode: 'IO_ERROR', expectedTitle: 'Journal Access Error', expectedCanRetry: true },
        { reasonCode: 'UNSUPPORTED_PLATFORM', expectedTitle: 'Platform Not Supported', expectedCanRetry: false },
        { reasonCode: 'DELETE_FAILED', expectedTitle: 'Journal Deletion Failed', expectedCanRetry: true },
        { reasonCode: 'UNKNOWN_PERSISTENCE', expectedTitle: 'Persistence Failure', expectedCanRetry: true }
      ];

      for (const tc of testCases) {
        it(`maps PersistenceFailure with ${tc.reasonCode}`, () => {
          const copy = mapJournalRecoveryCopy({
            status: 'PersistenceFailure',
            reasonCode: tc.reasonCode
          });
          assert.equal(copy.title, tc.expectedTitle);
          assert.equal(copy.alertType, 'error');
          assert.equal(copy.canRetry, tc.expectedCanRetry);
        });
      }
    });

    it('ensures zero path, token, or internal implementation leakage in user-facing copy', () => {
      const allResults: Partial<JournalResolutionResult>[] = [
        { status: 'CleanCleanupCompleted' },
        { status: 'ResolvedRestartRequired' },
        { status: 'NoJournal' },
        { status: 'NotResolvable', reasonCode: 'CORRUPT_JOURNAL' },
        { status: 'NotResolvable', reasonCode: 'UNSUPPORTED_VERSION' },
        { status: 'ProofFailed', reasonCode: 'LIVE_IDENTITY_UNAVAILABLE' },
        { status: 'ProofFailed', reasonCode: 'LIVE_IDENTITY_MISMATCH' },
        { status: 'ProofFailed', reasonCode: 'NO_ACTIVE_ACCOUNT' },
        { status: 'ProofFailed', reasonCode: 'ACTIVE_ACCOUNT_NOT_FOUND' },
        { status: 'ProofFailed', reasonCode: 'ACCOUNT_NOT_ENROLLED' },
        { status: 'ProofFailed', reasonCode: 'CREDENTIAL_MISSING' },
        { status: 'ProofFailed', reasonCode: 'VAULT_SESSION_MISSING' },
        { status: 'ProofFailed', reasonCode: 'CREDENTIAL_MISMATCH' },
        { status: 'ProofFailed', reasonCode: 'CONCURRENT_MUTATION' },
        { status: 'PersistenceFailure', reasonCode: 'LOCK_TIMEOUT' },
        { status: 'PersistenceFailure', reasonCode: 'LEASE_ACQUISITION_FAILED' },
        { status: 'PersistenceFailure', reasonCode: 'IO_ERROR' },
        { status: 'PersistenceFailure', reasonCode: 'UNSUPPORTED_PLATFORM' },
        { status: 'PersistenceFailure', reasonCode: 'DELETE_FAILED' }
      ];

      for (const res of allResults) {
        const copy = mapJournalRecoveryCopy(res);
        const text = `${copy.title} ${copy.description}`;
        assert.doesNotMatch(text, /AppData/i);
        assert.doesNotMatch(text, /LOCALAPPDATA/i);
        assert.doesNotMatch(text, /switch-journal\.json/i);
        assert.doesNotMatch(text, /sessions\.dat/i);
        assert.doesNotMatch(text, /bearer/i);
        assert.doesNotMatch(text, /C:\\/i);
        assert.doesNotMatch(text, /token/i);
      }
    });

    it('ensures NotResolvable copy has isRestartRequired false and no restart or delete advice', () => {
      const corruptCopy = mapJournalRecoveryCopy({
        status: 'NotResolvable',
        reasonCode: 'CORRUPT_JOURNAL'
      });
      assert.equal(corruptCopy.isRestartRequired, false);
      assert.doesNotMatch(corruptCopy.description, /restart/i);
      assert.doesNotMatch(corruptCopy.description, /delete/i);

      const unsupportedCopy = mapJournalRecoveryCopy({
        status: 'NotResolvable',
        reasonCode: 'UNSUPPORTED_VERSION'
      });
      assert.equal(unsupportedCopy.isRestartRequired, false);
      assert.doesNotMatch(unsupportedCopy.description, /restart/i);
      assert.doesNotMatch(unsupportedCopy.description, /delete/i);
    });

    it('ensures DELETE_FAILED has isRestartRequired false and failure copy', () => {
      const copy = mapJournalRecoveryCopy({
        status: 'PersistenceFailure',
        reasonCode: 'DELETE_FAILED'
      });
      assert.equal(copy.isRestartRequired, false);
      assert.match(copy.description, /switch journal could not be deleted from disk|file permissions/i);
    });
  });

  describe('isValidJournalRecoveryState', () => {
    it('validates known recovery states accurately', () => {
      assert.equal(isValidJournalRecoveryState('NONE'), true);
      assert.equal(isValidJournalRecoveryState('ACTION_REQUIRED'), true);
      assert.equal(isValidJournalRecoveryState('RESTART_REQUIRED'), true);
      assert.equal(isValidJournalRecoveryState('NOT_RESOLVABLE'), true);
      assert.equal(isValidJournalRecoveryState('UNKNOWN'), true);
    });

    it('rejects unknown or invalid types fail-closed', () => {
      assert.equal(isValidJournalRecoveryState(null), false);
      assert.equal(isValidJournalRecoveryState(undefined), false);
      assert.equal(isValidJournalRecoveryState(''), false);
      assert.equal(isValidJournalRecoveryState('INVALID'), false);
      assert.equal(isValidJournalRecoveryState(123), false);
      assert.equal(isValidJournalRecoveryState({}), false);
    });
  });

  describe('getRecoveryActivityLogMessage', () => {
    it('maps all valid resolution statuses to fixed safe activity log strings', () => {
      assert.equal(
        getRecoveryActivityLogMessage('CleanCleanupCompleted'),
        'Switch journal cleaned up successfully. Current safety status determines whether switching can resume.'
      );
      assert.equal(
        getRecoveryActivityLogMessage('ResolvedRestartRequired'),
        'Journal resolution succeeded. AG2 Router must be restarted from the Windows system tray before normal routing resumes.'
      );
      assert.equal(
        getRecoveryActivityLogMessage('NoJournal'),
        'Journal resolution completed: no switch journal was present on disk.'
      );
      assert.equal(
        getRecoveryActivityLogMessage('NotResolvable'),
        'Journal resolution failed: switch journal artifact cannot be resolved automatically.'
      );
      assert.equal(
        getRecoveryActivityLogMessage('ProofFailed'),
        'Journal resolution failed: account state coherence could not be verified.'
      );
      assert.equal(
        getRecoveryActivityLogMessage('PersistenceFailure'),
        'Journal resolution failed: a persistence or lock error occurred.'
      );
      assert.equal(
        getRecoveryActivityLogMessage('UNKNOWN_STATUS' as any),
        'Journal resolution ended with an unresolved state.'
      );
    });
  });

  describe('ApiClient.resolveQuarantine HTTP status & result consistency (F-286-1)', () => {
    function mockFetch(resolutionResponse: {
      ok: boolean;
      status: number;
      statusText?: string;
      headers?: Record<string, string>;
      json: () => Promise<unknown>;
    }) {
      return (async (input: any) => {
        const url = typeof input === 'string' ? input : input.toString();
        if (url.includes('/api/switching/intent')) {
          return {
            ok: true,
            status: 200,
            statusText: 'OK',
            headers: new Headers({ 'x-ag2-switch-token': 'token-intent-test' }),
            json: async () => ({ ready: true })
          } as unknown as Response;
        }
        return {
          ok: resolutionResponse.ok,
          status: resolutionResponse.status,
          statusText: resolutionResponse.statusText || (resolutionResponse.ok ? 'OK' : 'Error'),
          headers: new Headers(resolutionResponse.headers || {}),
          json: resolutionResponse.json
        } as unknown as Response;
      }) as unknown as typeof fetch;
    }

    it('intent token acquisition failure maps fail-closed to PersistenceFailure', async () => {
      const originalFetch = globalThis.fetch;
      try {
        globalThis.fetch = (async (input: any) => {
          const url = typeof input === 'string' ? input : input.toString();
          if (url.includes('/api/switching/intent')) {
            return {
              ok: false,
              status: 503,
              statusText: 'Service Unavailable',
              headers: new Headers(),
              json: async () => ({ error: 'intent acquisition failed' })
            } as unknown as Response;
          }
          return {
            ok: true,
            status: 200,
            statusText: 'OK',
            headers: new Headers(),
            json: async () => ({ status: 'ResolvedRestartRequired', restartRequired: true })
          } as unknown as Response;
        }) as unknown as typeof fetch;

        const client = new ApiClient();
        const result = await client.resolveQuarantine();
        assert.equal(result.status, 'PersistenceFailure');
        assert.equal(result.restartRequired, false);
        assert.equal(result.reasonCode, 'UNKNOWN');
      } finally {
        globalThis.fetch = originalFetch;
      }
    });

    it('HTTP 500 carrying ResolvedRestartRequired payload maps fail-closed to PersistenceFailure', async () => {
      const originalFetch = globalThis.fetch;
      try {
        globalThis.fetch = mockFetch({
          ok: false,
          status: 500,
          statusText: 'Internal Server Error',
          headers: { 'x-ag2-switch-token': 'token-500' },
          json: async () => ({
            status: 'ResolvedRestartRequired',
            restartRequired: true,
            coherentAccountId: 'acc-target'
          })
        });

        const client = new ApiClient();
        const result = await client.resolveQuarantine();
        assert.equal(result.status, 'PersistenceFailure');
        assert.equal(result.restartRequired, false);
        assert.equal(result.reasonCode, 'UNKNOWN');
        assert.equal(result.message, FAIL_CLOSED_RESOLUTION_RESULT.message);
      } finally {
        globalThis.fetch = originalFetch;
      }
    });

    it('HTTP 200 with ResolvedRestartRequired but restartRequired: false maps fail-closed', async () => {
      const originalFetch = globalThis.fetch;
      try {
        globalThis.fetch = mockFetch({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: async () => ({
            status: 'ResolvedRestartRequired',
            restartRequired: false,
            coherentAccountId: 'acc-target'
          })
        });

        const client = new ApiClient();
        const result = await client.resolveQuarantine();
        assert.equal(result.status, 'PersistenceFailure');
        assert.equal(result.restartRequired, false);
        assert.equal(result.reasonCode, 'UNKNOWN');
      } finally {
        globalThis.fetch = originalFetch;
      }
    });

    it('HTTP 500 with CleanCleanupCompleted maps fail-closed', async () => {
      const originalFetch = globalThis.fetch;
      try {
        globalThis.fetch = mockFetch({
          ok: false,
          status: 500,
          statusText: 'Internal Server Error',
          json: async () => ({
            status: 'CleanCleanupCompleted',
            restartRequired: false
          })
        });

        const client = new ApiClient();
        const result = await client.resolveQuarantine();
        assert.equal(result.status, 'PersistenceFailure');
        assert.equal(result.restartRequired, false);
        assert.equal(result.reasonCode, 'UNKNOWN');
      } finally {
        globalThis.fetch = originalFetch;
      }
    });

    it('HTTP 500 with NoJournal maps fail-closed', async () => {
      const originalFetch = globalThis.fetch;
      try {
        globalThis.fetch = mockFetch({
          ok: false,
          status: 500,
          statusText: 'Internal Server Error',
          json: async () => ({
            status: 'NoJournal',
            restartRequired: false
          })
        });

        const client = new ApiClient();
        const result = await client.resolveQuarantine();
        assert.equal(result.status, 'PersistenceFailure');
        assert.equal(result.restartRequired, false);
        assert.equal(result.reasonCode, 'UNKNOWN');
      } finally {
        globalThis.fetch = originalFetch;
      }
    });

    it('cleanup can require restart while contradictory failure restart flags still fail closed', async () => {
      const originalFetch = globalThis.fetch;
      try {
        globalThis.fetch = mockFetch({
          ok: true,
          status: 200,
          statusText: 'OK',
          json: async () => ({
            status: 'CleanCleanupCompleted',
            restartRequired: true
          })
        });

        const client = new ApiClient();
        const result = await client.resolveQuarantine();
        assert.equal(result.status, 'CleanCleanupCompleted');
        assert.equal(result.restartRequired, true);

        globalThis.fetch = mockFetch({
          ok: false,
          status: 409,
          statusText: 'Conflict',
          json: async () => ({
            status: 'NotResolvable',
            restartRequired: true,
            reasonCode: 'CORRUPT_JOURNAL'
          })
        });

        const failResult = await client.resolveQuarantine();
        assert.equal(failResult.status, 'PersistenceFailure');
        assert.equal(failResult.restartRequired, false);
      } finally {
        globalThis.fetch = originalFetch;
      }
    });

    it('hostile message strings in HTTP 500 or contradictory responses are not adopted into returned result', async () => {
      const originalFetch = globalThis.fetch;
      try {
        const hostilePayload = {
          status: 'PersistenceFailure',
          message: 'HOSTILE_INJECTION_<script>alert(1)</script>_EXFIL',
          reasonCode: 'IO_ERROR',
          restartRequired: false
        };
        globalThis.fetch = mockFetch({
          ok: false,
          status: 500,
          statusText: 'HOSTILE_STATUS_TEXT',
          json: async () => hostilePayload
        });

        const client = new ApiClient();
        const result = await client.resolveQuarantine();
        assert.equal(result.message, 'A persistence failure occurred during journal resolution.');
        assert.doesNotMatch(result.message, /HOSTILE/);
        assert.doesNotMatch(result.message, /script/);
      } finally {
        globalThis.fetch = originalFetch;
      }
    });

    it('HTTP 200 with valid ResolvedRestartRequired succeeds with client-owned safe message and restartRequired true', async () => {
      const originalFetch = globalThis.fetch;
      try {
        globalThis.fetch = mockFetch({
          ok: true,
          status: 200,
          statusText: 'OK',
          headers: { 'x-ag2-switch-token': 'token-xyz' },
          json: async () => ({
            status: 'ResolvedRestartRequired',
            restartRequired: true,
            coherentAccountId: 'acc-target',
            message: 'raw server string should be ignored'
          })
        });

        const client = new ApiClient();
        const result = await client.resolveQuarantine();
        assert.equal(result.status, 'ResolvedRestartRequired');
        assert.equal(result.restartRequired, true);
        assert.equal(result.coherentAccountId, 'acc-target');
        assert.equal(result.message, 'Switch journal successfully resolved and removed. Application restart is required before normal routing resumes.');
      } finally {
        globalThis.fetch = originalFetch;
      }
    });
  });

  describe('canExecuteLifecycleMutation & Confirmation Safety Guard (F-286-3)', () => {
    it('allows mutation only when safety is authoritative, quarantineActive is false, and journalRecoveryState is NONE', () => {
      const allowed = canExecuteLifecycleMutation(true, {
        quarantineActive: false,
        journalRecoveryState: 'NONE'
      });
      assert.equal(allowed, true);
    });

    it('blocks mutation when quarantineActive is true', () => {
      const blocked = canExecuteLifecycleMutation(true, {
        quarantineActive: true,
        journalRecoveryState: 'NONE'
      });
      assert.equal(blocked, false);
    });

    it('blocks mutation for non-NONE journal recovery states', () => {
      const states = ['ACTION_REQUIRED', 'RESTART_REQUIRED', 'NOT_RESOLVABLE', 'UNKNOWN'] as const;
      for (const st of states) {
        assert.equal(
          canExecuteLifecycleMutation(true, { quarantineActive: false, journalRecoveryState: st }),
          false,
          `Should block for state ${st}`
        );
      }
    });

    it('blocks mutation when isSafetyAuthoritative is false', () => {
      const blocked = canExecuteLifecycleMutation(false, {
        quarantineActive: false,
        journalRecoveryState: 'NONE'
      });
      assert.equal(blocked, false);
    });

    it('blocks mutation when switchStatus is null, undefined, or malformed', () => {
      assert.equal(canExecuteLifecycleMutation(true, null), false);
      assert.equal(canExecuteLifecycleMutation(true, undefined), false);
      assert.equal(canExecuteLifecycleMutation(true, {} as any), false);
      assert.equal(canExecuteLifecycleMutation(true, { quarantineActive: false } as any), false);
      assert.equal(canExecuteLifecycleMutation(true, { quarantineActive: false, journalRecoveryState: 'INVALID' as any }), false);
    });

    it('simulates confirmation callback: safety revoked at confirmation closes modal and blocks API call', () => {
      let switchModalOpen = true;
      let targetAccount: { id: string; email: string } | null = { id: 'acc-1', email: 'acc@example.com' };
      let globalNotification: { type: string; message: string } | null = null;
      let apiCallCount = 0;

      const currentSwitchStatus: SwitchStatusDto = {
        currentState: 'FAILED',
        quarantineActive: true,
        journalRecoveryState: 'ACTION_REQUIRED'
      };
      const isSafetyAuthoritative = true;

      const lifecycleMutationsAllowed = canExecuteLifecycleMutation(isSafetyAuthoritative, currentSwitchStatus);
      function checkLifecycleMutationsAllowed(): boolean {
        if (!lifecycleMutationsAllowed) {
          switchModalOpen = false;
          targetAccount = null;
          globalNotification = {
            type: 'error',
            message: 'Operation cancelled: safety quarantine is active or recovery is pending.'
          };
          return false;
        }
        return true;
      }

      function handleConfirmSwitch() {
        if (!checkLifecycleMutationsAllowed()) {
          return;
        }
        apiCallCount++;
      }

      handleConfirmSwitch();

      assert.equal(apiCallCount, 0, 'API call must not be issued');
      assert.equal(switchModalOpen, false, 'Modal must be closed');
      assert.equal(targetAccount, null, 'Target account must be cleared');
      assert.notEqual(globalNotification, null);
      assert.equal(globalNotification!.type, 'error');
      assert.match(globalNotification!.message, /Operation cancelled.*quarantine/i);
    });
  });
});
