<script lang="ts">
  import { summarizeProviderQuotas } from '../utils/providerQuota.js';
  import type { QuotaSnapshotDto } from '../api/types.js';

  interface Props {
    quota: QuotaSnapshotDto | null;
    isAg2Connected: boolean;
    lowThresholdPercent: number;
  }

  let { quota, isAg2Connected, lowThresholdPercent }: Props = $props();
  const providers = $derived(summarizeProviderQuotas(isAg2Connected ? quota : null, lowThresholdPercent));
</script>

<section class="provider-section" aria-labelledby="provider-heading">
  <div class="section-heading">
    <div>
      <span class="eyebrow">Live capacity</span>
      <h2 id="provider-heading">Quota overview</h2>
      <p>Conservative availability across each provider's tracked models.</p>
    </div>
    <span class="snapshot-time">
      {isAg2Connected && quota?.timestamp ? `Updated ${new Date(quota.timestamp).toLocaleTimeString()}` : 'Waiting for telemetry'}
    </span>
  </div>

  <div class="provider-grid" aria-label="Provider quota summaries">
    {#each providers as summary (summary.provider)}
      <article class="provider-card" class:claude={summary.provider === 'Claude'} aria-label={`${summary.provider} quota summary`}>
        <div class="provider-topline">
          <span class="provider-mark" aria-hidden="true">{summary.provider === 'Gemini' ? 'G' : 'C'}</span>
          <span class="health-pill {summary.health}">
            <span class="health-dot" aria-hidden="true"></span>
            {summary.health === 'exhausted' ? 'Exhausted' : summary.health === 'low' ? 'Low capacity' : summary.health === 'healthy' ? 'Available' : 'Unknown'}
          </span>
        </div>

        <div class="provider-main">
          <div>
            <h3>{summary.provider}</h3>
            <p class="model-count">{summary.modelCount} {summary.modelCount === 1 ? 'model' : 'models'} tracked</p>
          </div>
          <div class="quota-value" aria-label={`${summary.provider} remaining quota ${summary.percent === null ? 'unknown' : `${summary.percent} percent`}`}>
            {summary.percent === null ? '—' : `${summary.percent}%`}
            <span>remaining</span>
          </div>
        </div>

        <div class="capacity-track"
          role={summary.percent === null ? 'status' : 'progressbar'}
          aria-label={`${summary.provider} remaining quota`}
          aria-valuemin={summary.percent === null ? undefined : 0}
          aria-valuemax={summary.percent === null ? undefined : 100}
          aria-valuenow={summary.percent === null ? undefined : summary.percent}
        >
          {#if summary.percent !== null}
            <span class="capacity-fill {summary.health}" style={`width: ${summary.percent}%`}></span>
          {/if}
        </div>

        <div class="provider-foot">
          <span>{summary.resetSummary}</span>
          {#if summary.unknownCount > 0}<span>{summary.unknownCount} unknown</span>{/if}
        </div>
      </article>
    {/each}
  </div>
</section>

<style>
  .provider-section { margin-bottom: var(--space-6); }
  .section-heading { display: flex; justify-content: space-between; align-items: end; gap: var(--space-4); margin-bottom: var(--space-4); }
  .eyebrow { display: block; margin-bottom: 2px; font-size: 11px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; color: var(--color-primary-text); }
  h2 { font-size: 19px; letter-spacing: -.035em; line-height: 1.2; }
  .section-heading p { margin-top: 5px; color: var(--color-text-muted); font-size: 12px; }
  .snapshot-time { color: var(--color-text-muted); font-size: 11px; white-space: nowrap; }
  .provider-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-4); }
  .provider-card { min-width: 0; padding: 20px 22px 17px; background: var(--color-surface); border: 1px solid var(--color-card-border); border-radius: var(--radius-lg); box-shadow: var(--shadow-sm); }
  .provider-topline, .provider-main, .provider-foot { display: flex; align-items: center; justify-content: space-between; gap: var(--space-3); }
  .provider-mark { display: inline-grid; place-items: center; width: 31px; height: 31px; border-radius: 9px; background: var(--color-primary-subtle); color: var(--color-primary-text); font-size: 16px; font-weight: 800; letter-spacing: -.05em; }
  .claude .provider-mark { background: #fbf0e6; color: #a45432; }
  .health-pill { display: inline-flex; align-items: center; gap: 6px; border-radius: var(--radius-full); padding: 4px 9px; font-size: 11px; font-weight: 650; background: var(--color-surface-subtle); color: var(--color-text-secondary); }
  .health-pill.healthy { background: var(--color-success-subtle); color: var(--color-success-text); }
  .health-pill.low { background: var(--color-warning-subtle); color: var(--color-warning-text); }
  .health-pill.exhausted { background: var(--color-danger-subtle); color: var(--color-danger-text); }
  .health-dot { width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
  .provider-main { align-items: end; margin: 23px 0 19px; }
  h3 { font-size: 23px; line-height: 1; letter-spacing: -.045em; }
  .model-count { margin-top: 7px; color: var(--color-text-muted); font-size: 12px; }
  .quota-value { text-align: right; font-size: 31px; line-height: .95; font-weight: 700; letter-spacing: -.055em; font-variant-numeric: tabular-nums; }
  .quota-value span { display: block; margin-top: 7px; font-size: 10px; font-weight: 500; letter-spacing: 0; color: var(--color-text-muted); }
  .capacity-track { height: 7px; overflow: hidden; border-radius: var(--radius-full); background: var(--color-surface-hover); }
  .capacity-fill { display: block; height: 100%; border-radius: inherit; background: var(--color-primary); transition: width var(--transition-normal); }
  .capacity-fill.healthy { background: var(--color-success); }
  .capacity-fill.low { background: var(--color-warning); }
  .capacity-fill.exhausted { background: var(--color-danger); }
  .provider-foot { margin-top: 12px; color: var(--color-text-muted); font-size: 11px; }
  @media (max-width: 680px) { .provider-grid { grid-template-columns: 1fr; } .section-heading { align-items: start; flex-direction: column; gap: 4px; } }
</style>
