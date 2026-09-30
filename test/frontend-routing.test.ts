import { describe, it, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { createServer, type ViteDevServer } from 'vite';
import { ApiClient } from '../frontend/src/lib/api/client.js';
import { workloadModelKey, observedWorkloadModels, candidateEvidenceCopy } from '../frontend/src/lib/utils/routing.js';
import type { RouterConfigUpdate, CandidateQuotaStatusDto } from '../frontend/src/lib/api/types.js';

describe('routing configuration contract', () => {
  it('uses backend key syntax and canonicalization, preserving explicit unset', () => {
    assert.equal(workloadModelKey(' Model/Exact-1:@_ '), 'model/exact-1:@_');
    assert.equal(workloadModelKey(''), null);
    for (const key of ['display label', '☃', 'x'.repeat(129), 'x\nkey']) assert.throws(() => workloadModelKey(key));
  });
  it('offers only observed raw model keys, never display/group keys', () => {
    assert.deepEqual(observedWorkloadModels({ timestamp: '', models: [
      { modelOrTier: 'Model/B', label: 'Friendly label', remainingFraction: null, isExhausted: false },
      { modelOrTier: 'model/b', label: 'Other pool', remainingFraction: .9, isExhausted: false },
      { modelOrTier: null, label: 'Fake key', remainingFraction: .9, isExhausted: false }
    ], canonicalModels: [{ canonicalKey: 'display-group', displayLabel: 'group', modes: [], remainingFraction: 1, isExhausted: false }] }), ['model/b']);
    assert.deepEqual(observedWorkloadModels(null), []);
  });
  it('client load/save round-trip carries workload key through unrelated changes', async () => {
    const originalFetch = globalThis.fetch;
    let saved: RouterConfigUpdate = { autoSwitchEnabled: true, workloadModelKey: 'model/exact', lowQuotaThresholdPercent: 15, minimumCandidateQuotaPercent: 30, pollingIntervalMs: 10000 };
    const bodies: RouterConfigUpdate[] = [];
    globalThis.fetch = async (_url, init) => {
      if (init?.body) { saved = JSON.parse(String(init.body)); bodies.push(saved); }
      return new Response(JSON.stringify({ success: true, config: saved }), { status: 200 });
    };
    try {
      const client = new ApiClient();
      const loaded = (await client.getConfig()).config;
      await client.saveConfig({ ...loaded, workloadModelKey: loaded.workloadModelKey ?? null, lowQuotaThresholdPercent: 20 });
      assert.equal((await client.getConfig()).config.workloadModelKey, 'model/exact');
      assert.equal(bodies[0].workloadModelKey, 'model/exact');
      await client.saveConfig({ ...saved, workloadModelKey: null });
      assert.equal((await client.getConfig()).config.workloadModelKey, null);
    } finally { globalThis.fetch = originalFetch; }
  });
});

let vite: ViteDevServer;
let component: any;
let render: (component: any, options: any) => { body: string };
before(async () => {
  vite = await createServer({ configFile: resolve('frontend/vite.config.ts'), server: { middlewareMode: true, proxy: {} }, appType: 'custom', logLevel: 'silent' });
  component = (await vite.ssrLoadModule('/src/lib/components/RoutingConfigSection.svelte')).default;
  render = (await vite.ssrLoadModule('svelte/server')).render;
});
after(async () => { await vite?.close(); });
const config: RouterConfigUpdate = { autoSwitchEnabled: true, workloadModelKey: 'configured-unobserved', lowQuotaThresholdPercent: 15, minimumCandidateQuotaPercent: 30, pollingIntervalMs: 1500 };

describe('authored routing component', () => {
  it('renders all settings and retains configured keys not in telemetry', () => {
    const { body } = render(component, { props: { config, autoSwitchEnabled: true, onSaveConfig: async () => {} } });
    for (const id of ['cfg-auto-switch', 'cfg-workload-model', 'cfg-low-threshold', 'cfg-min-candidate', 'cfg-polling-interval']) assert.ok(body.includes(id));
    assert.match(body, /configured-unobserved/);
    assert.match(body, /No model keys are available/);
    assert.match(body, /value="1.5"/);
  });
  it('warns visibly for enabled auto switching with no model', () => {
    const { body } = render(component, { props: { config: { ...config, workloadModelKey: null }, autoSwitchEnabled: true, onSaveConfig: async () => {} } });
    assert.match(body, /Auto Switch cannot operate until a workload model is configured/);
  });
  it('does not display stale or unknown percentages even if a payload contains one', () => {
    const { body } = render(component, { props: { config, autoSwitchEnabled: true, onSaveConfig: async () => {}, candidateEvidence: { modelKey: 'model/exact', minimumCandidateQuotaPercent: 30, available: true, candidates: ['STALE', 'UNKNOWN'].map(state => ({ accountId: state, state, remainingFraction: 1, observedAtUtc: null, ageSeconds: null })) } } });
    assert.doesNotMatch(body, /% observed remaining|100%/);
  });
  for (const state of ['NOT_OBSERVED', 'UNKNOWN', 'STALE', 'EXHAUSTED', 'BELOW_MINIMUM', 'USABLE', 'INVALID']) {
    it(`renders ${state} evidence without fabricated capacity`, () => {
      const row: CandidateQuotaStatusDto = { accountId: 'synthetic', state, remainingFraction: ['USABLE', 'BELOW_MINIMUM', 'EXHAUSTED'].includes(state) ? 0 : null, observedAtUtc: null, ageSeconds: null };
      const { body } = render(component, { props: { config, autoSwitchEnabled: true, onSaveConfig: async () => {}, candidateEvidence: { modelKey: 'model/exact', minimumCandidateQuotaPercent: 30, available: true, candidates: [row] } } });
      assert.ok(body.includes(candidateEvidenceCopy(row)));
      assert.doesNotMatch(body, /100%/);
      if (row.remainingFraction === null) assert.doesNotMatch(body, /% observed remaining/);
    });
  }
});
