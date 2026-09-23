<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import Header from './lib/components/Header.svelte';
  import SummaryCards from './lib/components/SummaryCards.svelte';
  import QuotaSection from './lib/components/QuotaSection.svelte';
  import AccountsTable from './lib/components/AccountsTable.svelte';
  import RoutingConfigSection from './lib/components/RoutingConfigSection.svelte';
  import ActivityLogSection from './lib/components/ActivityLogSection.svelte';
  import ConnectAccountModal from './lib/components/ConnectAccountModal.svelte';
  import DeleteAccountModal from './lib/components/DeleteAccountModal.svelte';
  import ExecuteSwitchConfirmModal from './lib/components/ExecuteSwitchConfirmModal.svelte';
  import { api } from './lib/api/client.js';
  import {
    resolveAccountDisplayName,
    getAccountSubtitle,
    deriveLowestModelQuota
  } from './lib/utils/helpers.js';
  import type {
    SystemStatusDto,
    AccountMetadata,
    RouterConfigDto
  } from './lib/api/types.js';

  // Application State
  let status = $state<SystemStatusDto | null>(null);
  let accounts = $state<AccountMetadata[]>([]);
  let totalAccountsCount = $state<number>(0);
  let activeAccountId = $state<string | null>(null);
  let routerConfig = $state<RouterConfigDto | null>(null);

  interface LogEntry {
    timestamp: string;
    message: string;
  }
  let activityLogs = $state<LogEntry[]>([]);

  let activeTab = $state<'overview' | 'accounts' | 'telemetry' | 'routing' | 'activity'>('overview');
  let isInitialLoading = $state<boolean>(true);
  let isRefreshing = $state<boolean>(false);
  let isSavingCurrent = $state<boolean>(false);
  let lastPollTime = $state<string>('Never');
  let globalNotification = $state<{ type: 'error' | 'success'; message: string } | null>(null);

  // Modal State
  let isConnectModalOpen = $state<boolean>(false);

  let deleteModalOpen = $state<boolean>(false);
  let deleteTargetAccount = $state<AccountMetadata | null>(null);
  let isDeleting = $state<boolean>(false);

  let switchModalOpen = $state<boolean>(false);
  let switchTargetAccount = $state<AccountMetadata | null>(null);
  let isSwitching = $state<boolean>(false);

  // Derived Values
  const activeAccount = $derived.by(() => {
    if (!activeAccountId && accounts.length > 0) {
      return accounts.find(a => a.isActive) || null;
    }
    return accounts.find(a => a.id === activeAccountId) || null;
  });

  const activeAccountDisplayName = $derived.by(() => {
    if (activeAccount) {
      return resolveAccountDisplayName(activeAccount);
    }
    const ag2Email = status?.telemetry?.currentAccount?.email;
    if (ag2Email) return ag2Email;
    return 'No active account';
  });

  const activeAccountSubtitle = $derived.by(() => {
    if (activeAccount) {
      return getAccountSubtitle(activeAccount);
    }
    const currentAcc = status?.telemetry?.currentAccount;
    if (currentAcc?.name) return currentAcc.name;
    return null;
  });

  const quotaSnapshot = $derived(status?.telemetry?.quota ?? null);

  const modelPools = $derived.by(() => {
    if (quotaSnapshot?.canonicalModels && quotaSnapshot.canonicalModels.length > 0) {
      return quotaSnapshot.canonicalModels;
    }
    return quotaSnapshot?.models || [];
  });

  const lowestQuota = $derived(deriveLowestModelQuota(modelPools));

  const exhaustedCount = $derived(
    modelPools.filter(m => m.isExhausted || m.remainingFraction <= 0).length
  );

  const activeModelsCount = $derived(modelPools.length);

  const autoSwitchEnabled = $derived(
    Boolean(status?.router?.autoSwitchEnabled)
  );

  const lowThresholdPercent = $derived(
    routerConfig?.lowQuotaThresholdPercent ?? status?.router?.config?.lowQuotaThresholdPercent ?? 15
  );

  function logActivity(message: string) {
    const timestamp = new Date().toLocaleTimeString();
    activityLogs = [{ timestamp, message }, ...activityLogs.slice(0, 49)];
  }

  // Polling routine
  let pollTimer: ReturnType<typeof setInterval> | null = null;
  let configMutationFence = 0;

  async function refreshAll() {
    isRefreshing = true;
    const capturedFence = configMutationFence;
    try {
      const [statusRes, accountsRes, configRes] = await Promise.all([
        api.getStatus().catch(() => null),
        api.getAccounts().catch(() => ({ accounts: [], totalCount: 0, activeAccountId: null })),
        api.getConfig().catch(() => ({ config: null }))
      ]);

      if (statusRes) {
        status = statusRes;
      }
      if (accountsRes) {
        accounts = accountsRes.accounts || [];
        totalAccountsCount = accountsRes.totalCount ?? accounts.length;
        activeAccountId = accountsRes.activeAccountId || null;
      }
      if (configRes && configRes.config && configMutationFence <= capturedFence) {
        routerConfig = configRes.config;
      }

      lastPollTime = new Date().toLocaleTimeString();
    } finally {
      isInitialLoading = false;
      isRefreshing = false;
    }
  }

  // Handlers
  async function handleUpdateAlias(id: string, newAlias: string) {
    const res = await api.updateAccountAlias(id, newAlias);
    if (res.success && res.account) {
      accounts = accounts.map(a => (a.id === id ? { ...a, alias: res.account.alias } : a));
      logActivity(`Updated alias for ${res.account.email} to "${res.account.alias || '(cleared)'}".`);
    }
  }

  async function handleSaveCurrentAccount() {
    isSavingCurrent = true;
    try {
      const res = await api.enrollCurrentAccount();
      logActivity(res.message || `Captured running Antigravity 2 session for ${res.account.email}.`);
      await refreshAll();
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Session capture failed';
      logActivity(`Session capture error: ${msg}`);
      globalNotification = { type: 'error', message: msg };
    } finally {
      isSavingCurrent = false;
    }
  }

  async function handleConnectAccount(data: {
    email: string;
    name?: string;
    alias?: string;
    priority: number;
    isReserve: boolean;
  }) {
    const res = await api.createAccount(data);
    logActivity(`Connected account metadata for ${res.account.email}.`);
    await refreshAll();
  }

  function handleOpenExecuteSwitch(account: AccountMetadata) {
    switchTargetAccount = account;
    switchModalOpen = true;
  }

  async function handleConfirmSwitch(account: AccountMetadata) {
    isSwitching = true;
    try {
      const res = await api.executeSwitch(account.id);
      logActivity(res.message || `Switched active account to ${account.email}.`);
      await refreshAll();
    } finally {
      isSwitching = false;
    }
  }

  function handleOpenDelete(account: AccountMetadata) {
    deleteTargetAccount = account;
    deleteModalOpen = true;
  }

  async function handleConfirmDelete(account: AccountMetadata) {
    isDeleting = true;
    try {
      const res = await api.deleteAccount(account.id);
      if (res.vaultRecordDeleted) {
        logActivity(`Deleted account and wiped vaulted session (${account.email}).`);
      } else {
        logActivity(`Deleted account metadata (${account.email}).`);
      }
      await refreshAll();
    } finally {
      isDeleting = false;
    }
  }

  async function handleSaveConfig(updated: {
    autoSwitchEnabled: boolean;
    lowQuotaThresholdPercent: number;
    minimumCandidateQuotaPercent: number;
    pollingIntervalMs: number;
  }) {
    configMutationFence++;
    const res = await api.saveConfig(updated);
    if (res.success && res.config) {
      routerConfig = res.config;
      logActivity(`Updated router configuration: AutoSwitch=${updated.autoSwitchEnabled}, Threshold=${updated.lowQuotaThresholdPercent}%`);
      await refreshAll();
    }
  }

  onMount(() => {
    logActivity('Dashboard mounted. Initializing loopback connection.');
    refreshAll();
    pollTimer = setInterval(refreshAll, 6000);
  });

  onDestroy(() => {
    if (pollTimer) clearInterval(pollTimer);
  });
