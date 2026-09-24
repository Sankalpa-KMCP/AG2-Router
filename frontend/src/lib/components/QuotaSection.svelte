<script lang="ts">
  import { formatQuotaFraction, formatResetTime } from '../utils/helpers.js';
  import type { QuotaSnapshotDto } from '../api/types.js';

  interface Props {
    quota: QuotaSnapshotDto | null;
    isAg2Connected: boolean;
    lowThresholdPercent?: number;
  }

  let { quota = null, isAg2Connected = false, lowThresholdPercent = 15 }: Props = $props();
  const rows = $derived.by(() => {
    if (quota?.canonicalModels?.length) {
      return quota.canonicalModels.map((model, index) => ({
        key: `${model.canonicalKey}-${index}`,
        label: model.displayLabel,
        modes: model.modes,
        percent: formatQuotaFraction(model.remainingFraction),
        resetTime: model.resetTime,
        isExhausted: model.isExhausted
      }));
    }
    return (quota?.models ?? []).map((model, index) => ({
      key: `${model.modelOrTier}-${index}`,
      label: model.label,
      modes: [] as string[],
      percent: formatQuotaFraction(model.remainingFraction),
      resetTime: model.resetTime,
      isExhausted: model.isExhausted
    }));
  });
</script>

<section class="card quota-detail" aria-labelledby="detail-heading">
  <div class="detail-heading">
    <div>
      <span class="eyebrow">Telemetry &amp; quotas</span>
      <h2 id="detail-heading">Model capacity</h2>
      <p>Individual model observations and reset windows remain separate.</p>
    </div>
    {#if quota?.timestamp}<span class="sync-time">Updated {new Date(quota.timestamp).toLocaleTimeString()}</span>{/if}
  </div>

  {#if !isAg2Connected || !quota}
    <div class="empty-state" role="status">
      <h3>Telemetry unavailable</h3>
      <p>Connect to Antigravity to see model capacity.</p>
    </div>
  {:else if rows.length === 0}
    <div class="empty-state" role="status">
      <h3>No model observations</h3>
      <p>Model quotas will appear here when telemetry becomes available.</p>
    </div>
  {:else}
    <div class="model-list" aria-label="Individual model quotas">
      {#each rows as model (model.key)}
        {@const isUnknown = model.percent === null && !model.isExhausted}
        {@const isLow = model.percent !== null && model.percent <= lowThresholdPercent}
        <div class="model-row">
          <div class="model-identity">
            <h3>{model.label}</h3>
            {#if model.modes.length}<span class="modes">{model.modes.join(' · ')}</span>{/if}
          </div>
          <span class="reset-text">{formatResetTime(model.resetTime)}</span>
          <div class="model-capacity">
            <strong class:unknown={isUnknown}>{isUnknown ? 'Unknown' : `${model.isExhausted ? 0 : model.percent}%`}</strong>
            <span class="mini-track" aria-hidden="true">
              {#if !isUnknown}<span class="mini-fill" class:low={isLow} class:exhausted={model.isExhausted} style={`width: ${model.isExhausted ? 0 : model.percent}%`}></span>{/if}
            </span>
          </div>
        </div>
      {/each}
    </div>
  {/if}
</section>

<style>
  .quota-detail { padding: var(--space-5); }
  .detail-heading { display: flex; justify-content: space-between; align-items: end; gap: var(--space-4); padding-bottom: var(--space-4); border-bottom: 1px solid var(--color-divider); }
  .eyebrow { display: block; margin-bottom: 2px; color: var(--color-primary-text); font-size: 11px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; }
  h2 { font-size: 19px; letter-spacing: -.035em; }
  .detail-heading p { margin-top: 4px; color: var(--color-text-muted); font-size: 12px; }
  .sync-time { color: var(--color-text-muted); font-size: 11px; white-space: nowrap; }
  .model-list { display: grid; }
  .model-row { display: grid; grid-template-columns: minmax(180px, 1fr) minmax(125px, .5fr) 120px; gap: var(--space-4); align-items: center; padding: 14px 2px; border-bottom: 1px solid var(--color-divider); }
  .model-row:last-child { border-bottom: 0; padding-bottom: 2px; }
  .model-identity { min-width: 0; }
  .model-identity h3 { font-size: 13px; font-weight: 650; line-height: 1.3; }
  .modes { display: block; margin-top: 3px; color: var(--color-text-muted); font-size: 11px; }
  .reset-text { color: var(--color-text-muted); font-size: 11px; }
  .model-capacity { text-align: right; }
  .model-capacity strong { display: block; font-size: 13px; font-variant-numeric: tabular-nums; }
  .model-capacity strong.unknown { color: var(--color-text-muted); font-weight: 500; }
  .mini-track { display: block; height: 4px; margin-top: 5px; border-radius: var(--radius-full); background: var(--color-surface-hover); overflow: hidden; }
  .mini-fill { display: block; height: 100%; border-radius: inherit; background: var(--color-success); }
  .mini-fill.low { background: var(--color-warning); }
  .mini-fill.exhausted { background: var(--color-danger); }
  .empty-state { padding: var(--space-8) var(--space-4); text-align: center; }
  .empty-state h3 { font-size: 14px; }
  .empty-state p { margin-top: 5px; color: var(--color-text-muted); font-size: 12px; }
  @media (max-width: 680px) { .model-row { grid-template-columns: 1fr auto; gap: 4px var(--space-3); } .reset-text { grid-column: 1 / -1; grid-row: 2; } .model-capacity { grid-column: 2; grid-row: 1; } }
</style>
