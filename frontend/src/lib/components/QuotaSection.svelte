<script lang="ts">
  import ProgressRing from './ProgressRing.svelte';
  import { formatCreditPool, formatQuotaFraction, formatResetTime } from '../utils/helpers.js';
  import type { QuotaSnapshotDto } from '../api/types.js';

  interface Props {
    quota: QuotaSnapshotDto | null;
    isAg2Connected: boolean;
    lowThresholdPercent?: number;
  }

  let {
    quota = null,
    isAg2Connected = false,
    lowThresholdPercent = 15
  }: Props = $props();

  const canonicalModels = $derived(quota?.canonicalModels ?? null);
  const rawModels = $derived(quota?.models ?? []);
  const promptCredits = $derived(quota?.promptCredits ?? null);
  const flowCredits = $derived(quota?.flowCredits ?? null);
  const hasCanonical = $derived(Boolean(canonicalModels && canonicalModels.length > 0));
</script>

<div class="card quota-card">
  <div class="quota-header">
    <div>
      <h2 class="quota-title">Quota Overview</h2>
      <p class="quota-subtitle">
        {hasCanonical
          ? 'Canonical model pools • Reasoning variants deduplicated'
          : 'Live model quota pools'}
      </p>
    </div>
    {#if quota?.timestamp}
      <span class="quota-sync-time" title="Telemetry snapshot timestamp">
        Updated: {new Date(quota.timestamp).toLocaleTimeString()}
      </span>
    {/if}
  </div>

  {#if !isAg2Connected || !quota}
    <div class="empty-state">
      <div class="empty-icon" aria-hidden="true">📡</div>
      <h3 class="empty-title">Telemetry Offline</h3>
      <p class="empty-desc">
        Antigravity 2 language server is not detected or offline. Start Antigravity to stream live quota capacity and credit pools.
      </p>
    </div>
  {:else}
    <!-- Model Quotas Grid with SVG Progress Rings -->
    <div class="models-grid" aria-label="Model Quotas">
      {#if hasCanonical && canonicalModels}
        {#each canonicalModels as model (model.canonicalKey)}
          {@const pct = formatQuotaFraction(model.remainingFraction)}
          {@const isUnknown = pct === null && !model.isExhausted}
          {@const isLow = pct !== null && pct <= lowThresholdPercent}
          <div class="model-pool-card {model.isExhausted ? 'exhausted' : isUnknown ? 'unknown' : isLow ? 'warning' : 'healthy'}">
            <div class="model-pool-top">
              <div class="model-info">
                <h3 class="model-label">{model.displayLabel}</h3>
                <div class="model-modes">
                  {#each model.modes as mode}
                    <span class="mode-pill">{mode}</span>
                  {/each}
                </div>
              </div>
              {#if pct !== null}
                <ProgressRing percent={pct} size={58} strokeWidth={6}
                  isExhausted={model.isExhausted} label={model.displayLabel} />
              {:else}
                <span class="unknown-quota" aria-label="Quota unknown">--%</span>
              {/if}
            </div>

            <div class="model-pool-bottom">
              <div class="reset-info">
                <span class="reset-icon" aria-hidden="true">⏱</span>
                <span class="reset-text">{formatResetTime(model.resetTime)}</span>
              </div>
              <span class="badge {model.isExhausted ? 'badge-danger' : isUnknown ? 'badge-neutral' : isLow ? 'badge-warning' : 'badge-healthy'}">
                {model.isExhausted ? 'EXHAUSTED' : isUnknown ? 'UNKNOWN' : isLow ? 'LOW QUOTA' : 'HEALTHY'}
              </span>
            </div>
          </div>
        {/each}
      {:else if rawModels.length > 0}
        <!-- Graceful fallback to raw models if canonical grouping is absent -->
        {#each rawModels as model, idx (model.modelOrTier + idx)}
          {@const pct = formatQuotaFraction(model.remainingFraction)}
          {@const isUnknown = pct === null && !model.isExhausted}
          {@const isLow = pct !== null && pct <= lowThresholdPercent}
          <div class="model-pool-card {model.isExhausted ? 'exhausted' : isUnknown ? 'unknown' : isLow ? 'warning' : 'healthy'}">
            <div class="model-pool-top">
              <div class="model-info">
                <h3 class="model-label">{model.label}</h3>
                <span class="mode-pill">Direct Pool</span>
              </div>
              {#if pct !== null}
                <ProgressRing percent={pct} size={58} strokeWidth={6}
                  isExhausted={model.isExhausted} label={model.label} />
              {:else}
                <span class="unknown-quota" aria-label="Quota unknown">--%</span>
              {/if}
            </div>

            <div class="model-pool-bottom">
              <div class="reset-info">
                <span class="reset-icon" aria-hidden="true">⏱</span>
                <span class="reset-text">{formatResetTime(model.resetTime)}</span>
              </div>
              <span class="badge {model.isExhausted ? 'badge-danger' : isUnknown ? 'badge-neutral' : isLow ? 'badge-warning' : 'badge-healthy'}">
                {model.isExhausted ? 'EXHAUSTED' : isUnknown ? 'UNKNOWN' : isLow ? 'LOW QUOTA' : 'HEALTHY'}
              </span>
            </div>
          </div>
        {/each}
      {/if}
    </div>

    <!-- Segregated Credit Pools -->
    {#if promptCredits || flowCredits}
      {@const prompt = formatCreditPool(promptCredits)}
      {@const flow = formatCreditPool(flowCredits)}
      <div class="credits-section">
        <h3 class="credits-title">CREDIT POOLS (SEGREGATED)</h3>
        <div class="credits-grid">
          {#if prompt.hasData}
            <div class="credit-card">
              <div class="credit-header">
                <span class="credit-type">Prompt Credits</span>
                <span class="credit-ratio">
                  {prompt.ratioPercent !== null ? `${prompt.ratioPercent}% Available` : 'Available Pool'}
                </span>
              </div>
              <div class="credit-values">
                <strong>{prompt.availableText}</strong>
                {#if prompt.totalText}
                  <span class="credit-total">{prompt.totalText}</span>
                {:else}
                  <span class="credit-total">available</span>
                {/if}
              </div>
              <div class="credit-bar">
                <div
                  class="credit-fill"
                  class:is-indeterminate={prompt.isIndeterminate}
                  style="width: {prompt.ratioPercent !== null ? prompt.ratioPercent : 0}%;"
                ></div>
              </div>
            </div>
          {/if}

          {#if flow.hasData}
            <div class="credit-card">
              <div class="credit-header">
                <span class="credit-type">Flow Credits</span>
                <span class="credit-ratio">
                  {flow.ratioPercent !== null ? `${flow.ratioPercent}% Available` : 'Available Pool'}
                </span>
              </div>
              <div class="credit-values">
                <strong>{flow.availableText}</strong>
                {#if flow.totalText}
                  <span class="credit-total">{flow.totalText}</span>
                {:else}
                  <span class="credit-total">available</span>
                {/if}
              </div>
              <div class="credit-bar">
                <div
                  class="credit-fill flow-fill"
                  class:is-indeterminate={flow.isIndeterminate}
                  style="width: {flow.ratioPercent !== null ? flow.ratioPercent : 0}%;"
                ></div>
              </div>
            </div>
          {/if}
        </div>
      </div>
    {/if}
  {/if}
</div>

<style>
  .quota-card {
    padding: var(--space-5);
    margin-bottom: var(--space-5);
  }

  .quota-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: var(--space-4);
  }

  .quota-title {
    font-size: 15px;
    font-weight: 700;
    color: var(--color-text-primary);
  }

  .quota-subtitle {
    font-size: 12px;
    color: var(--color-text-muted);
    margin-top: 2px;
  }

  .quota-sync-time {
    font-size: 11.5px;
    color: var(--color-text-muted);
  }

  .models-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
    gap: var(--space-3-5);
    margin-bottom: var(--space-4);
  }

  .model-pool-card {
    background-color: var(--color-surface);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-md);
    padding: var(--space-4);
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    gap: var(--space-3);
    transition: border-color var(--transition-fast), box-shadow var(--transition-fast);
  }

  .model-pool-card:hover {
    border-color: var(--color-card-border-hover);
    box-shadow: var(--shadow-sm);
  }

  .model-pool-top {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: var(--space-2);
  }

  .model-info {
    display: flex;
    flex-direction: column;
    gap: var(--space-1-5);
  }

  .model-label {
    font-size: 14px;
    font-weight: 600;
    color: var(--color-text-primary);
    line-height: 1.2;
  }

  .model-modes {
    display: flex;
    flex-wrap: wrap;
    gap: var(--space-1);
  }

  .mode-pill {
    display: inline-block;
    padding: 1px 6px;
    background-color: var(--color-surface-subtle);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-full);
    font-size: 10.5px;
    font-weight: 500;
    color: var(--color-text-secondary);
  }

  .model-pool-bottom {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding-top: var(--space-2-5);
    border-top: 1px solid var(--color-divider);
  }

  .reset-info {
    display: flex;
    align-items: center;
    gap: var(--space-1);
    font-size: 11.5px;
    color: var(--color-text-muted);
  }

  .reset-icon {
    font-size: 12px;
  }

  .credits-section {
    padding-top: var(--space-4);
    border-top: 1px solid var(--color-divider);
  }

  .credits-title {
    font-size: 11px;
    font-weight: 700;
    color: var(--color-text-muted);
    letter-spacing: 0.05em;
    margin-bottom: var(--space-2-5);
  }

  .credits-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
    gap: var(--space-3);
  }

  .credit-card {
    background-color: var(--color-surface-subtle);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-sm);
    padding: var(--space-3);
  }

  .credit-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: var(--space-1);
  }

  .credit-type {
    font-size: 12px;
    font-weight: 600;
    color: var(--color-text-secondary);
  }

  .credit-ratio {
    font-size: 11.5px;
    color: var(--color-text-muted);
  }

  .credit-values {
    display: flex;
    align-items: baseline;
    gap: var(--space-1);
    margin-bottom: var(--space-2);
  }

  .credit-values strong {
    font-size: 16px;
    color: var(--color-text-primary);
  }

  .credit-total {
    font-size: 11.5px;
    color: var(--color-text-muted);
  }

  .credit-bar {
    width: 100%;
    height: 5px;
    background-color: var(--color-card-border);
    border-radius: var(--radius-full);
    overflow: hidden;
  }

  .credit-fill {
    height: 100%;
    background-color: var(--color-primary);
    border-radius: var(--radius-full);
  }

  .credit-fill.flow-fill {
    background-color: #8B5CF6;
  }

  .credit-fill.is-indeterminate {
    opacity: 0.3;
    background-color: var(--color-text-muted);
  }

  .empty-state {
    text-align: center;
    padding: var(--space-6) var(--space-4);
  }

  .empty-icon {
    font-size: 28px;
    margin-bottom: var(--space-2);
  }

  .empty-title {
    font-size: 14px;
    font-weight: 600;
    color: var(--color-text-primary);
    margin-bottom: var(--space-1);
  }

  .empty-desc {
    font-size: 12px;
    color: var(--color-text-muted);
    max-width: 440px;
    margin: 0 auto;
  }
</style>
