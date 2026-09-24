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
const { summarizeProviderQuotas } = await import(
  `data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`
);

const model = (canonicalKey: string, displayLabel: string, remainingFraction: number | null,
  resetTime: string | null = null, isExhausted = false) => ({
    canonicalKey, displayLabel, modes: [], remainingFraction, resetTime, isExhausted
  });

describe('authored provider overview summaries', () => {
  it('always returns exactly Gemini and Claude and excludes GPT-OSS', () => {
    const quota = { timestamp: '2026-01-01T00:00:00Z', models: [], canonicalModels: [
      model('gemini-3-flash', 'Gemini 3 Flash', .8),
      model('claude-sonnet', 'Claude Sonnet', .6),
      model('gpt-oss-120b', 'GPT-OSS 120B', .95)
    ] };
    const before = structuredClone(quota);
    const summaries = summarizeProviderQuotas(quota);
    assert.deepEqual(summaries.map((summary: { provider: string }) => summary.provider), ['Gemini', 'Claude']);
    assert.deepEqual(summaries.map((summary: { modelCount: number }) => summary.modelCount), [1, 1]);
    assert.deepEqual(quota, before, 'UI aggregation must not rewrite model telemetry');
  });

  it('uses the minimum known quota and retains distinct reset windows', () => {
    const [gemini] = summarizeProviderQuotas({ timestamp: '', models: [], canonicalModels: [
      model('gemini-flash', 'Gemini Flash', .82, '2026-01-01T01:00:00Z'),
      model('gemini-pro', 'Gemini Pro', .17, '2026-01-02T01:00:00Z')
    ] }, 15);
    assert.equal(gemini.percent, 17);
    assert.equal(gemini.health, 'healthy');
    assert.equal(gemini.resetSummary, 'Multiple reset windows');
  });

  it('never treats a partly unknown provider as healthy or full', () => {
    const [gemini, claude] = summarizeProviderQuotas({ timestamp: '', models: [], canonicalModels: [
      model('gemini-flash', 'Gemini Flash', .9),
      model('gemini-pro', 'Gemini Pro', null),
      model('claude-sonnet', 'Claude Sonnet', null)
    ] });
    assert.equal(gemini.percent, null);
    assert.equal(gemini.health, 'unknown');
    assert.equal(gemini.unknownCount, 1);
    assert.equal(claude.percent, null);
    assert.equal(claude.health, 'unknown');
  });

  it('prioritizes exhausted evidence and never invents a reset for partial data', () => {
    const [gemini] = summarizeProviderQuotas({ timestamp: '', models: [], canonicalModels: [
      model('gemini-flash', 'Gemini Flash', null, null, true),
      model('gemini-pro', 'Gemini Pro', .7, '2026-01-01T01:00:00Z')
    ] });
    assert.equal(gemini.percent, 0);
    assert.equal(gemini.health, 'exhausted');
    assert.equal(gemini.resetSummary, 'Reset windows vary or unknown');
  });

  it('falls back to raw model rows without collapsing them', () => {
    const summaries = summarizeProviderQuotas({ timestamp: '', models: [
      { modelOrTier: 'gemini-flash', label: 'Gemini Flash', remainingFraction: .6, isExhausted: false },
      { modelOrTier: 'gemini-pro', label: 'Gemini Pro', remainingFraction: .2, isExhausted: false },
      { modelOrTier: 'claude-sonnet', label: 'Claude Sonnet', remainingFraction: .4, isExhausted: false }
    ] });
    assert.equal(summaries[0].percent, 20);
    assert.equal(summaries[0].modelCount, 2);
    assert.equal(summaries[1].percent, 40);
  });
});
