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
    { canonicalKey: 'gemini-flash', displayLabel: 'Gemini 3.6 Flash', modes: ['High'], remainingFraction: 0.8, resetTime: '2026-01-02T00:00:00Z', isExhausted: false },
    { canonicalKey: 'gemini-pro', displayLabel: 'Gemini 3.1 Pro', modes: [], remainingFraction: 0.3, resetTime: '2026-01-03T00:00:00Z', isExhausted: false },
    { canonicalKey: 'claude-sonnet-4-6', displayLabel: 'Claude Sonnet 4.6', modes: [], remainingFraction: null, resetTime: null, isExhausted: false },
    { canonicalKey: 'gpt-oss', displayLabel: 'GPT-OSS', modes: [], remainingFraction: 0.9, resetTime: null, isExhausted: false }
  ]
};

const completeQuota = {
  timestamp: '2026-01-01T00:00:00Z',
  models: [],
  canonicalModels: [
    { canonicalKey: 'gemini-flash', displayLabel: 'Gemini 3.6 Flash', modes: [], remainingFraction: 0.85, resetTime: '2026-01-02T00:00:00Z', isExhausted: false },
    { canonicalKey: 'gemini-pro', displayLabel: 'Gemini 3.1 Pro', modes: [], remainingFraction: 0.6, resetTime: '2026-01-02T00:00:00Z', isExhausted: false },
    { canonicalKey: 'claude-opus', displayLabel: 'Opus 4.6', modes: [], remainingFraction: 0.25, resetTime: '2026-01-02T00:00:00Z', isExhausted: false },
    { canonicalKey: 'claude-sonnet', displayLabel: 'Sonnet 4.6', modes: [], remainingFraction: 0.7, resetTime: '2026-01-02T00:00:00Z', isExhausted: false },
    { canonicalKey: 'gpt-oss', displayLabel: 'GPT-OSS 120B', modes: [], remainingFraction: 0.95, resetTime: null, isExhausted: false }
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
  it('renders overall available capacity summary and compact model family cards', () => {
    const { body } = renderComponent(ProviderOverview, {
      props: {
        quota,
        isAg2Connected: true,
        lowThresholdPercent: 15,
        currentAccount: { email: 'dev@example.com' }
      }
    });

    // Overall summary card
    assert.match(body, /Available capacity/);
    assert.match(body, /dev@example\.com/);
    assert.match(body, /3 model families observed/);

    // Observed family cards (Gemini 3.6 Flash, Gemini 3.1 Pro, Sonnet 4.6)
    assert.equal((body.match(/<article\b/g) ?? []).length, 3);
    assert.match(body, /Gemini 3\.6 Flash/);
    assert.match(body, /Gemini 3\.1 Pro/);
    assert.match(body, /Sonnet 4\.6/);

    // GPT-OSS must NOT appear in overview family cards
    const familyCards = body.split('class="family-grid"')[1]?.split('class="section-footnote"')[0] ?? '';
    assert.doesNotMatch(familyCards, /GPT-OSS/);

    // No credit pool clutter
    assert.doesNotMatch(body, /CREDIT POOLS|Prompt Credits|Flow Credits/);
  });

  it('renders four model family cards and prominent bottleneck percentage when all four exist', () => {
    const { body } = renderComponent(ProviderOverview, {
      props: {
        quota: completeQuota,
        isAg2Connected: true,
        lowThresholdPercent: 15,
        currentAccount: { email: 'pilot@example.com' }
      }
    });

    // All 4 verified model families rendered
    assert.equal((body.match(/<article\b/g) ?? []).length, 4);
    assert.match(body, /Gemini 3\.6 Flash/);
    assert.match(body, /Gemini 3\.1 Pro/);
    assert.match(body, /Opus 4\.6/);
    assert.match(body, /Sonnet 4\.6/);

    // Prominent bottleneck capacity (25% from Opus 4.6)
    assert.match(body, /25%/);
    assert.match(body, /Limited by Opus 4\.6/);
    assert.match(body, /4 model families observed/);
  });

  it('keeps individual model rows in the detailed view without credit presentation', () => {
    const { body } = renderComponent(QuotaSection, { props: { quota, isAg2Connected: true, lowThresholdPercent: 15 } });
    for (const label of ['Gemini 3.6 Flash', 'Gemini 3.1 Pro', 'Claude Sonnet', 'GPT-OSS']) {
      assert.match(body, new RegExp(label));
    }
    assert.doesNotMatch(body, /CREDIT POOLS|Prompt Credits|Flow Credits/);
  });

  it('renders unknown capacity without a fabricated percentage when offline', () => {
    const { body } = renderComponent(ProviderOverview, { props: { quota, isAg2Connected: false, lowThresholdPercent: 15 } });
    assert.match(body, /Waiting for telemetry|Antigravity telemetry disconnected/);
    assert.doesNotMatch(body, /\b100%\b/);
  });
});
