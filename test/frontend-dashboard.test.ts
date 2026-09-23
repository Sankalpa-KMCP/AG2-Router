import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  resolveAccountDisplayName,
  getAccountSubtitle,
  formatQuotaFraction,
  deriveLowestModelQuota,
  formatResetTime,
  prepareAliasPayload,
  derivePoolsStatusSummary,
  formatCreditPool
} from '../src/dashboard/helpers.js';

describe('Frontend Dashboard Business Logic & Truthful Telemetry', () => {
  describe('resolveAccountDisplayName', () => {
    it('should prioritize Alias over Name and Email', () => {
      const acc = {
        alias: 'Work Primary',
        name: 'John Doe',
        email: 'john@example.com'
      };
      assert.equal(resolveAccountDisplayName(acc), 'Work Primary');
    });

    it('should fallback to Name when Alias is missing or whitespace', () => {
      const acc1 = { alias: '', name: 'Personal Profile', email: 'me@example.com' };
      const acc2 = { alias: '   ', name: 'Personal Profile', email: 'me@example.com' };
      const acc3 = { alias: null, name: 'Personal Profile', email: 'me@example.com' };

      assert.equal(resolveAccountDisplayName(acc1), 'Personal Profile');
      assert.equal(resolveAccountDisplayName(acc2), 'Personal Profile');
      assert.equal(resolveAccountDisplayName(acc3), 'Personal Profile');
    });

    it('should fallback to Email when both Alias and Name are missing', () => {
      const acc = { alias: null, name: '', email: 'fallback@example.com' };
      assert.equal(resolveAccountDisplayName(acc), 'fallback@example.com');
    });

    it('should return Unknown Account for null or undefined input', () => {
      assert.equal(resolveAccountDisplayName(null), 'Unknown Account');
      assert.equal(resolveAccountDisplayName(undefined), 'Unknown Account');
    });
  });

  describe('getAccountSubtitle', () => {
    it('should return email when alias is present', () => {
      const acc = { alias: 'Work', email: 'work@example.com' };
      assert.equal(getAccountSubtitle(acc), 'work@example.com');
    });

    it('should return email when name is present and alias is absent', () => {
      const acc = { name: 'John Doe', email: 'john@example.com' };
      assert.equal(getAccountSubtitle(acc), 'john@example.com');
    });

    it('should return null when neither alias nor name is present (email is primary)', () => {
      const acc = { email: 'john@example.com' };
      assert.equal(getAccountSubtitle(acc), null);
    });
  });

  describe('formatQuotaFraction', () => {
    it('should convert standard fractions to percentages', () => {
      assert.equal(formatQuotaFraction(0.85), 85);
      assert.equal(formatQuotaFraction(0.534), 53);
      assert.equal(formatQuotaFraction(1.0), 100);
      assert.equal(formatQuotaFraction(0.0), 0);
    });

    it('should clamp out-of-bounds values strictly to [0, 100]', () => {
      assert.equal(formatQuotaFraction(-0.2), 0);
      assert.equal(formatQuotaFraction(1.5), 100);
    });

    it('should handle NaN and non-numbers gracefully by returning 0', () => {
      assert.equal(formatQuotaFraction(NaN), 0);
      assert.equal(formatQuotaFraction(undefined as unknown as number), 0);
    });
  });

  describe('deriveLowestModelQuota', () => {
    it('should find the minimum remainingFraction across canonical models without summing', () => {
      const models = [
        { displayLabel: 'Gemini 2.5 Pro', remainingFraction: 0.85, isExhausted: false },
        { displayLabel: 'Gemini 2.0 Flash', remainingFraction: 0.42, isExhausted: false },
        { displayLabel: 'Claude 3.7 Sonnet', remainingFraction: 0.95, isExhausted: false }
      ];

      const result = deriveLowestModelQuota(models);
      assert.notEqual(result, null);
      assert.equal(result!.fraction, 0.42);
      assert.equal(result!.percent, 42);
      assert.equal(result!.label, 'Gemini 2.0 Flash');
      assert.equal(result!.isExhausted, false);
    });

    it('should flag isExhausted if the lowest model is marked exhausted or zero fraction', () => {
      const models = [
        { displayLabel: 'Gemini 2.5 Pro', remainingFraction: 0.0, isExhausted: true },
        { displayLabel: 'Gemini 2.0 Flash', remainingFraction: 0.75, isExhausted: false }
      ];

      const result = deriveLowestModelQuota(models);
      assert.notEqual(result, null);
      assert.equal(result!.fraction, 0.0);
      assert.equal(result!.percent, 0);
      assert.equal(result!.isExhausted, true);
    });

    it('should work seamlessly with raw models when canonicalModels is absent', () => {
      const rawModels = [
        { modelOrTier: 'gemini-2.5-pro', label: 'Gemini 2.5 Pro', remainingFraction: 0.65 },
        { modelOrTier: 'gemini-1.5-flash', label: 'Gemini 1.5 Flash', remainingFraction: 0.35 }
      ];

      const result = deriveLowestModelQuota(rawModels);
      assert.notEqual(result, null);
      assert.equal(result!.percent, 35);
      assert.equal(result!.label, 'Gemini 1.5 Flash');
    });

    it('should return null for empty or null model lists', () => {
      assert.equal(deriveLowestModelQuota([]), null);
      assert.equal(deriveLowestModelQuota(null), null);
      assert.equal(deriveLowestModelQuota(undefined), null);
    });
  });

  describe('formatResetTime', () => {
    it('should format future relative reset times truthfully without guessing Weekly/5-Hour semantics', () => {
      const baseTime = new Date('2026-09-21T12:00:00Z');

      // 45 minutes in future
      const t1 = new Date('2026-09-21T12:45:00Z').toISOString();
      assert.equal(formatResetTime(t1, baseTime), 'Resets in 45m');

      // 3 hours 15 minutes in future
      const t2 = new Date('2026-09-21T15:15:00Z').toISOString();
      assert.equal(formatResetTime(t2, baseTime), 'Resets in 3h 15m');

      // 4 days 18 hours in future
      const t3 = new Date('2026-09-26T06:00:00Z').toISOString();
      assert.equal(formatResetTime(t3, baseTime), 'Resets in 4d 18h');
    });

    it('should report Reset due for timestamps in the past', () => {
      const baseTime = new Date('2026-09-21T12:00:00Z');
      const pastTime = new Date('2026-09-21T11:59:00Z').toISOString();
      assert.equal(formatResetTime(pastTime, baseTime), 'Reset due');
    });

    it('should report No reset scheduled when timestamp is null or empty', () => {
      assert.equal(formatResetTime(null), 'No reset scheduled');
      assert.equal(formatResetTime(''), 'No reset scheduled');
    });
  });

  describe('prepareAliasPayload', () => {
    it('should trim surrounding whitespace', () => {
      assert.deepEqual(prepareAliasPayload('  Work  '), { alias: 'Work' });
    });

    it('should produce empty string to signal alias clearance', () => {
      assert.deepEqual(prepareAliasPayload('   '), { alias: '' });
      assert.deepEqual(prepareAliasPayload(''), { alias: '' });
    });
  });

  describe('derivePoolsStatusSummary', () => {
    it('returns No Data when activeModelsCount is 0 without fabricating Nominal', () => {
      const summary = derivePoolsStatusSummary(0, 0, 0);
      assert.equal(summary.badgeText, 'No Data');
      assert.equal(summary.badgeClass, 'badge-neutral');
      assert.equal(summary.metricText, 'No Data');
      assert.equal(summary.metricClass, 'text-muted');
    });

    it('reports Near Limit / Exhausted when exhausted models exist', () => {
      const summary = derivePoolsStatusSummary(3, 2, 0);
      assert.equal(summary.badgeText, '2 Near Limit');
      assert.equal(summary.badgeClass, 'badge-danger');
      assert.equal(summary.metricText, '2 Exhausted');
      assert.equal(summary.metricClass, 'text-danger');
    });

    it('reports Unknown when unknown models exist and none exhausted', () => {
      const summary = derivePoolsStatusSummary(2, 0, 1);
      assert.equal(summary.badgeText, '1 Unknown');
      assert.equal(summary.badgeClass, 'badge-neutral');
      assert.equal(summary.metricText, 'Quota Unknown');
      assert.equal(summary.metricClass, 'text-muted');
    });

    it('reports Nominal / All Healthy when all active models are healthy', () => {
      const summary = derivePoolsStatusSummary(4, 0, 0);
      assert.equal(summary.badgeText, 'Nominal');
      assert.equal(summary.badgeClass, 'badge-healthy');
      assert.equal(summary.metricText, 'All Healthy');
      assert.equal(summary.metricClass, 'text-success');
    });
  });

  describe('formatCreditPool', () => {
    it('returns indeterminate when pool is null or credits are missing', () => {
      const formatted = formatCreditPool(null);
      assert.equal(formatted.hasData, false);
      assert.equal(formatted.isIndeterminate, true);
      assert.equal(formatted.ratioPercent, null);
    });

    it('formats normal credits with correct ratio', () => {
      const formatted = formatCreditPool({ availableCredits: 800, monthlyCredits: 1000, usedCredits: 200 });
      assert.equal(formatted.hasData, true);
      assert.equal(formatted.availableText, '800');
      assert.equal(formatted.totalText, '/ 1,000 total');
      assert.equal(formatted.ratioPercent, 80);
      assert.equal(formatted.isIndeterminate, false);
    });

    it('avoids fabricating 100% when monthly limit is missing or zero', () => {
      const formatted = formatCreditPool({ availableCredits: 500, monthlyCredits: null, usedCredits: null });
      assert.equal(formatted.hasData, true);
      assert.equal(formatted.availableText, '500');
      assert.equal(formatted.totalText, null);
      assert.equal(formatted.ratioPercent, null);
      assert.equal(formatted.isIndeterminate, true);
    });
  });
});
