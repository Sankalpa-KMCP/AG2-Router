<script lang="ts">
  interface LogItem {
    timestamp: string;
    message: string;
  }

  interface Props {
    gateState: string;
    lastDecisionReason: string | null;
    logs: LogItem[];
    onClearLogs: () => void;
  }

  let {
    gateState = 'IDLE',
    lastDecisionReason = null,
    logs = [],
    onClearLogs
  }: Props = $props();

  const gateBadgeClass = $derived.by(() => {
    switch (gateState) {
      case 'IDLE': return 'badge-neutral';
      case 'EVALUATING': return 'badge-warning';
      case 'BLOCKED': return 'badge-danger';
      case 'COOLDOWN': return 'badge-warning';
      case 'EXECUTING': return 'badge-healthy';
      default: return 'badge-neutral';
    }
  });
</script>

<div class="card activity-card">
  <div class="activity-header">
    <div>
      <h2 class="activity-title">Activity &amp; Safety Gate</h2>
      <p class="activity-subtitle">Real-time decisions, safety checks, and transition log</p>
    </div>
    <button
      type="button"
      class="btn btn-secondary btn-sm"
      onclick={onClearLogs}
      disabled={logs.length === 0}
      title="Clear event log list"
    >
      Clear Log
    </button>
  </div>

  <!-- Safety Gate Status Card -->
  <div class="gate-status-box">
    <div class="gate-status-top">
      <span class="gate-label">Router Safety Gate:</span>
      <span class="badge {gateBadgeClass}">{gateState}</span>
    </div>
    <div class="gate-decision-text">
      {lastDecisionReason || 'Router initialized. Polling loopback state.'}
    </div>
  </div>

  <!-- Log list -->
  <div class="log-container">
    {#if logs.length === 0}
      <p class="empty-log-text">No activity recorded yet.</p>
    {:else}
      <ul class="log-list" aria-label="Activity Events">
        {#each logs as log, idx (log.timestamp + idx)}
          <li class="log-item">
            <span class="log-time">{log.timestamp}</span>
            <span class="log-msg">{log.message}</span>
          </li>
        {/each}
      </ul>
    {/if}
  </div>
</div>

<style>
  .activity-card {
    padding: var(--space-5);
    margin-bottom: var(--space-5);
  }

  .activity-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: var(--space-4);
  }

  .activity-title {
    font-size: 15px;
    font-weight: 700;
    color: var(--color-text-primary);
  }

  .activity-subtitle {
    font-size: 12px;
    color: var(--color-text-muted);
    margin-top: 2px;
  }

  .gate-status-box {
    background-color: var(--color-surface-subtle);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-md);
    padding: var(--space-3) var(--space-4);
    margin-bottom: var(--space-4);
  }

  .gate-status-top {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    margin-bottom: var(--space-1);
  }

  .gate-label {
    font-size: 12px;
    font-weight: 600;
    color: var(--color-text-secondary);
  }

  .gate-decision-text {
    font-size: 12.5px;
    color: var(--color-text-primary);
  }

  .log-container {
    max-height: 240px;
    overflow-y: auto;
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-sm);
    background-color: var(--color-surface);
  }

  .empty-log-text {
    padding: var(--space-4);
    text-align: center;
    color: var(--color-text-muted);
    font-size: 12px;
  }

  .log-list {
    list-style: none;
    margin: 0;
    padding: 0;
  }

  .log-item {
    display: flex;
    align-items: baseline;
    gap: var(--space-3);
    padding: var(--space-2) var(--space-3);
    border-bottom: 1px solid var(--color-divider);
    font-size: 12px;
  }

  .log-item:last-child {
    border-bottom: none;
  }

  .log-time {
    font-family: var(--font-mono);
    color: var(--color-text-muted);
    font-size: 11px;
    flex-shrink: 0;
  }

  .log-msg {
    color: var(--color-text-secondary);
  }
</style>