</script>

<div class="app-layout">
  <!-- Top Navigation & Brand Header -->
  <Header
    {status}
    activeAccountName={activeAccountDisplayName}
    onRefresh={refreshAll}
    {isRefreshing}
  />

  <!-- Main Content Body -->
  <main class="main-content">
    {#if isInitialLoading}
      <div class="loading-container">
        <div class="spinner" aria-hidden="true"></div>
        <p class="loading-text">Loading AG2 Router dashboard...</p>
      </div>
    {:else}
      {#if globalNotification}
        <div class="notification-banner {globalNotification.type}" role="alert">
          <span class="notification-text">{globalNotification.message}</span>
          <button
            type="button"
            class="btn-icon notification-close"
            onclick={() => (globalNotification = null)}
            aria-label="Dismiss notification"
          >
            ✕
          </button>
        </div>
      {/if}

      <!-- Summary metrics cards -->
      <SummaryCards
        totalAccounts={totalAccountsCount}
        activeAccountName={activeAccountDisplayName}
        {activeAccountSubtitle}
        {lowestQuota}
        {exhaustedCount}
        {activeModelsCount}
        {autoSwitchEnabled}
        {lowThresholdPercent}
      />

      <!-- Navigation Tabs -->
      <nav class="view-nav" aria-label="Dashboard Views">
        <button
          type="button"
          class="nav-tab {activeTab === 'overview' ? 'active' : ''}"
          onclick={() => (activeTab = 'overview')}
        >
          Overview
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'accounts' ? 'active' : ''}"
          onclick={() => (activeTab = 'accounts')}
        >
          Accounts ({totalAccountsCount})
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'telemetry' ? 'active' : ''}"
          onclick={() => (activeTab = 'telemetry')}
        >
          Telemetry &amp; Quotas
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'routing' ? 'active' : ''}"
          onclick={() => (activeTab = 'routing')}
        >
          Settings
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'activity' ? 'active' : ''}"
          onclick={() => (activeTab = 'activity')}
        >
          Activity &amp; Safety
        </button>
      </nav>

      <!-- View Panels -->
      {#if activeTab === 'overview'}
        <QuotaSection
          quota={quotaSnapshot}
          isAg2Connected={Boolean(status?.ag2?.connected)}
          {lowThresholdPercent}
        />
        <AccountsTable
          {accounts}
          onSaveCurrent={handleSaveCurrentAccount}
          onOpenConnect={() => (isConnectModalOpen = true)}
          onExecuteSwitch={handleOpenExecuteSwitch}
          onDeleteAccount={handleOpenDelete}
          onUpdateAlias={handleUpdateAlias}
          {isSavingCurrent}
          {isSwitching}
        />
      {:else if activeTab === 'accounts'}
        <AccountsTable
          {accounts}
          onSaveCurrent={handleSaveCurrentAccount}
          onOpenConnect={() => (isConnectModalOpen = true)}
          onExecuteSwitch={handleOpenExecuteSwitch}
          onDeleteAccount={handleOpenDelete}
          onUpdateAlias={handleUpdateAlias}
          {isSavingCurrent}
          {isSwitching}
        />
      {:else if activeTab === 'telemetry'}
        <QuotaSection
          quota={quotaSnapshot}
          isAg2Connected={Boolean(status?.ag2?.connected)}
          {lowThresholdPercent}
        />
      {:else if activeTab === 'routing'}
        <RoutingConfigSection
          config={routerConfig}
          {autoSwitchEnabled}
          onSaveConfig={handleSaveConfig}
        />
      {:else if activeTab === 'activity'}
        <ActivityLogSection
          gateState={status?.router?.state || 'IDLE'}
          lastDecisionReason={status?.router?.lastDecisionReason || null}
          logs={activityLogs}
          onClearLogs={() => (activityLogs = [])}
        />
      {/if}
    {/if}
  </main>

  <!-- App Footer -->
  <footer class="app-footer">
    <span>AG2 Router &bull; Loopback only (127.0.0.1) &bull; Zero external network telemetry</span>
    <span class="footer-poll">Last poll: {lastPollTime}</span>
  </footer>
</div>

<!-- Modal Dialogs -->
<ConnectAccountModal
  isOpen={isConnectModalOpen}
  onClose={() => (isConnectModalOpen = false)}
  onSubmit={handleConnectAccount}
/>

<ExecuteSwitchConfirmModal
  isOpen={switchModalOpen}
  account={switchTargetAccount}
  {isSwitching}
  onClose={() => (switchModalOpen = false)}
  onConfirm={handleConfirmSwitch}
/>

<DeleteAccountModal
  isOpen={deleteModalOpen}
  account={deleteTargetAccount}
  {isDeleting}
  onClose={() => (deleteModalOpen = false)}
  onConfirm={handleConfirmDelete}
/>

<style>
  .app-layout {
    display: flex;
    flex-direction: column;
    min-height: 100vh;
    background-color: var(--color-canvas);
  }

  .main-content {
    flex: 1;
    padding: var(--space-5) var(--space-6);
    max-width: 1360px;
    width: 100%;
    margin: 0 auto;
  }

  .view-nav {
    display: flex;
    align-items: center;
    gap: var(--space-1);
    margin-bottom: var(--space-4);
    border-bottom: 1px solid var(--color-card-border);
    padding-bottom: var(--space-1);
  }

  .nav-tab {
    background: transparent;
    border: none;
    font-family: var(--font-sans);
    font-size: 13px;
    font-weight: 500;
    color: var(--color-text-secondary);
    padding: var(--space-2) var(--space-3);
    border-radius: var(--radius-sm);
    cursor: pointer;
    transition: all var(--transition-fast);
  }

  .nav-tab:hover {
    background-color: var(--color-surface-hover);
    color: var(--color-text-primary);
  }

  .nav-tab.active {
    background-color: var(--color-primary-subtle);
    color: var(--color-primary-text);
    font-weight: 600;
  }

  .app-footer {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: var(--space-3) var(--space-6);
    background-color: var(--color-surface);
    border-top: 1px solid var(--color-card-border);
    font-size: 11.5px;
    color: var(--color-text-muted);
  }

  .footer-poll {
    font-family: var(--font-mono);
  }

  .loading-container {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    padding: var(--space-8);
    gap: var(--space-3);
  }

  .spinner {
    width: 32px;
    height: 32px;
    border: 3px solid var(--color-card-border);
    border-top-color: var(--color-primary);
    border-radius: var(--radius-full);
    animation: spin 1s linear infinite;
  }

  .loading-text {
    font-size: 13px;
    color: var(--color-text-muted);
  }

  @keyframes spin {
    from { transform: rotate(0deg); }
    to { transform: rotate(360deg); }
  }

  .notification-banner {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: var(--space-2-5) var(--space-4);
    border-radius: var(--radius-sm);
    margin-bottom: var(--space-4);
    font-size: 12.5px;
    gap: var(--space-3);
  }

  .notification-banner.error {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
  }

  .notification-banner.success {
    background-color: var(--color-success-subtle);
    border: 1px solid var(--color-success-border);
    color: var(--color-success-text);
  }

  .notification-close {
    font-size: 13px;
    padding: 2px 6px;
    color: inherit;
  }
</style>
