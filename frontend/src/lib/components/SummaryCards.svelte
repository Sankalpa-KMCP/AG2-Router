<script lang="ts">
  import ProgressRing from './ProgressRing.svelte';
  import { derivePoolsStatusSummary, type LowestQuotaSummary } from '../utils/helpers.js';

  interface Props {
    totalAccounts: number;
    activeAccountName: string;
    activeAccountSubtitle: string | null;
    lowestQuota: LowestQuotaSummary | null;
    exhaustedCount: number;
    activeModelsCount: number;
    unknownModelsCount?: number;
    autoSwitchEnabled: boolean;
    lowThresholdPercent: number;
  }

  let {
    totalAccounts = 0,
    activeAccountName = 'No active account',
    activeAccountSubtitle = null,
    lowestQuota = null,
    exhaustedCount = 0,
    activeModelsCount = 0,
    unknownModelsCount = 0,
    autoSwitchEnabled = false,
    lowThresholdPercent = 15
  }: Props = $props();

  const poolsSummary = $derived(derivePoolsStatusSummary(activeModelsCount, exhaustedCount, unknownModelsCount));
</script>

<section class="summary-grid" aria-label="Overview Metrics">
  <!-- Card 1: Total Accounts -->
  <div class="card metric-card">
    <div class="metric-top">
      <span class="metric-label">Total Accounts</span>
      <span class="badge badge-neutral">{totalAccounts}</span>
    </div>
    <div class="metric-body">
      <span class="metric-value-large">{totalAccounts}</span>
      <span class="metric-subtext">Configured in store</span>
    </div>
  </div>

  <!-- Card 2: Active Account -->
  <div class="card metric-card">
    <div class="metric-top">
      <span class="metric-label">Active Account</span>
      <span class="badge {activeAccountName !== 'No active account' ? 'badge-healthy' : 'badge-neutral'}">
        {activeAccountName !== 'No active account' ? 'Active' : 'None'}
      </span>
    </div>
    <div class="metric-body">
      <span class="metric-text-primary" title={activeAccountName}>{activeAccountName}</span>
      <span class="metric-subtext" title={activeAccountSubtitle || ''}>
        {activeAccountSubtitle || 'Antigravity session identity'}
      </span>
    </div>
  </div>

  <!-- Card 3: Lowest Model Quota Remaining (Honest, conservative, non-fabricated) -->
  <div class="card metric-card">
    <div class="metric-top">
      <span class="metric-label">Lowest Model Quota</span>
      {#if lowestQuota}
        <span class="badge {lowestQuota.isExhausted ? 'badge-danger' : lowestQuota.percent === null ? 'badge-neutral' : lowestQuota.percent <= lowThresholdPercent ? 'badge-warning' : 'badge-healthy'}">
          {lowestQuota.isExhausted ? 'Exhausted' : lowestQuota.percent === null ? 'Unknown' : lowestQuota.percent <= lowThresholdPercent ? 'Low' : 'Healthy'}
        </span>
      {:else}
        <span class="badge badge-neutral">{unknownModelsCount > 0 ? 'Unknown' : 'No Data'}</span>
      {/if}
    </div>
    <div class="metric-body-with-ring">
      <div class="metric-details">
        <span class="metric-model-name" title={lowestQuota?.label || 'Waiting for telemetry'}>
          {lowestQuota?.label || (unknownModelsCount > 0 ? 'Quota Unknown' : 'No Model Data')}
        </span>
        <span class="metric-subtext">Conservative model floor</span>
      </div>
      <div class="metric-ring-wrap">
        {#if lowestQuota && lowestQuota.percent !== null}
          <ProgressRing
            percent={lowestQuota.percent}
            size={46}
            strokeWidth={5}
            isExhausted={lowestQuota.isExhausted}
            label="Lowest model quota"
          />
        {:else}
          <span class="metric-placeholder">--%</span>
        {/if}
      </div>
    </div>
  </div>

  <!-- Card 4: Quota Health / Attention Required -->
  <div class="card metric-card">
    <div class="metric-top">
      <span class="metric-label">Model health</span>
      <span class="badge {poolsSummary.badgeClass}">
        {poolsSummary.badgeText}
      </span>
    </div>
    <div class="metric-body">
      <span class="metric-value-medium {poolsSummary.metricClass}">
        {poolsSummary.metricText}
      </span>
      <span class="metric-subtext">
        {activeModelsCount > 0 ? `${activeModelsCount} model rows monitored` : 'Waiting for telemetry data'}
      </span>
    </div>
  </div>

  <!-- Card 5: Auto-Switch Status -->
  <div class="card metric-card">
    <div class="metric-top">
      <span class="metric-label">Auto Switch</span>
      <span class="badge {autoSwitchEnabled ? 'badge-healthy' : 'badge-neutral'}">
        {autoSwitchEnabled ? 'Enabled' : 'Disabled'}
      </span>
    </div>
    <div class="metric-body">
      <span class="metric-value-medium {autoSwitchEnabled ? 'text-primary' : 'text-muted'}">
        {autoSwitchEnabled ? 'Automatic' : 'Manual only'}
      </span>
      <span class="metric-subtext">
        Trigger quota &le; {lowThresholdPercent}%
      </span>
    </div>
  </div>
</section>

<style>
  .summary-grid {
    display: grid;
    grid-template-columns: repeat(5, minmax(0, 1fr));
    gap: 10px;
    margin-bottom: 23px;
  }

  .metric-card {
    padding: 15px 16px;
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    min-height: 108px;
  }

  .metric-top {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: var(--space-1-5);
  }

  .metric-label {
    font-size: 11px;
    font-weight: 650;
    color: var(--color-text-muted);
    letter-spacing: .01em;
  }

  .metric-body {
    display: flex;
    flex-direction: column;
    gap: var(--space-0-5);
  }

  .metric-body-with-ring {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--space-2);
  }

  .metric-details {
    display: flex;
    flex-direction: column;
    gap: var(--space-0-5);
    overflow: hidden;
  }

  .metric-value-large {
    font-size: 27px;
    font-weight: 700;
    color: var(--color-text-primary);
    line-height: 1.1;
  }

  .metric-value-medium {
    font-size: 15px;
    font-weight: 700;
    line-height: 1.2;
  }

  .metric-text-primary {
    font-size: 14px;
    font-weight: 600;
    color: var(--color-text-primary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .metric-model-name {
    font-size: 13px;
    font-weight: 600;
    color: var(--color-text-primary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .metric-subtext {
    font-size: 11.5px;
    color: var(--color-text-muted);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .metric-placeholder {
    font-size: 16px;
    font-weight: 600;
    color: var(--color-text-subtle);
  }

  .text-success {
    color: var(--color-success);
  }

  .text-danger {
    color: var(--color-danger);
  }

  .text-primary {
    color: var(--color-primary);
  }

  .text-muted {
    color: var(--color-text-muted);
  }

  @media (max-width: 1150px) { .summary-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
  @media (max-width: 680px) { .summary-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } .metric-card { min-height: 100px; } }
  @media (max-width: 400px) { .summary-grid { grid-template-columns: 1fr; } }
</style>
