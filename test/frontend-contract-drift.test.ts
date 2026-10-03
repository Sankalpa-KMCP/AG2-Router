import { describe, it, before, after } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as http from 'node:http';
import { InMemoryAccountStore } from '../src/accounts/account-store.js';
import { AG2AdapterFoundation } from '../src/ag2/adapter.js';
import { normalizeQuotaSnapshot, type RawUserStatusResponse } from '../src/ag2/normalizer.js';
import { QuotaRouter } from '../src/router/router.js';
import { AppServer } from '../src/server/server.js';
import { type AppConfig } from '../src/config/config.js';
import type {
  CanonicalModelDto,
  RouterConfigDto,
  RawModelQuotaDto,
  QuotaSnapshotDto,
  SwitchStatusDto,
  JournalRecoveryState,
  JournalResolutionResult,
  JournalResolutionStatus,
  SystemStatusDto
} from '../frontend/src/lib/api/types.js';
import { summarizeModelFamilies } from '../frontend/src/lib/utils/providerQuota.js';
import { deriveLowestModelQuota, formatQuotaFraction } from '../frontend/src/lib/utils/helpers.js';

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

  describe('Node.js engine compatibility contract (F06)', () => {
    it('declares engine range matching locked build tooling reality', () => {
      const packageJsonPath = path.resolve(process.cwd(), 'package.json');
      const packageLockPath = path.resolve(process.cwd(), 'package-lock.json');
      const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
      const packageLock = JSON.parse(fs.readFileSync(packageLockPath, 'utf8'));

      const rootEngine = packageJson.engines?.node;
      const lockEngine = packageLock.packages?.['']?.engines?.node;

      assert.equal(rootEngine, lockEngine);
      assert.notEqual(rootEngine, undefined);

      // Must not claim broad Node 20.0.0 support (broken by locked Vite and missing import.meta.dirname)
      assert.doesNotMatch(rootEngine, />=20\.0\.0/);

      // Must require at least Node 20.19+ for Node 20 line
      assert.match(rootEngine, /20\.19/);

      // Must require at least Node 22.12+ for Node 22 line
      assert.match(rootEngine, /22\.12/);
    });
  });

  describe('R09: Canonical Model Quota Wire Contract Alignment & Frontend Compatibility', () => {
    const realisticFixture: RawUserStatusResponse = {
      userStatus: {
        email: 'developer@example.com',
        name: 'Jane Developer',
        userTier: {
          id: 'tier-pro',
          name: 'Pro Tier'
        },
        cascadeModelConfigData: {
          clientModelConfigs: [
            {
              label: 'Gemini 3.1 Pro (High)',
              modelOrTier: 'gemini-3.1-pro',
              quotaInfo: {
                remainingFraction: 0.85,
                resetTime: '2026-10-05T12:00:00Z',
                isExhausted: false
              }
            },
            {
              label: 'Gemini 3.8 Flash',
              modelOrTier: 'gemini-3.8-flash',
              quotaInfo: {
                remainingFraction: 0.50,
                resetTime: '2026-10-05T14:00:00Z',
                isExhausted: false
              }
            },
            {
              label: 'Claude Sonnet 4.6 (Thinking)',
              modelOrTier: 'claude-sonnet-4.6',
              quotaInfo: {
                remainingFraction: 0.20,
                resetTime: '2026-10-05T16:00:00Z',
                isExhausted: false
              }
            },
            {
              label: 'Claude Opus 4.6',
              modelOrTier: 'claude-opus-4.6',
              quotaInfo: {
                remainingFraction: 0.40,
                resetTime: '2026-10-05T15:00:00Z',
                isExhausted: false
              }
            },
            {
              label: 'Experimental Custom Model',
              modelOrTier: 'custom-internal-model',
              quotaInfo: {
                remainingFraction: undefined,
                resetTime: undefined,
                isExhausted: false
              }
            },
            {
              label: 'Exhausted Model',
              modelOrTier: 'exhausted-model-tier',
              quotaInfo: {
                remainingFraction: 0.0,
                resetTime: '2026-10-05T10:00:00Z',
                isExhausted: true
              }
            }
          ]
        },
        planStatus: {
          availablePromptCredits: 5000,
          availableFlowCredits: 1000,
          planInfo: {
            monthlyPromptCredits: 10000,
            monthlyFlowCredits: 2000,
            planName: 'Pro Tier'
          }
        }
      }
    };

    let server: AppServer;
    const r09TestPort = 39599;

    const testConfig: AppConfig = {
      host: '127.0.0.1',
      port: r09TestPort,
      storageDir: path.resolve('data'),
      uiDir: path.resolve('src', 'ui'),
      router: {
        autoSwitchEnabled: false,
        lowQuotaThresholdPercent: 15,
        minimumCandidateQuotaPercent: 30,
        pollingIntervalMs: 10000
      },
      isDev: true
    };

    class MockQuotaAdapter extends AG2AdapterFoundation {
      override async discover() {
        return { isRunning: true, status: 'HEALTHY' as const, processInfo: null };
      }
      override async getQuota() {
        return normalizeQuotaSnapshot(realisticFixture);
      }
    }

    before(async () => {
      const store = new InMemoryAccountStore();
      const adapter = new MockQuotaAdapter();
      const router = new QuotaRouter(store, adapter, testConfig.router);
      server = new AppServer(testConfig, store, adapter, router);
      await server.start();
    });

    after(async () => {
      await server.stop();
    });

    function requestStatus(): Promise<{ status: number; body: SystemStatusDto }> {
      return new Promise((resolve, reject) => {
        const req = http.request(
          {
            hostname: '127.0.0.1',
            port: r09TestPort,
            path: '/api/status',
            method: 'GET'
          },
          (res) => {
            let data = '';
            res.on('data', (c) => (data += c));
            res.on('end', () => {
              try {
                resolve({ status: res.statusCode || 0, body: JSON.parse(data) });
              } catch (e) {
                reject(e);
              }
            });
          }
        );
        req.on('error', reject);
        req.end();
      });
    }

    it('end-to-end: GET /api/status produces canonicalModels wire contract matching frontend expectations', async () => {
      const res = await requestStatus();
      assert.equal(res.status, 200);

      const quota = res.body.telemetry?.quota;
      assert.ok(quota, 'telemetry.quota must exist');
      assert.ok(quota.canonicalModels, 'canonicalModels must exist');
      assert.equal(quota.canonicalModels.length, 6, 'All 6 models retained as individual canonical rows');

      // Regression Case 1: Canonical model identity (canonicalKey)
      for (const m of quota.canonicalModels) {
        assert.equal(typeof m.canonicalKey, 'string', 'canonicalKey must be a string');
        assert.ok(m.canonicalKey.length > 0, 'canonicalKey must not be empty');
        assert.equal(m.canonicalKey, (m as unknown as { key: string }).key, 'canonicalKey must equal key');
      }

      // Regression Case 2: Display label (displayLabel)
      for (const m of quota.canonicalModels) {
        assert.equal(typeof m.displayLabel, 'string', 'displayLabel must be a string');
        assert.ok(m.displayLabel.length > 0, 'displayLabel must not be empty');
        assert.equal(m.displayLabel, (m as unknown as { label: string }).label, 'displayLabel must equal label');
        assert.doesNotMatch(m.displayLabel, /\s*\((?:thinking|reasoning)\)\s*$/i, 'Variant annotations cleaned in displayLabel');
      }

      // Regression Case 3: Family summary recognition via summarizeModelFamilies
      // Before R09, identity evaluated to 'undefined undefined' and returned 0 families.
      const families = summarizeModelFamilies(quota);
      assert.ok(families.length >= 3, `Expected at least 3 model families recognized, got ${families.length}`);

      const geminiPro = families.find((f) => f.key === 'gemini-pro');
      assert.ok(geminiPro, 'Gemini 3.1 Pro recognized by summarizeModelFamilies');
      assert.equal(geminiPro!.displayName, 'Gemini 3.1 Pro');
      assert.equal(geminiPro!.percent, 85);
      assert.equal(geminiPro!.health, 'healthy');
      assert.equal(geminiPro!.isExhausted, false);

      const geminiFlash = families.find((f) => f.key === 'gemini-flash');
      assert.ok(geminiFlash, 'Gemini 3.8 Flash recognized by summarizeModelFamilies');
      assert.equal(geminiFlash!.displayName, 'Gemini 3.8 Flash');
      assert.equal(geminiFlash!.percent, 50);
      assert.equal(geminiFlash!.health, 'healthy');
      assert.equal(geminiFlash!.isExhausted, false);

      const claudeSonnet = families.find((f) => f.key === 'claude-sonnet');
      assert.ok(claudeSonnet, 'Sonnet 4.6 recognized by summarizeModelFamilies');
      assert.equal(claudeSonnet!.displayName, 'Sonnet 4.6');
      assert.equal(claudeSonnet!.percent, 20);
      assert.equal(claudeSonnet!.health, 'healthy');
      assert.equal(claudeSonnet!.isExhausted, false);

      const claudeOpus = families.find((f) => f.key === 'claude-opus');
      assert.ok(claudeOpus, 'Opus 4.6 recognized by summarizeModelFamilies');
      assert.equal(claudeOpus!.displayName, 'Opus 4.6');
      assert.equal(claudeOpus!.percent, 40);
      assert.equal(claudeOpus!.health, 'healthy');

      // Regression Case 4: Individual quota row retention & QuotaSection simulation
      // Emulate QuotaSection.svelte row derivation:
      const renderedRows = quota.canonicalModels.map((model, index) => ({
        key: `${model.canonicalKey}-${index}`,
        label: model.displayLabel,
        modes: model.modes,
        percent: formatQuotaFraction(model.remainingFraction),
        resetTime: model.resetTime,
        isExhausted: model.isExhausted
      }));
      assert.equal(renderedRows.length, 6);
      for (const row of renderedRows) {
        assert.doesNotMatch(row.key, /undefined/, `Row key '${row.key}' must not contain undefined`);
        assert.ok(row.label && row.label !== 'undefined', `Row label '${row.label}' must be defined`);
        assert.ok(Array.isArray(row.modes), 'Modes must be an array');
      }

      // Regression Case 5: Unknown model behavior preserved (does not falsely promote)
      const customModel = quota.canonicalModels.find((m) => m.canonicalKey.includes('custom-internal-model'));
      assert.ok(customModel, 'Custom internal model preserved');
      assert.equal(customModel!.displayLabel, 'Experimental Custom Model');
      // Ensure it did not falsely map to any family
      const matchingFam = families.find((f) => f.displayName.includes('Experimental'));
      assert.equal(matchingFam, undefined, 'Unknown model must not create or enter any known family summary');

      // Regression Case 6: Nullable/missing telemetry preserved truthfully
      assert.equal(customModel!.remainingFraction, null, 'Nullable fraction preserved as null');
      assert.equal(customModel!.isExhausted, false, 'Unexhausted null fraction model is not exhausted');

      const exhaustedModel = quota.canonicalModels.find((m) => m.canonicalKey.includes('exhausted-model-tier'));
      assert.ok(exhaustedModel, 'Exhausted model preserved');
      assert.equal(exhaustedModel!.remainingFraction, 0.0, 'Zero remaining fraction preserved');
      assert.equal(exhaustedModel!.isExhausted, true, 'isExhausted is true');

      // Regression Case 7: Multiple canonical models (no key collision or label swapping)
      const allKeys = quota.canonicalModels.map((m) => m.canonicalKey);
      const uniqueKeys = new Set(allKeys);
      assert.equal(uniqueKeys.size, allKeys.length, 'All canonical keys must be unique without collisions');

      // Regression Case 8: Native contract equivalence
      // Verify that every canonical model contains the exact property set emitted by .NET CanonicalModelQuotaDto:
      // Key, Label, CanonicalKey, DisplayLabel, ModelOrTier, RemainingFraction, ResetTime, IsExhausted, Modes
      for (const m of quota.canonicalModels) {
        const record = m as unknown as Record<string, unknown>;
        assert.ok('key' in record, 'Must contain key');
        assert.ok('label' in record, 'Must contain label');
        assert.ok('canonicalKey' in record, 'Must contain canonicalKey');
        assert.ok('displayLabel' in record, 'Must contain displayLabel');
        assert.ok('modelOrTier' in record, 'Must contain modelOrTier');
        assert.ok('remainingFraction' in record, 'Must contain remainingFraction');
        assert.ok('resetTime' in record, 'Must contain resetTime');
        assert.ok('isExhausted' in record, 'Must contain isExhausted');
        assert.ok('modes' in record, 'Must contain modes');
      }
    });

    it('defensive projection: server projects aliases even if adapter provides only legacy fields', async () => {
      const legacyPort = 39598;
      const legacyConfig: AppConfig = { ...testConfig, port: legacyPort };
      const legacyServer = new AppServer(
        legacyConfig,
        new InMemoryAccountStore(),
        {
          discover: async () => ({ isRunning: true, status: 'HEALTHY' as const, processInfo: null }),
          getCurrentAccount: async () => null,
          getQuota: async () => ({
            timestamp: new Date().toISOString(),
            models: [],
            // Legacy canonicalModels without canonicalKey or displayLabel
            canonicalModels: [
              {
                key: 'tier:legacy-model',
                label: 'Legacy Model',
                modelOrTier: 'legacy-model',
                remainingFraction: 0.75,
                resetTime: undefined,
                isExhausted: false,
                modes: ['Standard']
              }
            ] as any
          }),
          getActivityState: async () => ({ state: 'IDLE' as const, totalTrajectories: 0, runningTrajectories: 0, timestamp: '' }),
          switchAccount: async () => { throw new Error('Not implemented'); },
          verifyAccount: async () => false
        },
        new QuotaRouter(new InMemoryAccountStore(), new AG2AdapterFoundation(), legacyConfig.router)
      );

      await legacyServer.start();
      try {
        const res = await new Promise<SystemStatusDto>((resolve, reject) => {
          const req = http.request(
            { hostname: '127.0.0.1', port: legacyPort, path: '/api/status', method: 'GET' },
            (r) => {
              let d = '';
              r.on('data', (c) => (d += c));
              r.on('end', () => resolve(JSON.parse(d)));
            }
          );
          req.on('error', reject);
          req.end();
        });

        const canonical = res.telemetry?.quota?.canonicalModels?.[0];
        assert.ok(canonical);
        assert.equal(canonical.canonicalKey, 'tier:legacy-model');
        assert.equal(canonical.displayLabel, 'Legacy Model');
        assert.equal((canonical as unknown as { key: string }).key, 'tier:legacy-model');
        assert.equal((canonical as unknown as { label: string }).label, 'Legacy Model');
      } finally {
        await legacyServer.stop();
      }
    });
  });
});
