/**
 * Test: AG2 Telemetry Normalization
 */

import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import {
  normalizeAccountIdentity,
  normalizeActivitySnapshot,
  normalizeQuotaSnapshot,
  RawTrajectoriesResponse,
  RawUserStatusResponse
} from '../src/ag2/normalizer.js';

describe('AG2 Normalizer', () => {
  describe('normalizeAccountIdentity', () => {
    it('should normalize full user status account identity', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          email: 'developer@example.com',
          name: 'Developer Jane',
          userTier: {
            id: 'g1-pro-tier',
            name: 'Google AI Pro'
          },
          planStatus: {
            planInfo: {
              planName: 'Pro',
              teamsTier: 'TEAMS_TIER_PRO'
            }
          }
        }
      };

      const account = normalizeAccountIdentity(fixture);
      assert.ok(account);
      assert.equal(account.email, 'developer@example.com');
      assert.equal(account.name, 'Developer Jane');
      assert.equal(account.tierId, 'g1-pro-tier');
      assert.equal(account.tierName, 'Google AI Pro');
      assert.ok(account.rawStatusTimestamp);
    });

    it('should return null when userStatus or email is missing', () => {
      assert.equal(normalizeAccountIdentity(null), null);
      assert.equal(normalizeAccountIdentity({}), null);
      assert.equal(normalizeAccountIdentity({ userStatus: {} }), null);
      assert.equal(normalizeAccountIdentity({ userStatus: { name: 'No Email' } }), null);
    });
  });

  describe('normalizeQuotaSnapshot', () => {
    it('keeps missing, partial and non-finite credits unknown while preserving observed zero', () => {
      const missing = normalizeQuotaSnapshot({ userStatus: { planStatus: { planInfo: {} } } });
      assert.deepEqual(missing?.promptCredits,
        { availableCredits: null, monthlyCredits: null, usedCredits: null });
      assert.deepEqual(missing?.flowCredits,
        { availableCredits: null, monthlyCredits: null, usedCredits: null });

      const partial = normalizeQuotaSnapshot({ userStatus: { planStatus: {
        availablePromptCredits: 7,
        availableFlowCredits: Infinity,
        planInfo: { monthlyFlowCredits: 20, monthlyPromptCredits: NaN }
      } } });
      assert.deepEqual(partial?.promptCredits,
        { availableCredits: 7, monthlyCredits: null, usedCredits: null });
      assert.deepEqual(partial?.flowCredits,
        { availableCredits: null, monthlyCredits: 20, usedCredits: null });

      const zero = normalizeQuotaSnapshot({ userStatus: { planStatus: {
        availablePromptCredits: 0,
        availableFlowCredits: 0,
        planInfo: { monthlyPromptCredits: 0, monthlyFlowCredits: 10 }
      } } });
      assert.deepEqual(zero?.promptCredits,
        { availableCredits: 0, monthlyCredits: 0, usedCredits: 0 });
      assert.deepEqual(zero?.flowCredits,
        { availableCredits: 0, monthlyCredits: 10, usedCredits: 10 });
    });

    it('preserves absent and non-finite model capacity as unknown rather than 100%', () => {
      const values = [undefined, NaN, Infinity, -Infinity, 0, 0.42];
      const result = normalizeQuotaSnapshot({ userStatus: { cascadeModelConfigData: {
        clientModelConfigs: values.map((remainingFraction, index) => ({
          label: `Model ${index}`,
          modelOrTier: `model-${index}`,
          quotaInfo: { remainingFraction }
        }))
      } } });
      assert.ok(result);
      assert.deepEqual(result.models.map(model => model.remainingFraction),
        [null, null, null, null, 0, 0.42]);
      assert.deepEqual(result.models.map(model => model.isExhausted),
        [false, false, false, false, true, false]);
    });

    it('should preserve segregated model quota pools without artificial aggregation', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          email: 'developer@example.com',
          cascadeModelConfigData: {
            clientModelConfigs: [
              {
                label: 'Gemini 3.8 Flash (High)',
                modelOrTier: 'gemini-3.8-flash-high',
                quotaInfo: {
                  remainingFraction: 0.52,
                  resetTime: '2026-09-20T14:00:00Z',
                  isExhausted: false
                }
              },
              {
                label: 'Claude Sonnet 4.6 (Thinking)',
                modelOrTier: 'claude-sonnet-4.6',
                quotaInfo: {
                  remainingFraction: 1.0,
                  resetTime: '2026-09-20T16:00:00Z',
                  isExhausted: false
                }
              },
              {
                label: 'Gemini 3.1 Pro (High)',
                quotaInfo: {
                  remainingFraction: 0.0,
                  resetTime: '2026-09-20T12:00:00Z',
                  isExhausted: true
                }
              }
            ]
          },
          planStatus: {
            availablePromptCredits: 500,
            availableFlowCredits: 100,
            planInfo: {
              monthlyPromptCredits: 50000,
              monthlyFlowCredits: 150000,
              planName: 'Pro'
            }
          }
        }
      };

      const quota = normalizeQuotaSnapshot(fixture);
      assert.ok(quota);
      assert.equal(quota.models.length, 3);

      // Model 1: Gemini 3.8 Flash
      assert.equal(quota.models[0].label, 'Gemini 3.8 Flash (High)');
      assert.equal(quota.models[0].remainingFraction, 0.52);
      assert.equal(quota.models[0].isExhausted, false);

      // Model 2: Claude Sonnet (Separate pool)
      assert.equal(quota.models[1].label, 'Claude Sonnet 4.6 (Thinking)');
      assert.equal(quota.models[1].remainingFraction, 1.0);
      assert.equal(quota.models[1].isExhausted, false);

      // Model 3: Exhausted
      assert.equal(quota.models[2].label, 'Gemini 3.1 Pro (High)');
      assert.equal(quota.models[2].remainingFraction, 0.0);
      assert.equal(quota.models[2].isExhausted, true);

      // Segregated Prompt vs Flow Credits (Invariant: Never combined)
      assert.ok(quota.promptCredits);
      assert.equal(quota.promptCredits.availableCredits, 500);
      assert.equal(quota.promptCredits.monthlyCredits, 50000);
      assert.equal(quota.promptCredits.usedCredits, 49500);

      assert.ok(quota.flowCredits);
      assert.equal(quota.flowCredits.availableCredits, 100);
      assert.equal(quota.flowCredits.monthlyCredits, 150000);
      assert.equal(quota.flowCredits.usedCredits, 149900);

      // Invariant check: pools remain distinct objects
      assert.notEqual(quota.promptCredits, quota.flowCredits);
    });

    it('should return null when userStatus is missing', () => {
      assert.equal(normalizeQuotaSnapshot(null), null);
      assert.equal(normalizeQuotaSnapshot({}), null);
    });

    it('preserves same-tier same-reset reasoning variants as separate capacity rows', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          cascadeModelConfigData: {
            clientModelConfigs: [
              {
                label: 'Gemini 2.5 Pro',
                modelOrTier: 'gemini-2.5-pro',
                quotaInfo: {
                  remainingFraction: 0.85,
                  resetTime: '2026-09-21T21:00:00Z',
                  isExhausted: false
                }
              },
              {
                label: 'Gemini 2.5 Pro (Thinking)',
                modelOrTier: 'gemini-2.5-pro',
                quotaInfo: {
                  remainingFraction: 0.85,
                  resetTime: '2026-09-21T21:00:00Z',
                  isExhausted: false
                }
              }
            ]
          }
        }
      };

      const quota = normalizeQuotaSnapshot(fixture);
      assert.ok(quota);
      // Raw compatibility
      assert.equal(quota.models.length, 2);

      // Equal tier and reset still cannot prove shared capacity.
      assert.ok(quota.canonicalModels);
      assert.equal(quota.canonicalModels.length, 2);
      const canonical = quota.canonicalModels[0];
      assert.equal(canonical.key, 'tier:gemini-2.5-pro');
      assert.equal(canonical.label, 'Gemini 2.5 Pro');
      assert.equal(canonical.canonicalKey, 'tier:gemini-2.5-pro');
      assert.equal(canonical.displayLabel, 'Gemini 2.5 Pro');
      assert.equal(canonical.modelOrTier, 'gemini-2.5-pro');
      assert.equal(canonical.remainingFraction, 0.85);
      assert.equal(canonical.resetTime, '2026-09-21T21:00:00Z');
      assert.equal(canonical.isExhausted, false);
      assert.deepEqual(canonical.modes, ['Standard']);
      assert.equal(quota.canonicalModels[1].key, 'tier:gemini-2.5-pro:row:1');
      assert.equal(quota.canonicalModels[1].canonicalKey, 'tier:gemini-2.5-pro:row:1');
      assert.equal(quota.canonicalModels[1].displayLabel, 'Gemini 2.5 Pro');
      assert.deepEqual(quota.canonicalModels[1].modes, ['Thinking']);
    });

    it('should never merge distinct modelOrTier even with similar labels or suffixes', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          cascadeModelConfigData: {
            clientModelConfigs: [
              {
                label: 'Gemini 3.8 Flash (High)',
                modelOrTier: 'gemini-3.8-flash-high',
                quotaInfo: { remainingFraction: 0.70 }
              },
              {
                label: 'Gemini 3.1 Pro (High)',
                modelOrTier: 'gemini-3.1-pro-high',
                quotaInfo: { remainingFraction: 0.40 }
              },
              {
                label: 'Identical Label Model',
                modelOrTier: 'tier-alpha',
                quotaInfo: { remainingFraction: 0.90 }
              },
              {
                label: 'Identical Label Model',
                modelOrTier: 'tier-beta',
                quotaInfo: { remainingFraction: 0.90 }
              }
            ]
          }
        }
      };

      const quota = normalizeQuotaSnapshot(fixture);
      assert.ok(quota?.canonicalModels);
      assert.equal(quota.canonicalModels.length, 4);
      assert.equal(quota.canonicalModels[0].label, 'Gemini 3.8 Flash (High)');
      assert.equal(quota.canonicalModels[1].label, 'Gemini 3.1 Pro (High)');
      assert.equal(quota.canonicalModels[2].key, 'tier:tier-alpha');
      assert.equal(quota.canonicalModels[3].key, 'tier:tier-beta');
    });

    it('does not treat a presentation label as pool identity even when reset times match', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          cascadeModelConfigData: {
            clientModelConfigs: [
              {
                label: 'Claude 3.7 Sonnet',
                quotaInfo: { remainingFraction: 0.60, resetTime: '2026-09-21T21:00:00Z' }
              },
              {
                label: 'Claude 3.7 Sonnet (Thinking)',
                quotaInfo: { remainingFraction: 0.60, resetTime: '2026-09-21T21:00:00Z' }
              }
            ]
          }
        }
      };

      const quota = normalizeQuotaSnapshot(fixture);
      assert.ok(quota?.canonicalModels);
      assert.equal(quota.canonicalModels.length, 2);
      assert.equal(quota.canonicalModels[0].label, 'Claude 3.7 Sonnet');
      assert.deepEqual(quota.canonicalModels.map(model => model.modes), [['Standard'], ['Thinking']]);
    });

    it('should not merge by label fallback when resetTimes differ', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          cascadeModelConfigData: {
            clientModelConfigs: [
              {
                label: 'Custom Pool',
                quotaInfo: { remainingFraction: 0.50, resetTime: '2026-09-21T18:00:00Z' }
              },
              {
                label: 'Custom Pool (Thinking)',
                quotaInfo: { remainingFraction: 0.50, resetTime: '2026-09-28T00:00:00Z' }
              }
            ]
          }
        }
      };

      const quota = normalizeQuotaSnapshot(fixture);
      assert.ok(quota?.canonicalModels);
      assert.equal(quota.canonicalModels.length, 2);
    });

    it('preserves conflicting rows when no source pool identity proves sharing', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          cascadeModelConfigData: {
            clientModelConfigs: [
              {
                label: 'Conflicted Model',
                modelOrTier: 'conflicted-model',
                quotaInfo: {
                  remainingFraction: 0.80,
                  isExhausted: false,
                  resetTime: '2026-09-21T18:00:00Z'
                }
              },
              {
                label: 'Conflicted Model (Reasoning)',
                modelOrTier: 'conflicted-model',
                quotaInfo: {
                  remainingFraction: 0.30,
                  isExhausted: true,
                  resetTime: '2026-09-21T18:00:00Z'
                }
              }
            ]
          }
        }
      };

      const quota = normalizeQuotaSnapshot(fixture);
      assert.ok(quota?.canonicalModels);
      assert.equal(quota.canonicalModels.length, 2);
      assert.equal(quota.canonicalModels[0].remainingFraction, 0.80);
      assert.equal(quota.canonicalModels[0].isExhausted, false);
      assert.equal(quota.canonicalModels[1].remainingFraction, 0.30);
      assert.equal(quota.canonicalModels[1].isExhausted, true);
      assert.deepEqual(quota.canonicalModels[0].modes, ['Standard']);
      assert.deepEqual(quota.canonicalModels[1].modes, ['Reasoning']);
    });

    it('should force isExhausted to true when remaining fraction is zero', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          cascadeModelConfigData: {
            clientModelConfigs: [
              {
                label: 'Zero Model',
                modelOrTier: 'zero-model',
                quotaInfo: { remainingFraction: 0.0, isExhausted: false }
              }
            ]
          }
        }
      };

      const quota = normalizeQuotaSnapshot(fixture);
      assert.ok(quota?.canonicalModels);
      assert.equal(quota.canonicalModels[0].isExhausted, true);
      assert.equal(quota.canonicalModels[0].remainingFraction, 0.0);
    });

    it('should normalize real-world synthetic Connect-RPC payload with full model and credit segregation', () => {
      const fixture: RawUserStatusResponse = {
        userStatus: {
          email: 'dev@example.com',
          name: 'Developer Jane',
          cascadeModelConfigData: {
            clientModelConfigs: [
              {
                label: 'Gemini 2.5 Pro',
                modelOrTier: 'gemini-2.5-pro',
                quotaInfo: { remainingFraction: 0.90, resetTime: '2026-09-21T22:00:00Z' }
              },
              {
                label: 'Gemini 2.5 Pro (Thinking)',
                modelOrTier: 'gemini-2.5-pro',
                quotaInfo: { remainingFraction: 0.90, resetTime: '2026-09-21T22:00:00Z' }
              },
              {
                label: 'Claude 3.7 Sonnet',
                modelOrTier: 'claude-3-7-sonnet',
                quotaInfo: { remainingFraction: 0.45, resetTime: '2026-09-21T20:00:00Z' }
              },
              {
                label: 'Claude 3.7 Sonnet (Thinking)',
                modelOrTier: 'claude-3-7-sonnet',
                quotaInfo: { remainingFraction: 0.45, resetTime: '2026-09-21T20:00:00Z' }
              },
              {
                label: 'Gemini 2.5 Flash',
                modelOrTier: 'gemini-2.5-flash',
                quotaInfo: { remainingFraction: 1.0, resetTime: '2026-09-21T23:00:00Z' }
              }
            ]
          },
          planStatus: {
            availablePromptCredits: 1500,
            availableFlowCredits: 300,
            planInfo: {
              monthlyPromptCredits: 2000,
              monthlyFlowCredits: 500,
              planName: 'Google AI Pro'
            }
          }
        }
      };

      const quota = normalizeQuotaSnapshot(fixture);
      assert.ok(quota);
      assert.equal(quota.models.length, 5);
      assert.ok(quota.canonicalModels);
      assert.equal(quota.canonicalModels.length, 5);

      assert.equal(quota.canonicalModels[0].label, 'Gemini 2.5 Pro');
      assert.deepEqual(quota.canonicalModels[0].modes, ['Standard']);
      assert.deepEqual(quota.canonicalModels[1].modes, ['Thinking']);

      assert.equal(quota.canonicalModels[2].label, 'Claude 3.7 Sonnet');
      assert.deepEqual(quota.canonicalModels[2].modes, ['Standard']);
      assert.deepEqual(quota.canonicalModels[3].modes, ['Thinking']);

      assert.equal(quota.canonicalModels[4].label, 'Gemini 2.5 Flash');
      assert.deepEqual(quota.canonicalModels[4].modes, ['Standard']);

      assert.equal(quota.promptCredits?.availableCredits, 1500);
      assert.equal(quota.promptCredits?.usedCredits, 500);
      assert.equal(quota.flowCredits?.availableCredits, 300);
      assert.equal(quota.flowCredits?.usedCredits, 200);
    });
  });

  describe('normalizeActivitySnapshot', () => {
    it('should classify as BUSY when at least one trajectory is running', () => {
      const fixture: RawTrajectoriesResponse = {
        trajectorySummaries: {
          'traj-1': {
            summary: 'Active task 1',
            status: 'CASCADE_RUN_STATUS_RUNNING',
            stepCount: 10
          },
          'traj-2': {
            summary: 'Idle task 2',
            status: 'CASCADE_RUN_STATUS_IDLE',
            stepCount: 5
          }
        }
      };

      const activity = normalizeActivitySnapshot(fixture);
      assert.equal(activity.state, 'BUSY');
      assert.equal(activity.totalTrajectories, 2);
      assert.equal(activity.runningTrajectories, 1);
    });

    it('should classify as IDLE when zero trajectories are running', () => {
      const fixture: RawTrajectoriesResponse = {
        trajectorySummaries: {
          'traj-1': {
            summary: 'Done task',
            status: 'CASCADE_RUN_STATUS_IDLE',
            stepCount: 20
          },
          'traj-2': {
            summary: 'Another completed task',
            status: 'CASCADE_RUN_STATUS_DONE',
            stepCount: 15
          }
        }
      };

      const activity = normalizeActivitySnapshot(fixture);
      assert.equal(activity.state, 'IDLE');
      assert.equal(activity.totalTrajectories, 2);
      assert.equal(activity.runningTrajectories, 0);
    });

    it('should require explicit activity evidence before declaring IDLE', () => {
      const activityEmpty = normalizeActivitySnapshot({ trajectorySummaries: {} });
      assert.equal(activityEmpty.state, 'IDLE');
      assert.equal(activityEmpty.totalTrajectories, 0);
      assert.equal(activityEmpty.runningTrajectories, 0);

      const activityNull = normalizeActivitySnapshot(null);
      assert.equal(activityNull.state, 'UNKNOWN');
      assert.equal(activityNull.totalTrajectories, 0);
      assert.equal(activityNull.runningTrajectories, 0);

      assert.equal(normalizeActivitySnapshot({}).state, 'UNKNOWN');
      assert.equal(normalizeActivitySnapshot({ trajectorySummaries: {
        'traj-1': { status: 'CASCADE_RUN_STATUS_UNRECOGNIZED' }
      } }).state, 'UNKNOWN');
      assert.equal(normalizeActivitySnapshot({ trajectorySummaries: [] } as never).state, 'UNKNOWN');
    });
  });
});
