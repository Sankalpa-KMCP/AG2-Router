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

    it('should classify as IDLE when summaries are empty or null', () => {
      const activityEmpty = normalizeActivitySnapshot({ trajectorySummaries: {} });
      assert.equal(activityEmpty.state, 'IDLE');
      assert.equal(activityEmpty.totalTrajectories, 0);
      assert.equal(activityEmpty.runningTrajectories, 0);

      const activityNull = normalizeActivitySnapshot(null);
      assert.equal(activityNull.state, 'IDLE');
      assert.equal(activityNull.totalTrajectories, 0);
      assert.equal(activityNull.runningTrajectories, 0);
    });
  });
});
