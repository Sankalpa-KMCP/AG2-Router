<script lang="ts">
  import ProgressRing from './ProgressRing.svelte';
  import type { LowestQuotaSummary } from '../utils/helpers.js';

  interface Props {
    totalAccounts: number;
    activeAccountName: string;
    activeAccountSubtitle: string | null;
    lowestQuota: LowestQuotaSummary | null;
    exhaustedCount: number;
    activeModelsCount: number;
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
    autoSwitchEnabled = false,
    lowThresholdPercent = 15
  }: Props = $props();
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
        <span class="badge {lowestQuota.isExhausted ? 'badge-danger' : lowestQuota.percent <= lowThresholdPercent ? 'badge-warning' : 'badge-healthy'}">
          {lowestQuota.isExhausted ? 'Exhausted' : lowestQuota.percent <= lowThresholdPercent ? 'Low' : 'Healthy'}
        </span>
      {:else}
        <span class="badge badge-neutral">Offline</span>
      {/if}
    </div>
    <div class="metric-body-with-ring">
      <div class="metric-details">
        <span class="metric-model-name" title={lowestQuota?.label || 'Waiting for telemetry'}>
          {lowestQuota?.label || 'Telemetry Offline'}
        </span>
        <span class="metric-subtext">Conservative model floor</span>
      </div>
      <div class="metric-ring-wrap">
        {#if lowestQuota}
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
      <span class="metric-label">Pools Status</span>
      <span class="badge {exhaustedCount > 0 ? 'badge-danger' : 'badge-healthy'}">
        {exhaustedCount > 0 ? `${exhaustedCount} Near Limit` : 'Nominal'}
      </span>
    </div>
    <div class="metric-body">
      <span class="metric-value-medium {exhaustedCount > 0 ? 'text-danger' : 'text-success'}">
        {exhaustedCount > 0 ? `${exhaustedCount} Exhausted` : 'All Healthy'}
      </span>
      <span class="metric-subtext">
        {activeModelsCount > 0 ? `${activeModelsCount} active quota pools monitored` : 'Waiting for telemetry data'}
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
        {autoSwitchEnabled ? 'AUTOMATED' : 'MANUAL ONLY'}
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
    grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
    gap: var(--space-3);
    margin-bottom: var(--space-5);
  }

  .metric-card {
    padding: var(--space-3-5) var(--space-4);
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    min-height: 96px;
  }

  .metric-top {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: var(--space-1-5);
  }

  .metric-label {
    font-size: 11.5px;
    font-weight: 600;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.04em;
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
    font-size: 24px;
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
</style>
