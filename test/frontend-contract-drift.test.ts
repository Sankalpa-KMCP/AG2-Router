import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import type {
  CanonicalModelDto,
  RouterConfigDto,
  RawModelQuotaDto,
  QuotaSnapshotDto,
  SwitchStatusDto,
  JournalRecoveryState,
  JournalResolutionResult,
  JournalResolutionStatus
} from '../frontend/src/lib/api/types.js';
import { summarizeModelFamilies } from '../frontend/src/lib/utils/providerQuota.js';
import { deriveLowestModelQuota } from '../frontend/src/lib/utils/helpers.js';

describe('AUD-103 API contract alignment & drift prevention', () => {
  describe('CanonicalModelDto contract', () => {
    it('adheres to canonical backend properties without phantom reset/rolling window fields', () => {
      const canonicalModel: CanonicalModelDto = {
        canonicalKey: 'gemini-3.8-flash',
        displayLabel: 'Gemini 3.8 Flash',
        modes: ['Standard'],
        remainingFraction: 0.85,
        resetTime: '2026-01-01T00:00:00Z',
        isExhausted: false
      };

      assert.equal(canonicalModel.canonicalKey, 'gemini-3.8-flash');
      assert.equal(canonicalModel.displayLabel, 'Gemini 3.8 Flash');
      assert.deepEqual(canonicalModel.modes, ['Standard']);
      assert.equal(canonicalModel.remainingFraction, 0.85);
      assert.equal(canonicalModel.resetTime, '2026-01-01T00:00:00Z');
      assert.equal(canonicalModel.isExhausted, false);

      // Verify at runtime that phantom fields are undefined
      const record = canonicalModel as unknown as Record<string, unknown>;
      assert.equal(record['timeUntilReset'], undefined);
      assert.equal(record['isRollingWindow'], undefined);
    });
  });

  describe('RouterConfigDto contract', () => {
    it('includes autoSwitchEnabled matching backend RouterConfigDto serialization', () => {
      const config: RouterConfigDto = {
        autoSwitchEnabled: true,
        lowQuotaThresholdPercent: 15,
        minimumCandidateQuotaPercent: 30,
        pollingIntervalMs: 10000
      };

      assert.equal(typeof config.autoSwitchEnabled, 'boolean');
      assert.equal(config.autoSwitchEnabled, true);
      assert.equal(config.lowQuotaThresholdPercent, 15);
      assert.equal(config.minimumCandidateQuotaPercent, 30);
      assert.equal(config.pollingIntervalMs, 10000);
      assert.equal(config.workloadModelKey, undefined);
    });

    it('accepts workloadModelKey matching backend RouterConfigDto serialization', () => {
      const configWithModel: RouterConfigDto = {
        autoSwitchEnabled: true,
        lowQuotaThresholdPercent: 15,
        minimumCandidateQuotaPercent: 30,
        pollingIntervalMs: 10000,
        workloadModelKey: 'gemini-2.5-pro'
      };

      assert.equal(configWithModel.workloadModelKey, 'gemini-2.5-pro');

      const configWithNullModel: RouterConfigDto = {
        autoSwitchEnabled: false,
        lowQuotaThresholdPercent: 15,
        minimumCandidateQuotaPercent: 30,
        pollingIntervalMs: 10000,
        workloadModelKey: null
      };

      assert.equal(configWithNullModel.workloadModelKey, null);
    });
  });

  describe('RawModelQuotaDto contract', () => {
    it('accepts null modelOrTier truthfully without type or runtime failure', () => {
      const rawWithNullTier: RawModelQuotaDto = {
        modelOrTier: null,
        label: 'Custom Model Without Tier',
        remainingFraction: 0.75,
        resetTime: null,
        isExhausted: false
      };

      assert.equal(rawWithNullTier.modelOrTier, null);
      assert.equal(rawWithNullTier.label, 'Custom Model Without Tier');

      // Passes safely to deriveLowestModelQuota without errors
      const summary = deriveLowestModelQuota([rawWithNullTier]);
      assert.notEqual(summary, null);
      assert.equal(summary!.fraction, 0.75);
      assert.equal(summary!.label, 'Custom Model Without Tier');
    });

    it('summarizeModelFamilies extracts truthful identity when modelOrTier is null', () => {
      const quota: QuotaSnapshotDto = {
        timestamp: '2026-01-01T00:00:00Z',
        models: [
          {
            modelOrTier: null,
            label: 'Gemini 3.8 Flash High',
            remainingFraction: 0.60,
            resetTime: null,
            isExhausted: false
          }
        ]
      };

      const families = summarizeModelFamilies(quota);
      assert.equal(families.length, 1);
      assert.equal(families[0].key, 'gemini-flash');
      assert.equal(families[0].displayName, 'Gemini 3.8 Flash');
      assert.equal(families[0].percent, 60);
      assert.equal(families[0].modelCount, 1);
    });
  });

  describe('SwitchStatusDto & JournalRecoveryState contract', () => {
    it('accurately models quarantineActive and journalRecoveryState without inferring from File.Exists', () => {
      const validRecoveryStates: JournalRecoveryState[] = [
        'NONE',
        'ACTION_REQUIRED',
        'RESTART_REQUIRED',
        'NOT_RESOLVABLE',
        'UNKNOWN'
      ];

      for (const state of validRecoveryStates) {
        const dto: SwitchStatusDto = {
          currentState: 'IDLE',
          quarantineActive: state !== 'NONE',
          journalRecoveryState: state
        };

        assert.equal(typeof dto.currentState, 'string');
        assert.equal(typeof dto.quarantineActive, 'boolean');
        assert.equal(dto.journalRecoveryState, state);
      }
    });

    it('allows non-journal quarantine where quarantineActive is true but journalRecoveryState is NONE', () => {
      const dto: SwitchStatusDto = {
        currentState: 'IDLE',
        quarantineActive: true,
        journalRecoveryState: 'NONE'
      };

      assert.equal(dto.quarantineActive, true);
      assert.equal(dto.journalRecoveryState, 'NONE');
    });
  });

  describe('JournalResolutionResult contract', () => {
    it('adheres to loopback POST /api/switching/resolve-quarantine structured response shape', () => {
      const validStatuses: JournalResolutionStatus[] = [
        'NoJournal',
        'CleanCleanupCompleted',
        'ResolvedRestartRequired',
        'NotResolvable',
        'ProofFailed',
        'PersistenceFailure'
      ];

      for (const status of validStatuses) {
        const res: JournalResolutionResult = {
          status,
          message: `Resolution status ${status}`,
          coherentAccountId: status === 'ResolvedRestartRequired' ? 'acc-123' : null,
          restartRequired: status === 'ResolvedRestartRequired',
          reasonCode: status === 'ProofFailed' ? 'LIVE_IDENTITY_UNAVAILABLE' : null
        };

        assert.equal(res.status, status);
        assert.equal(typeof res.message, 'string');
        if (res.coherentAccountId) assert.equal(typeof res.coherentAccountId, 'string');
        if (res.restartRequired !== undefined) assert.equal(typeof res.restartRequired, 'boolean');
        if (res.reasonCode) assert.equal(typeof res.reasonCode, 'string');
      }
    });
  });
});
