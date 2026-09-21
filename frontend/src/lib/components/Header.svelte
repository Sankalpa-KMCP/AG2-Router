<script lang="ts">
  import type { SystemStatusDto } from '../api/types.js';

  interface Props {
    status: SystemStatusDto | null;
    activeAccountName: string;
    onRefresh: () => void;
    isRefreshing?: boolean;
  }

  let {
    status = null,
    activeAccountName = 'No active account',
    onRefresh,
    isRefreshing = false
  }: Props = $props();

  const isConnected = $derived(Boolean(status?.ag2?.connected));
  const statusMessage = $derived.by(() => {
    if (!status) return 'Connecting to loopback...';
    const ag2 = status.ag2;
    if (!ag2?.connected) return ag2?.message || 'Waiting for Antigravity 2';

    const activity = ag2.activity;
    if (activity) {
      const runCount = activity.runningTrajectories ?? 0;
      const state = activity.state || 'IDLE';
      return `${ag2.message || 'Antigravity 2 Connected'} • ${state}${runCount > 0 ? ` (${runCount} running)` : ''}`;
    }
    return ag2.message || 'Antigravity 2 Connected';
  });
</script>

<header class="app-header">
  <div class="header-left">
    <div class="brand">
      <h1 class="brand-title">AG2 Router</h1>
    </div>
    <div class="connection-status" role="status" aria-live="polite">
      <span class="status-dot {isConnected ? 'connected' : 'disconnected'}" aria-hidden="true"></span>
      <span class="status-text">{statusMessage}</span>
    </div>
  </div>

  <div class="header-right">
    <div class="active-account-chip" title="Currently Active Account">
      <span class="chip-label">Active:</span>
      <span class="chip-value">{activeAccountName}</span>
    </div>
    <button
      type="button"
      class="btn btn-secondary btn-sm refresh-btn"
      onclick={onRefresh}
      disabled={isRefreshing}
      title="Refresh status and accounts"
      aria-label="Refresh telemetry"
    >
      <span class="refresh-icon {isRefreshing ? 'spinning' : ''}">↻</span>
      <span>{isRefreshing ? 'Syncing...' : 'Refresh'}</span>
    </button>
  </div>
</header>

<style>
  .app-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: var(--space-3) var(--space-6);
    background-color: var(--color-surface);
    border-bottom: 1px solid var(--color-card-border);
    gap: var(--space-4);
  }

  .header-left {
    display: flex;
    align-items: center;
    gap: var(--space-5);
  }

  .brand {
    display: flex;
    align-items: baseline;
    gap: var(--space-2);
  }

  .brand-title {
    font-size: 16px;
    font-weight: 700;
    color: var(--color-text-primary);
    letter-spacing: -0.01em;
  }

  .connection-status {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    font-size: 12px;
    color: var(--color-text-secondary);
  }

  .status-dot {
    width: 8px;
    height: 8px;
    border-radius: var(--radius-full);
    transition: background-color var(--transition-fast);
  }

  .status-dot.connected {
    background-color: var(--color-success);
    box-shadow: 0 0 0 2px var(--color-success-subtle);
  }

  .status-dot.disconnected {
    background-color: var(--color-text-subtle);
  }

  .status-text {
    font-size: 12px;
  }

  .header-right {
    display: flex;
    align-items: center;
    gap: var(--space-3);
  }

  .active-account-chip {
    display: inline-flex;
    align-items: center;
    gap: var(--space-1-5);
    background-color: var(--color-surface-subtle);
    border: 1px solid var(--color-card-border);
    padding: 3px 10px;
    border-radius: var(--radius-full);
    font-size: 12px;
    max-width: 280px;
  }

  .chip-label {
    color: var(--color-text-muted);
    font-weight: 500;
  }

  .chip-value {
    color: var(--color-text-primary);
    font-weight: 600;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .refresh-btn {
    font-size: 12px;
    padding: 4px 10px;
  }

  .refresh-icon {
    display: inline-block;
    font-size: 14px;
    line-height: 1;
  }

  .refresh-icon.spinning {
    animation: spin 1s linear infinite;
  }

  @keyframes spin {
    from { transform: rotate(0deg); }
    to { transform: rotate(360deg); }
  }
</style>
