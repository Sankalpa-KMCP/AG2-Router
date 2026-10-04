import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as ts from 'typescript';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Frontend Usage tests follow the repository's authored-frontend test architecture: TypeScript
 * helpers are compiled in-memory from frontend/src and exercised directly, so dashboard logic
 * is proven without a browser while remaining the authored source of truth.
 */

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// Compiled to dist/test at runtime; the authored frontend source stays at the repository root.
const frontendSrc = path.resolve(__dirname, '..', '..', 'frontend', 'src');

async function compileModule(
  relativePath: string,
  dependencies: Record<string, string> = {},
): Promise<Record<string, unknown>> {
  const filePath = path.join(frontendSrc, relativePath);
  const source = readFileSync(filePath, 'utf8');
  let js = ts.transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.ESNext,
      target: ts.ScriptTarget.ES2022,
    },
    fileName: filePath,
  }).outputText;
  for (const [specifier, moduleUrl] of Object.entries(dependencies)) {
    js = js.split(`'${specifier}'`).join(`'${moduleUrl}'`);
    js = js.split(`"${specifier}"`).join(`"${moduleUrl}"`);
  }
  const moduleUrl = 'data:text/javascript;base64,' + Buffer.from(js, 'utf8').toString('base64');
  return (await import(moduleUrl)) as Record<string, unknown>;
}

void test('usage scope queries map from account options', async () => {
  const usage = await compileModule('lib/utils/usage.ts');
  const toScopeQuery = usage.toScopeQuery as (option: unknown) => string;
  assert.equal(toScopeQuery({ kind: 'all', label: 'All Accounts' }), 'all');
  assert.equal(toScopeQuery({ kind: 'unattributed', label: 'Unattributed' }), 'unattributed');
  assert.equal(toScopeQuery({ kind: 'account', accountId: 'acc_x', label: 'X', isHistorical: false }), 'account:acc_x');
});

void test('token formatting stays exact in detail and compact on cards', async () => {
  const usage = await compileModule('lib/utils/usage.ts');
  const formatTokenCount = usage.formatTokenCount as (value: number | null | undefined) => string;
  const formatExact = usage.formatExactTokenCount as (value: number | null | undefined) => string;

  assert.equal(formatTokenCount(3872), '3,872');
  assert.equal(formatTokenCount(16_307), '16.3K');
  assert.match(formatTokenCount(2_000_000_000_000), /T$/);
  assert.equal(formatTokenCount(Number.NaN), '--');
  assert.equal(formatTokenCount(-5), '--');
  assert.equal(formatExact(16307), '16,307 tokens');
  assert.equal(formatExact(Number.NaN), 'Value unavailable');
});

void test('bucket projection never produces NaN and guards zero denominators', async () => {
  const usage = await compileModule('lib/utils/usage.ts');
  const projectBuckets = usage.projectBuckets as (buckets: unknown) => Array<Record<string, unknown>>;

  assert.deepEqual(projectBuckets(null), []);
  assert.deepEqual(projectBuckets([]), []);

  const projected = projectBuckets([
    { bucketStartUtc: '2026-10-04T00:00:00Z', calls: 3, conversationTokens: 0, inputTokens: 0, outputTokens: 0, cacheReadTokens: 0 },
    { bucketStartUtc: '2026-10-05T00:00:00Z', calls: 1, conversationTokens: 500, inputTokens: 400, outputTokens: 100, cacheReadTokens: 0 },
  ]);
  assert.equal(projected.length, 2);
  assert.equal(projected[0].relativeWidth, 2); // zero keeps the 2% minimum visual width
  assert.equal(projected[1].relativeWidth, 100);
  for (const row of projected) {
    assert.equal(Number.isFinite(row.relativeWidth), true);
  }
});

void test('model rows show unknown bucket and skip percentages on zero totals', async () => {
  const usage = await compileModule('lib/utils/usage.ts');
  const projectModelRows = usage.projectModelRows as (models: unknown) => Array<Record<string, unknown>>;

  const rows = projectModelRows([
    { modelKey: 'claude-sonnet-4-5', isUnknownModel: false, calls: 2, conversationTokens: 300, inputTokens: 0, outputTokens: 0, thinkingOutputTokens: 0, cacheReadTokens: 0 },
    { modelKey: null, isUnknownModel: true, calls: 1, conversationTokens: 100, inputTokens: 0, outputTokens: 0, thinkingOutputTokens: 0, cacheReadTokens: 0 },
  ]);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].percentOfTotal, 75);
  assert.equal(rows[1].label, 'Unknown model');
  assert.equal(rows[1].percentOfTotal, 25);

  const zeroTotal = projectModelRows([
    { modelKey: null, isUnknownModel: true, calls: 0, conversationTokens: 0, inputTokens: 0, outputTokens: 0, thinkingOutputTokens: 0, cacheReadTokens: 0 },
  ]);
  assert.equal(zeroTotal[0].percentOfTotal, null);
});

void test('panel state derives honest empty, unsupported, and error states', async () => {
  const usage = await compileModule('lib/utils/usage.ts');
  const derivePanelState = usage.derivePanelState as (input: Record<string, unknown>) => { kind: string; reason?: string; message?: string };

  const zeroTotals = { calls: 0, conversationTokens: 0, inputTokens: 0, outputTokens: 0, responseOutputTokens: 0, responseOutputCalls: 0, thinkingOutputTokens: 0, thinkingOutputCalls: 0, cacheReadTokens: 0, cacheReportedCalls: 0, cacheUnknownCalls: 0, outputMismatchCalls: 0 };

  assert.equal(derivePanelState({ summaryFetched: false, unsupported: false, errorMessage: null, collector: null, totals: null }).kind, 'loading');
  assert.equal(derivePanelState({ summaryFetched: false, unsupported: true, errorMessage: null, collector: null, totals: null }).kind, 'unsupported');
  assert.equal(derivePanelState({ summaryFetched: true, unsupported: false, errorMessage: 'boom', collector: null, totals: zeroTotals }).kind, 'error');
  assert.equal(
    derivePanelState({ summaryFetched: true, unsupported: false, errorMessage: null, collector: { integrityAvailable: false }, totals: zeroTotals }).kind,
    'empty');
  assert.equal(
    derivePanelState({ summaryFetched: true, unsupported: false, errorMessage: null, collector: null, totals: zeroTotals }).kind,
    'empty');
  assert.equal(
    derivePanelState({ summaryFetched: true, unsupported: false, errorMessage: null, collector: null, totals: { ...zeroTotals, calls: 5 } }).kind,
    'ready');
});

void test('usage API client rejects 501 as explicit unsupported capability', async () => {
  const typesUrl = 'data:text/javascript;base64,' +
    Buffer.from(ts.transpileModule(
      readFileSync(path.join(frontendSrc, 'lib/api/types.ts'), 'utf8'),
      { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } },
    ).outputText, 'utf8').toString('base64');
  const clientModule = await compileModule('lib/api/client.ts', { './types.js': typesUrl });
  const client = clientModule.api as unknown as {
    getUsageSummary: (scope?: string) => Promise<unknown>;
  };
  const originalFetch = globalThis.fetch;
  globalThis.fetch = (async () => new Response(JSON.stringify({ error: 'not supported' }), { status: 501 })) as typeof fetch;
  try {
    await assert.rejects(() => client.getUsageSummary('all'), (error: unknown) => {
      assert.equal((error as Error).name, 'UsageUnavailableError');
      return true;
    });
  } finally {
    globalThis.fetch = originalFetch;
  }
});
