<script lang="ts">
  import { summarizeModelFamilies, deriveOverallCapacity } from '../utils/providerQuota.js';
  import type { QuotaSnapshotDto, AccountIdentityDto } from '../api/types.js';

  interface Props {
    quota: QuotaSnapshotDto | null;
    isAg2Connected: boolean;
    lowThresholdPercent: number;
    currentAccount?: AccountIdentityDto | null;
    onNavigateToTelemetry?: () => void;
  }

  let {
    quota,
    isAg2Connected,
    lowThresholdPercent,
    currentAccount = null,
    onNavigateToTelemetry
  }: Props = $props();

  const families = $derived(summarizeModelFamilies(isAg2Connected ? quota : null, lowThresholdPercent));
  const overall = $derived(deriveOverallCapacity(families, lowThresholdPercent));
</script>

<section class="capacity-section" aria-labelledby="capacity-heading">
  <div class="section-header">
    <div class="header-titles">
      <span class="eyebrow">Live capacity</span>
      <h2 id="capacity-heading">Capacity overview</h2>
      <p class="header-desc">Bottleneck availability across active model families.</p>
    </div>
    <div class="header-aside">
      <span class="snapshot-time">
        {isAg2Connected && quota?.timestamp
          ? `Synced ${new Date(quota.timestamp).toLocaleTimeString()}`
          : 'Waiting for telemetry'}
      </span>
    </div>
  </div>

  <!-- Large Overall Capacity Summary -->
  <div
    class="overall-card"
    class:exhausted={overall.health === 'exhausted'}
    class:low={overall.health === 'low'}
    class:unknown={overall.health === 'unknown'}
    aria-label="Overall available capacity summary"
  >
    <div class="overall-body">
      <div class="overall-metric-block">
        <span
          class="overall-number"
          aria-label={`Available capacity: ${overall.percent === null ? 'unknown' : `${overall.percent}%`}`}
        >
          {overall.percent === null ? '—' : `${overall.percent}%`}
        </span>
        <div class="overall-label-block">
          <span class="overall-title">Available capacity</span>
          <span class="overall-caption">
            {#if !isAg2Connected}
              Antigravity telemetry disconnected
            {:else if overall.health === 'exhausted'}
              Critical &bull; {overall.bottleneckFamily || 'Model'} exhausted
            {:else if overall.health === 'unknown'}
              Indeterminate &bull; Partial observation
            {:else if overall.health === 'low'}
              Low capacity &bull; Bottleneck: {overall.bottleneckFamily || 'Observed models'}
            {:else}
              Bottleneck floor &bull; Limited by {overall.bottleneckFamily || 'active family'}
            {/if}
          </span>
        </div>
      </div>

      <div class="overall-context-block">
        {#if currentAccount?.email}
          <div class="account-badge" title={`Active account: ${currentAccount.email}`}>
            <span class="account-dot" aria-hidden="true"></span>
            <span class="account-email">{currentAccount.email}</span>
          </div>
        {/if}
        <span class="coverage-text">
          {overall.observedFamiliesCount} {overall.observedFamiliesCount === 1 ? 'model family' : 'model families'} observed
          {#if overall.totalModelsObserved > 0}
            <span class="variants-count">({overall.totalModelsObserved} variants)</span>
          {/if}
        </span>
      </div>
    </div>

    <div
      class="overall-track"
      role={overall.percent === null ? 'status' : 'progressbar'}
      aria-label="Overall capacity level"
      aria-valuemin={overall.percent === null ? undefined : 0}
      aria-valuemax={overall.percent === null ? undefined : 100}
      aria-valuenow={overall.percent === null ? undefined : overall.percent}
    >
      {#if overall.percent !== null}
        <span class="overall-fill {overall.health}" style={`width: ${overall.percent}%`}></span>
      {/if}
    </div>
  </div>

  <!-- Model Family Cards Grid -->
  {#if families.length > 0}
    <div class="family-grid" aria-label="Model family quotas">
      {#each families as family (family.key)}
        <article
          class="family-card"
          class:claude={family.provider === 'Claude'}
          class:exhausted={family.health === 'exhausted'}
          aria-label={`${family.displayName} quota summary`}
        >
          <div class="card-head">
            <div class="family-name-group">
              <span class="provider-tag {family.provider.toLowerCase()}">{family.provider}</span>
              <h3 class="family-name">{family.displayName}</h3>
            </div>
            <span class="health-pill {family.health}">
              <span class="health-dot" aria-hidden="true"></span>
              {family.health === 'exhausted' ? 'Exhausted' : family.health === 'low' ? 'Low' : family.health === 'healthy' ? 'Available' : 'Unknown'}
            </span>
          </div>

          <div class="card-metric-row">
            <div class="metric-value-wrap">
              <span
                class="family-percent"
                aria-label={`${family.displayName} remaining quota ${family.percent === null ? 'unknown' : `${family.percent} percent`}`}
              >
                {family.percent === null ? '—' : `${family.percent}%`}
              </span>
              <span class="metric-caption">remaining</span>
            </div>
            {#if family.modelCount > 1}
              <span class="variant-badge">{family.modelCount} variants</span>
            {/if}
          </div>

          <div
            class="family-track"
            role={family.percent === null ? 'status' : 'progressbar'}
            aria-label={`${family.displayName} remaining quota`}
            aria-valuemin={family.percent === null ? undefined : 0}
            aria-valuemax={family.percent === null ? undefined : 100}
            aria-valuenow={family.percent === null ? undefined : family.percent}
          >
            {#if family.percent !== null}
              <span class="family-fill {family.health}" style={`width: ${family.percent}%`}></span>
            {/if}
          </div>

          <div class="card-foot">
            <span class="reset-time">{family.resetSummary}</span>
            {#if family.unknownCount > 0}
              <span class="unknown-tag">{family.unknownCount} unknown</span>
            {/if}
          </div>
        </article>
      {/each}
    </div>
  {:else}
    <div class="empty-state">
      <p>{isAg2Connected ? 'No tracked model families observed in current telemetry snapshot.' : 'Waiting for Antigravity telemetry connection...'}</p>
    </div>
  {/if}

  <!-- Telemetry Footnote -->
  <div class="section-footnote">
    <span>Detailed per-model rows, token pools, and additional models are preserved in Telemetry &amp; quotas.</span>
    {#if onNavigateToTelemetry}
      <button type="button" class="footnote-link" onclick={onNavigateToTelemetry}>
        View detailed quotas &rarr;
      </button>
    {/if}
  </div>
</section>

<style>
  .capacity-section {
    margin-bottom: var(--space-6);
  }

  .section-header {
    display: flex;
    justify-content: space-between;
    align-items: flex-end;
    gap: var(--space-4);
    margin-bottom: var(--space-3);
  }

  .eyebrow {
    display: block;
    margin-bottom: 2px;
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--color-primary-text);
  }

  h2 {
    font-size: 19px;
    letter-spacing: -0.035em;
    line-height: 1.2;
    color: var(--color-text-primary);
  }

  .header-desc {
    margin-top: 4px;
    color: var(--color-text-muted);
    font-size: 12px;
  }

  .snapshot-time {
    color: var(--color-text-muted);
    font-size: 11px;
    white-space: nowrap;
  }

  /* Large Overall Capacity Summary */
  .overall-card {
    padding: 18px 22px 16px;
    background: var(--color-surface);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-lg);
    box-shadow: var(--shadow-xs);
    margin-bottom: var(--space-4);
  }

  .overall-body {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: var(--space-4);
    margin-bottom: 14px;
  }

  .overall-metric-block {
    display: flex;
    align-items: baseline;
    gap: var(--space-3);
  }

  .overall-number {
    font-size: 38px;
    line-height: 1;
    font-weight: 750;
    letter-spacing: -0.055em;
    color: var(--color-text-primary);
    font-variant-numeric: tabular-nums;
  }

  .overall-label-block {
    display: flex;
    flex-direction: column;
    gap: 3px;
  }

  .overall-title {
    font-size: 14px;
    font-weight: 650;
    letter-spacing: -0.015em;
    color: var(--color-text-primary);
  }

  .overall-caption {
    font-size: 12px;
    color: var(--color-text-muted);
  }

  .overall-context-block {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    gap: 6px;
  }

  .account-badge {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 3px 9px;
    border-radius: var(--radius-full);
    background: var(--color-surface-subtle);
    border: 1px solid var(--color-card-border);
    font-size: 11px;
    font-weight: 550;
    color: var(--color-text-secondary);
  }

  .account-dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--color-success);
  }

  .account-email {
    max-width: 220px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .coverage-text {
    font-size: 11px;
    color: var(--color-text-muted);
  }

  .variants-count {
    color: var(--color-text-subtle);
  }

  .overall-track {
    height: 5px;
    overflow: hidden;
    border-radius: var(--radius-full);
    background: var(--color-surface-hover);
  }

  .overall-fill {
    display: block;
    height: 100%;
    border-radius: inherit;
    background: var(--color-primary);
    transition: width var(--transition-normal);
  }

  .overall-fill.healthy {
    background: var(--color-success);
  }

  .overall-fill.low {
    background: var(--color-warning);
  }

  .overall-fill.exhausted {
    background: var(--color-danger);
  }

  /* Model Family Cards Grid */
  .family-grid {
    display: grid;
    grid-template-columns: repeat(4, minmax(0, 1fr));
    gap: var(--space-3);
  }

  .family-card {
    min-width: 0;
    padding: 14px 16px 13px;
    background: var(--color-surface);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-xs);
    display: flex;
    flex-direction: column;
    justify-content: space-between;
  }

  .card-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--space-2);
    margin-bottom: 12px;
  }

  .family-name-group {
    display: flex;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }

  .provider-tag {
    display: inline-block;
    padding: 1px 5px;
    border-radius: var(--radius-xs);
    font-size: 9px;
    font-weight: 700;
    letter-spacing: 0.04em;
    text-transform: uppercase;
    background: var(--color-primary-subtle);
    color: var(--color-primary-text);
  }

  .claude .provider-tag {
    background: #fbf0e6;
    color: #a45432;
  }

  .family-name {
    font-size: 13px;
    font-weight: 650;
    letter-spacing: -0.02em;
    color: var(--color-text-primary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .health-pill {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    border-radius: var(--radius-full);
    padding: 2px 7px;
    font-size: 10px;
    font-weight: 600;
    background: var(--color-surface-subtle);
    color: var(--color-text-secondary);
    white-space: nowrap;
  }

  .health-pill.healthy {
    background: var(--color-success-subtle);
    color: var(--color-success-text);
  }

  .health-pill.low {
    background: var(--color-warning-subtle);
    color: var(--color-warning-text);
  }

  .health-pill.exhausted {
    background: var(--color-danger-subtle);
    color: var(--color-danger-text);
  }

  .health-dot {
    width: 5px;
    height: 5px;
    border-radius: 50%;
    background: currentColor;
  }

  .card-metric-row {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    margin-bottom: 8px;
  }

  .metric-value-wrap {
    display: flex;
    align-items: baseline;
    gap: 5px;
  }

  .family-percent {
    font-size: 22px;
    line-height: 1;
    font-weight: 700;
    letter-spacing: -0.04em;
    color: var(--color-text-primary);
    font-variant-numeric: tabular-nums;
  }

  .metric-caption {
    font-size: 10px;
    color: var(--color-text-muted);
    font-weight: 500;
  }

  .variant-badge {
    font-size: 10px;
    font-weight: 550;
    color: var(--color-text-subtle);
  }

  .family-track {
    height: 4px;
    overflow: hidden;
    border-radius: var(--radius-full);
    background: var(--color-surface-hover);
    margin-bottom: 10px;
  }

  .family-fill {
    display: block;
    height: 100%;
    border-radius: inherit;
    background: var(--color-primary);
    transition: width var(--transition-normal);
  }

  .family-fill.healthy {
    background: var(--color-success);
  }

  .family-fill.low {
    background: var(--color-warning);
  }

  .family-fill.exhausted {
    background: var(--color-danger);
  }

  .card-foot {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: var(--space-2);
    font-size: 11px;
    color: var(--color-text-muted);
  }

  .reset-time {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .unknown-tag {
    font-size: 10px;
    color: var(--color-warning-text);
  }

  .empty-state {
    padding: var(--space-6);
    background: var(--color-surface);
    border: 1px dashed var(--color-card-border);
    border-radius: var(--radius-md);
    text-align: center;
    color: var(--color-text-muted);
    font-size: 12px;
  }

  .section-footnote {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: var(--space-3);
    margin-top: var(--space-2-5);
    font-size: 11px;
    color: var(--color-text-subtle);
  }

  .footnote-link {
    background: none;
    border: none;
    padding: 0;
    font-size: 11px;
    font-weight: 600;
    color: var(--color-primary-text);
    cursor: pointer;
    text-decoration: underline;
    text-underline-offset: 2px;
  }

  .footnote-link:hover {
    color: var(--color-primary);
  }

  /* Responsive layout */
  @media (max-width: 900px) {
    .family-grid {
      grid-template-columns: repeat(2, minmax(0, 1fr));
    }
  }

  @media (max-width: 600px) {
    .family-grid {
      grid-template-columns: 1fr;
    }
    .section-header {
      align-items: flex-start;
      flex-direction: column;
      gap: 4px;
    }
    .overall-body {
      flex-direction: column;
      align-items: flex-start;
      gap: var(--space-2);
    }
    .overall-context-block {
      align-items: flex-start;
    }
  }
</style>
