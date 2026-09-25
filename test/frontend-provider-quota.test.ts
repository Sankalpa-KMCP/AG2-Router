import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import ts from 'typescript';

// Compile the authored frontend helper directly; the root TypeScript build excludes frontend/.
const source = readFileSync(resolve('frontend/src/lib/utils/providerQuota.ts'), 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 }
}).outputText;
const {
  summarizeModelFamilies,
  deriveOverallCapacity,
  formatResetTime,
  summarizeReset,
  summarizeProviderQuotas
} = await import(
  `data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`
);

const model = (
  canonicalKey: string,
  displayLabel: string,
  remainingFraction: number | null,
  resetTime: string | null = null,
  isExhausted = false
) => ({
  canonicalKey,
  displayLabel,
  modes: [],
  remainingFraction,
  resetTime,
  isExhausted
});

describe('Live Capacity model family grouping & overall capacity', () => {
  it('correctly identifies and groups the four verified model families and excludes GPT-OSS', () => {
    const quota = {
      timestamp: '2026-01-01T00:00:00Z',
      models: [],
      canonicalModels: [
        model('gemini-3.8-flash-high', 'Gemini 3.8 Flash High', 0.8),
        model('gemini-3.1-pro-high', 'Gemini 3.1 Pro High', 0.65),
        model('claude-opus-4-6-thinking', 'Claude Opus 4.6', 0.4),
        model('claude-sonnet-4-6', 'Claude Sonnet 4.6', 0.55),
        model('gpt-oss-120b', 'GPT-OSS 120B', 0.95)
      ]
    };
    const before = structuredClone(quota);
    const families = summarizeModelFamilies(quota);

    assert.equal(families.length, 4);
    assert.deepEqual(
      families.map((f: { displayName: string }) => f.displayName),
      ['Gemini 3.8 Flash', 'Gemini 3.1 Pro', 'Opus 4.6', 'Sonnet 4.6']
    );
    assert.deepEqual(
      families.map((f: { provider: string }) => f.provider),
      ['Gemini', 'Gemini', 'Claude', 'Claude']
    );

    // Detailed telemetry remains unmutated
    assert.deepEqual(quota, before, 'Model telemetry snapshot must not be mutated');
  });

  it('handles Gemini 3.8 Flash grouping and derives conservative minimum across variants', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('tier:gemini-3.8-flash:row:0', 'Gemini 3.8 Flash High', 0.85, '2026-01-01T03:00:00Z'),
        model('tier:gemini-3.8-flash:row:1', 'Gemini 3.8 Flash Low', 0.35, '2026-01-01T03:00:00Z')
      ]
    };
    const [flash] = summarizeModelFamilies(quota, 15);
    assert.equal(flash.displayName, 'Gemini 3.8 Flash');
    assert.equal(flash.modelCount, 2);
    assert.equal(flash.percent, 35, 'Must use conservative minimum across variants');
    assert.equal(flash.health, 'healthy');
  });

  it('handles Gemini 3.1 Pro grouping without conflating with Flash', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-3.1-pro-high', 'Gemini 3.1 Pro High', 0.12)
      ]
    };
    const [pro] = summarizeModelFamilies(quota, 15);
    assert.equal(pro.displayName, 'Gemini 3.1 Pro');
    assert.equal(pro.percent, 12);
    assert.equal(pro.health, 'low');
  });

  it('handles Sonnet 4.6 and Opus 4.6 as separate families', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('claude-sonnet-4-6', 'Sonnet 4.6', 0.7),
        model('claude-opus-4-6-thinking', 'Opus 4.6', 0.25)
      ]
    };
    const families = summarizeModelFamilies(quota, 15);
    assert.equal(families.length, 2);
    assert.equal(families[0].displayName, 'Opus 4.6');
    assert.equal(families[0].percent, 25);
    assert.equal(families[1].displayName, 'Sonnet 4.6');
    assert.equal(families[1].percent, 70);
  });

  it('derives overall capacity conservatively as the bottleneck floor', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-flash', 'Gemini 3.8 Flash', 0.8),
        model('gemini-pro', 'Gemini 3.1 Pro', 0.6),
        model('claude-opus', 'Opus 4.6', 0.2),
        model('claude-sonnet', 'Sonnet 4.6', 0.75)
      ]
    };
    const families = summarizeModelFamilies(quota, 15);
    const overall = deriveOverallCapacity(families, 15);

    assert.equal(overall.percent, 20, 'Overall capacity must be minimum observed fraction (bottleneck)');
    assert.equal(overall.bottleneckFamily, 'Opus 4.6');
    assert.equal(overall.health, 'healthy');
    assert.equal(overall.observedFamiliesCount, 4);
    assert.equal(overall.totalModelsObserved, 4);
  });

  it('propagates unknown quota without fabricating 100% or healthy state', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-flash', 'Gemini 3.8 Flash', 0.9),
        model('gemini-pro', 'Gemini 3.1 Pro', null),
        model('claude-sonnet', 'Sonnet 4.6', 0.85)
      ]
    };
    const families = summarizeModelFamilies(quota, 15);
    const overall = deriveOverallCapacity(families, 15);

    const pro = families.find((f: { key: string }) => f.key === 'gemini-pro');
    assert.equal(pro.percent, null);
    assert.equal(pro.health, 'unknown');
    assert.equal(pro.unknownCount, 1);

    assert.equal(overall.percent, null, 'Overall capacity must be null when an observation is unknown');
    assert.equal(overall.health, 'unknown');
    assert.equal(overall.hasUnknownQuota, true);
  });

  it('prioritizes exhausted evidence over unknown evidence', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-flash', 'Gemini 3.8 Flash', null),
        model('claude-sonnet', 'Sonnet 4.6', 0.7),
        model('claude-opus', 'Opus 4.6', null, null, true)
      ]
    };
    const families = summarizeModelFamilies(quota, 15);
    const overall = deriveOverallCapacity(families, 15);

    const opus = families.find((f: { key: string }) => f.key === 'claude-opus');
    assert.equal(opus.percent, 0);
    assert.equal(opus.health, 'exhausted');

    assert.equal(overall.percent, 0, 'Exhaustion must take precedence');
    assert.equal(overall.health, 'exhausted');
    assert.equal(overall.isExhausted, true);
  });

  it('preserves multiple reset windows without collapsing differing reset instants', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-flash-1', 'Gemini 3.8 Flash High', 0.8, '2026-01-01T02:00:00Z'),
        model('gemini-flash-2', 'Gemini 3.8 Flash Low', 0.5, '2026-01-01T05:00:00Z')
      ]
    };
    const [flash] = summarizeModelFamilies(quota);
    assert.equal(flash.resetSummary, 'Multiple reset windows');
  });

  it('formats relative reset times truthfully without fabricating window labels', () => {
    const base = new Date('2026-01-01T00:00:00Z');
    const future3h15 = new Date('2026-01-01T03:15:00Z').toISOString();
    const past = new Date('2025-12-31T23:50:00Z').toISOString();

    assert.equal(formatResetTime(future3h15, base), 'Resets in 3h 15m');
    assert.equal(formatResetTime(past, base), 'Reset due');
    assert.equal(formatResetTime(null, base), 'No reset scheduled');
    assert.equal(formatResetTime('', base), 'No reset scheduled');

    // With identical rows
    const rows = [
      { identity: 'flash', remainingFraction: 0.8, resetTime: future3h15, isExhausted: false },
      { identity: 'flash', remainingFraction: 0.7, resetTime: future3h15, isExhausted: false }
    ];
    assert.equal(summarizeReset(rows, base), 'Resets in 3h 15m');

    // Partial reset times
    const partialRows = [
      { identity: 'flash', remainingFraction: 0.8, resetTime: future3h15, isExhausted: false },
      { identity: 'flash', remainingFraction: 0.7, resetTime: null, isExhausted: false }
    ];
    assert.equal(summarizeReset(partialRows, base), 'Reset windows vary or unknown');
  });

  it('leaves unmatched models unassigned rather than fabricating families', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('custom-internal-model', 'Custom Model 99B', 0.9),
        model('gpt-oss-120b', 'GPT-OSS 120B', 0.8)
      ]
    };
    const families = summarizeModelFamilies(quota);
    assert.equal(families.length, 0, 'Must not create cards for unrecognized models');
  });

  it('does not create empty cards for families that have no observed models', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-3.8-flash', 'Gemini 3.8 Flash', 0.8),
        model('claude-sonnet-4-6', 'Sonnet 4.6', 0.6)
      ]
    };
    const families = summarizeModelFamilies(quota);
    assert.equal(families.length, 2, 'Only observed families must be returned');
    assert.deepEqual(
      families.map((f: { displayName: string }) => f.displayName),
      ['Gemini 3.8 Flash', 'Sonnet 4.6']
    );
  });

  it('rejects unverified, prior, or future model versions from model family cards', () => {
    const unverifiedModels = [
      model('gemini-2.5-flash', 'Gemini 2.5 Flash', 0.9),
      model('gemini-2.5-pro', 'Gemini 2.5 Pro', 0.9),
      model('gemini-3.6-flash', 'Gemini 3.6 Flash', 0.9),
      model('gemini-3.7-flash', 'Gemini 3.7 Flash', 0.9),
      model('gemini-3.9-flash', 'Gemini 3.9 Flash', 0.9),
      model('gemini-4.0-flash', 'Gemini 4.0 Flash', 0.9),
      model('gemini-3.80-flash', 'Gemini 3.80 Flash', 0.9),
      model('gemini-13.8-flash', 'Gemini 13.8 Flash', 0.9),
      model('gemini-4.0-pro', 'Gemini 4.0 Pro', 0.9),
      model('claude-opus-4-5', 'Claude Opus 4.5', 0.9),
      model('claude-opus-4-7', 'Claude Opus 4.7', 0.9),
      model('claude-sonnet-4-5', 'Claude Sonnet 4.5', 0.9),
      model('claude-sonnet-4-7', 'Claude Sonnet 4.7', 0.9),
      model('claude-3-5-sonnet', 'Claude 3.5 Sonnet', 0.9),
      model('claude-3-7-sonnet', 'Claude 3.7 Sonnet', 0.9),
      model('claude-3-opus', 'Claude 3 Opus', 0.9)
    ];

    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: unverifiedModels
    };
    const families = summarizeModelFamilies(quota);
    assert.equal(families.length, 0, 'No unverified or mismatched versions may be matched into family cards');
  });

  it('rejects versionless generic names from model family cards', () => {
    const genericModels = [
      model('gemini-flash', 'Gemini Flash', 0.8),
      model('gemini-pro', 'Gemini Pro', 0.8),
      model('claude-opus', 'Claude Opus', 0.8),
      model('claude-sonnet', 'Claude Sonnet', 0.8),
      model('opus', 'Opus', 0.8),
      model('sonnet', 'Sonnet', 0.8)
    ];

    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: genericModels
    };
    const families = summarizeModelFamilies(quota);
    assert.equal(families.length, 0, 'Generic versionless names must not be classified into verified family cards');
  });

  it('prevents an unverified or mismatched model from contaminating verified family capacity or overall bottleneck', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-3.8-flash-high', 'Gemini 3.8 Flash High', 0.8),
        // Exhausted unverified/prior versions that must NOT drag down Gemini 3.8 Flash
        model('gemini-3.6-flash', 'Gemini 3.6 Flash', 0.0, null, true),
        model('gemini-3.7-flash', 'Gemini 3.7 Flash', 0.0, null, true),
        model('gemini-2.5-flash', 'Gemini 2.5 Flash', 0.0, null, true),
        // An exhausted unverified Claude model that must NOT drag down Sonnet 4.6
        model('claude-sonnet-4-5', 'Claude Sonnet 4.5', 0.0, null, true),
        model('claude-sonnet-4-6', 'Claude Sonnet 4.6', 0.75)
      ]
    };

    const families = summarizeModelFamilies(quota, 15);
    assert.equal(families.length, 2);

    const flash = families.find((f: { key: string }) => f.key === 'gemini-flash');
    assert.ok(flash, 'Gemini 3.8 Flash family must be present');
    assert.equal(flash.displayName, 'Gemini 3.8 Flash');
    assert.equal(flash.percent, 80, 'Must not be contaminated by Gemini 3.6, 3.7, or 2.5 Flash');
    assert.equal(flash.isExhausted, false);
    assert.equal(flash.health, 'healthy');

    const sonnet = families.find((f: { key: string }) => f.key === 'claude-sonnet');
    assert.ok(sonnet, 'Sonnet 4.6 family must be present');
    assert.equal(sonnet.percent, 75, 'Must not be contaminated by Sonnet 4.5');
    assert.equal(sonnet.isExhausted, false);

    const overall = deriveOverallCapacity(families, 15);
    assert.equal(overall.percent, 75, 'Bottleneck must be Sonnet 4.6 (75%), not contaminated by 0% unverified models');
    assert.equal(overall.isExhausted, false);
    assert.equal(overall.bottleneckFamily, 'Sonnet 4.6');
  });

  it('matches verified models across separator variations (. - _)', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-3.8-flash', 'Gemini 3.8 Flash', 0.8),
        model('gemini-3_1-pro', 'Gemini 3-1 Pro', 0.7),
        model('claude-opus-4-6-thinking', 'Claude Opus 4.6', 0.6),
        model('claude-sonnet-4_6', 'Claude Sonnet 4_6', 0.5)
      ]
    };
    const families = summarizeModelFamilies(quota);
    assert.equal(families.length, 4);
    assert.deepEqual(
      families.map((f: { displayName: string }) => f.displayName),
      ['Gemini 3.8 Flash', 'Gemini 3.1 Pro', 'Opus 4.6', 'Sonnet 4.6']
    );
  });

  it('matches Gemini 3.8 Flash across separator variations (3.8, 3-8, 3_8)', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-3.8-flash', 'Gemini 3.8 Flash', 0.85),
        model('gemini-3-8-flash', 'Gemini 3-8 Flash', 0.65),
        model('gemini-3_8-flash', 'Gemini 3_8 Flash', 0.70)
      ]
    };
    const families = summarizeModelFamilies(quota);
    assert.equal(families.length, 1);
    assert.equal(families[0].displayName, 'Gemini 3.8 Flash');
    assert.equal(families[0].modelCount, 3);
    assert.equal(families[0].percent, 65, 'Must derive conservative minimum across separator variants');
  });
});

describe('backward compatibility provider summaries', () => {
  it('still provides Gemini and Claude summaries when requested', () => {
    const quota = {
      timestamp: '',
      models: [],
      canonicalModels: [
        model('gemini-3.8-flash', 'Gemini 3.8 Flash', 0.75),
        model('claude-sonnet-4-6', 'Claude Sonnet 4.6', 0.5)
      ]
    };
    const providers = summarizeProviderQuotas(quota);
    assert.equal(providers.length, 2);
    assert.equal(providers[0].provider, 'Gemini');
    assert.equal(providers[0].percent, 75);
    assert.equal(providers[1].provider, 'Claude');
    assert.equal(providers[1].percent, 50);
  });
});
