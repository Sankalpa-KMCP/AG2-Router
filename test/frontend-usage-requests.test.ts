import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as ts from 'typescript';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * F-03 regression: the Usage request controller's ownership rules. Controlled promises and
 * deferred outcomes prove stale requests cannot mutate state — no timing sleeps.
 */

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const frontendSrc = path.resolve(__dirname, '..', '..', 'frontend', 'src');

function compileModule(relativePath: string): Promise<Record<string, unknown>> {
  const filePath = path.join(frontendSrc, relativePath);
  const source = readFileSync(filePath, 'utf8');
  const js = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    fileName: filePath,
  }).outputText;
  const moduleUrl = 'data:text/javascript;base64,' + Buffer.from(js, 'utf8').toString('base64');
  return import(moduleUrl);
}

interface RecordedState {
  begin: number;
  successes: number;
  unsupported: number;
  errors: string[];
  finalized: number;
}

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (error: Error) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const successOutcome = (calls: number) => ({
  kind: 'success' as const,
  summary: { calls, conversationTokens: calls, inputTokens: 0, outputTokens: 0, responseOutputTokens: 0, responseOutputCalls: 0, thinkingOutputTokens: 0, thinkingOutputCalls: 0, cacheReadTokens: 0, cacheReportedCalls: 0, cacheUnknownCalls: 0, outputMismatchCalls: 0, generatedAtUtc: '', scope: { kind: 'all', accountId: null }, historicalUnknown: {}, accounts: [] },
  collector: null,
  buckets: [],
  models: [],
});

async function makeController() {
  const usageRequests = await compileModule('lib/utils/usageRequests.ts');
  const create = usageRequests.createUsageRequestController as (callbacks: unknown) => {
    run: (fetchOutcome: () => Promise<unknown>) => Promise<void>;
    invalidate: () => void;
  };
  const state: RecordedState = { begin: 0, successes: 0, unsupported: 0, errors: [], finalized: 0 };
  const controller = create({
    beginRequest: () => { state.begin++; },
    applySuccess: () => { state.successes++; },
    applyUnsupported: () => { state.unsupported++; },
    applyError: (message: string) => { state.errors.push(message); },
    finalizeRequest: () => { state.finalized++; },
  });
  return { controller, state };
}

void test('UI1: stale success cannot overwrite newer success', async () => {
  const { controller, state } = await makeController();
  const outcomeA = deferred<unknown>();
  const outcomeB = deferred<unknown>();

  const requestA = controller.run(() => outcomeA.promise);
  const requestB = controller.run(() => outcomeB.promise);

  outcomeB.resolve(successOutcome(2));
  await requestB;
  outcomeA.resolve(successOutcome(1));
  await requestA;

  assert.equal(state.successes, 1, 'Only the latest request may apply success.');
  assert.equal(state.begin, 2);
  assert.equal(state.finalized, 1, 'Only the latest request finalizes loading.');
});

void test('UI2: stale failure cannot replace newer success with an error', async () => {
  const { controller, state } = await makeController();
  const outcomeA = deferred<unknown>();
  const outcomeB = deferred<unknown>();

  const requestA = controller.run(() => outcomeA.promise);
  const requestB = controller.run(() => outcomeB.promise);

  outcomeB.resolve(successOutcome(2));
  await requestB;
  outcomeA.reject(new Error('stale failure'));
  await requestA;

  assert.equal(state.successes, 1);
  assert.deepEqual(state.errors, [], 'A stale failure must not surface.');
  assert.equal(state.finalized, 1);
});

void test('UI3: stale success cannot clear newer unsupported state', async () => {
  const { controller, state } = await makeController();
  const outcomeA = deferred<unknown>();
  const outcomeB = deferred<unknown>();

  const requestA = controller.run(() => outcomeA.promise);
  const requestB = controller.run(() => outcomeB.promise);

  outcomeB.resolve({ kind: 'unsupported' });
  await requestB;
  outcomeA.resolve(successOutcome(1));
  await requestA;

  assert.equal(state.unsupported, 1, 'Unsupported state from the latest request persists.');
  assert.equal(state.successes, 0, 'Stale success must not clear unsupported.');
  assert.equal(state.finalized, 1);
});

void test('UI4: failure then retry succeeds normally', async () => {
  const { controller, state } = await makeController();
  const failure = deferred<unknown>();

  const first = controller.run(() => failure.promise);
  failure.reject(new Error('transient'));
  await first;

  const second = controller.run(async () => successOutcome(3));
  await second;

  assert.equal(state.errors[0], 'transient');
  assert.equal(state.successes, 1);
  assert.equal(state.finalized, 2);
});

void test('UI5/UI6: range and account changes follow the same latest-wins rule', async () => {
  const { controller, state } = await makeController();
  const rangeA = deferred<unknown>();
  const rangeB = deferred<unknown>();
  const accountA = deferred<unknown>();
  const accountB = deferred<unknown>();

  const rangeRequestA = controller.run(() => rangeA.promise);
  const rangeRequestB = controller.run(() => rangeB.promise);
  rangeB.resolve(successOutcome(10));
  await rangeRequestB;
  rangeA.resolve(successOutcome(11));
  await rangeRequestA;
  assert.equal(state.successes, 1, 'Range race: stale outcome dropped.');

  const accountRequestA = controller.run(() => accountA.promise);
  const accountRequestB = controller.run(() => accountB.promise);
  accountB.resolve(successOutcome(20));
  await accountRequestB;
  accountA.reject(new Error('stale account error'));
  await accountRequestA;
  assert.equal(state.errors.length, 0, 'Account race: stale failure dropped.');
  assert.equal(state.successes, 2);
});

void test('UI7: exactly one loading finalization for the latest request', async () => {
  const { controller, state } = await makeController();
  const outcomeA = deferred<unknown>();
  const outcomeB = deferred<unknown>();
  const outcomeC = deferred<unknown>();

  const requestA = controller.run(() => outcomeA.promise);
  const requestB = controller.run(() => outcomeB.promise);
  const requestC = controller.run(() => outcomeC.promise);

  outcomeC.resolve(successOutcome(3));
  await requestC;
  outcomeB.resolve(successOutcome(2));
  await requestB;
  outcomeA.resolve(successOutcome(1));
  await requestA;

  assert.equal(state.begin, 3);
  assert.equal(state.finalized, 1, 'Only the latest request may finalize loading.');
});

void test('UI8: invalidated requests (teardown) cannot mutate state', async () => {
  const { controller, state } = await makeController();
  const outcomeA = deferred<unknown>();

  const requestA = controller.run(() => outcomeA.promise);
  controller.invalidate(); // component teardown

  outcomeA.resolve(successOutcome(1));
  await requestA;

  assert.equal(state.begin, 1);
  assert.equal(state.successes, 0, 'Invalidated request must not apply data.');
  assert.equal(state.finalized, 0, 'Invalidated request must not finalize loading.');
});
