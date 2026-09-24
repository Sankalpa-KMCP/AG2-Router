import { describe, it, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { resolve } from 'node:path';
import { createServer, type ViteDevServer } from 'vite';

let vite: ViteDevServer;
let ProviderOverview: any;
let QuotaSection: any;
let renderComponent: (component: any, options: any) => { body: string };

const quota = {
  timestamp: '2026-01-01T00:00:00Z',
  promptCredits: { availableCredits: 100 },
  flowCredits: { availableCredits: 200 },
  models: [],
  canonicalModels: [
    { canonicalKey: 'gemini-flash', displayLabel: 'Gemini Flash', modes: ['High'], remainingFraction: .8, resetTime: '2026-01-02T00:00:00Z', isExhausted: false },
    { canonicalKey: 'gemini-pro', displayLabel: 'Gemini Pro', modes: [], remainingFraction: .3, resetTime: '2026-01-03T00:00:00Z', isExhausted: false },
    { canonicalKey: 'claude-sonnet', displayLabel: 'Claude Sonnet', modes: [], remainingFraction: null, resetTime: null, isExhausted: false },
    { canonicalKey: 'gpt-oss', displayLabel: 'GPT-OSS', modes: [], remainingFraction: .9, resetTime: null, isExhausted: false }
  ]
};

before(async () => {
  // Transform Svelte components in memory. No HTTP listener or API proxy is started.
  vite = await createServer({
    configFile: resolve('frontend/vite.config.ts'),
    server: { middlewareMode: true, proxy: {} },
    appType: 'custom',
    logLevel: 'silent'
  });
  ProviderOverview = (await vite.ssrLoadModule('/src/lib/components/ProviderOverview.svelte')).default;
  QuotaSection = (await vite.ssrLoadModule('/src/lib/components/QuotaSection.svelte')).default;
  renderComponent = (await vite.ssrLoadModule('svelte/server')).render;
});

after(async () => { if (vite) await vite.close(); });

describe('authored quota components', () => {
  it('renders exactly two provider summaries without model cards or credit pools', () => {
    const { body } = renderComponent(ProviderOverview, { props: { quota, isAg2Connected: true, lowThresholdPercent: 15 } });
    assert.equal((body.match(/<article\b/g) ?? []).length, 2);
    assert.match(body, /Gemini quota summary/);
    assert.match(body, /Claude quota summary/);
    assert.match(body, /30%/);
    assert.match(body, /Multiple reset windows/);
    assert.doesNotMatch(body, /GPT-OSS|Gemini Flash|Gemini Pro|Claude Sonnet/);
    assert.doesNotMatch(body, /CREDIT POOLS|Prompt Credits|Flow Credits/);
  });

  it('keeps individual model rows in the detailed view without credit presentation', () => {
    const { body } = renderComponent(QuotaSection, { props: { quota, isAg2Connected: true, lowThresholdPercent: 15 } });
    for (const label of ['Gemini Flash', 'Gemini Pro', 'Claude Sonnet', 'GPT-OSS']) {
      assert.match(body, new RegExp(label));
    }
    assert.doesNotMatch(body, /CREDIT POOLS|Prompt Credits|Flow Credits/);
  });

  it('renders unknown provider quota without a fabricated percentage', () => {
    const { body } = renderComponent(ProviderOverview, { props: { quota, isAg2Connected: false, lowThresholdPercent: 15 } });
    assert.equal((body.match(/<article\b/g) ?? []).length, 2);
    assert.match(body, /Gemini remaining quota unknown/);
    assert.match(body, /Claude remaining quota unknown/);
    assert.doesNotMatch(body, /100%/);
  });
});
