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
      <span class="brand-mark" aria-hidden="true">A<span>2</span></span>
      <div>
        <h1 class="brand-title">AG2 Router</h1>
        <span class="brand-subtitle">Account intelligence</span>
      </div>
    </div>
    <div class="connection-status" role="status" aria-live="polite">
      <span class="status-dot {isConnected ? 'connected' : 'disconnected'}" aria-hidden="true"></span>
      <span class="status-text">{statusMessage}</span>
    </div>
  </div>

  <div class="header-right">
    <div class="active-account-chip" title="Currently Active Account">
      <span class="chip-label">Active account</span>
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
    padding: 13px 34px;
    background-color: var(--color-surface);
    border-bottom: 1px solid var(--color-divider);
    gap: var(--space-4);
  }

  .header-left {
    display: flex;
    align-items: center;
    gap: 24px;
    min-width: 0;
  }

  .brand {
    display: flex;
    align-items: center;
    gap: 10px;
    flex: 0 0 auto;
  }

  .brand-mark { display: inline-flex; align-items: baseline; justify-content: center; width: 35px; height: 35px; padding-top: 6px; border-radius: 10px; background: var(--color-primary); color: white; font-size: 18px; font-weight: 800; letter-spacing: -.07em; line-height: 1; box-shadow: 0 3px 9px rgba(38, 84, 150, .16); }
  .brand-mark span { font-size: 11px; }

  .brand-title {
    font-size: 15px;
    font-weight: 700;
    color: var(--color-text-primary);
    letter-spacing: -.035em;
  }
  .brand-subtitle { display: block; margin-top: 1px; font-size: 10px; color: var(--color-text-muted); }

  .connection-status {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    font-size: 12px;
    color: var(--color-text-secondary);
    min-width: 0;
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
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
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
    padding: 5px 11px;
    border-radius: var(--radius-sm);
    font-size: 12px;
    max-width: 310px;
  }

  .chip-label {
    color: var(--color-text-muted);
    font-weight: 500;
    white-space: nowrap;
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
    padding: 7px 12px;
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

  @media (max-width: 940px) {
    .app-header { flex-wrap: wrap; gap: 10px; }
    .header-left, .header-right { width: 100%; justify-content: space-between; }
    .status-text { max-width: 220px; }
  }
  @media (max-width: 640px) {
    .app-header { padding: 12px 16px; }
    .brand-subtitle, .chip-label { display: none; }
    .status-text { max-width: 40vw; }
    .active-account-chip { max-width: min(70vw, 310px); }
    .refresh-btn span:last-child { display: none; }
  }
</style>
