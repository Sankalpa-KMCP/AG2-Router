<script lang="ts">
  import { updateAlias, connectAccount, type ConnectAccountInput } from './lib/utils/account-mutations.js';
  import { onMount, onDestroy } from 'svelte';
  import Header from './lib/components/Header.svelte';
  import SummaryCards from './lib/components/SummaryCards.svelte';
  import QuotaSection from './lib/components/QuotaSection.svelte';
  import ProviderOverview from './lib/components/ProviderOverview.svelte';
  import AccountsTable from './lib/components/AccountsTable.svelte';
  import RoutingConfigSection from './lib/components/RoutingConfigSection.svelte';
  import ActivityLogSection from './lib/components/ActivityLogSection.svelte';
  import UsageSection from './lib/components/UsageSection.svelte';
  import ConnectAccountModal from './lib/components/ConnectAccountModal.svelte';
  import DeleteAccountModal from './lib/components/DeleteAccountModal.svelte';
  import ExecuteSwitchConfirmModal from './lib/components/ExecuteSwitchConfirmModal.svelte';
  import SwitchJournalRecoveryModal from './lib/components/SwitchJournalRecoveryModal.svelte';
  import { api } from './lib/api/client.js';
  import { executeDashboardSwitch } from './lib/utils/switching.js';
  import {
    resolveAccountDisplayName,
    getAccountSubtitle,
    deriveLowestModelQuota,
    DashboardRefreshGate
  } from './lib/utils/helpers.js';
  import {
    getBannerCopy,
    isValidJournalRecoveryState,
    validateSwitchStatusDto,
    getRecoveryActivityLogMessage,
    canExecuteLifecycleMutation
  } from './lib/utils/recovery.js';
  import type {
    SystemStatusDto,
    AccountMetadata,
    RouterConfigDto,
    RouterConfigUpdate,
    CandidateEvidenceStatusDto,
    SwitchStatusDto,
    JournalResolutionResult
  } from './lib/api/types.js';

  // Application State
  let status = $state<SystemStatusDto | null>(null);
  let accounts = $state<AccountMetadata[]>([]);
  let totalAccountsCount = $state<number>(0);
  let activeAccountId = $state<string | null>(null);
  let routerConfig = $state<RouterConfigDto | null>(null);
  let candidateEvidence = $state<CandidateEvidenceStatusDto | null>(null);
  let switchStatus = $state<SwitchStatusDto | null>(null);
  let isSafetyAuthoritative = $state<boolean>(false);

  interface LogEntry {
    timestamp: string;
    message: string;
  }
  let activityLogs = $state<LogEntry[]>([]);

  let activeTab = $state<'overview' | 'accounts' | 'telemetry' | 'routing' | 'usage' | 'activity'>('overview');
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

  let isRecoveryModalOpen = $state<boolean>(false);
  let isResolvingQuarantine = $state<boolean>(false);

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
    modelPools.filter(m => m.isExhausted || (m.remainingFraction !== null && Number.isFinite(m.remainingFraction) && m.remainingFraction <= 0)).length
  );

  const unknownModelsCount = $derived(
    modelPools.filter(m => !m.isExhausted && (m.remainingFraction === null || !Number.isFinite(m.remainingFraction))).length
  );

  const activeModelsCount = $derived(modelPools.length);

  const autoSwitchEnabled = $derived(
    Boolean(status?.router?.autoSwitchEnabled)
  );

  const lowThresholdPercent = $derived(
    routerConfig?.lowQuotaThresholdPercent ?? status?.router?.config?.lowQuotaThresholdPercent ?? 15
  );

  const quarantineActive = $derived(
    switchStatus ? Boolean(switchStatus.quarantineActive) : true
  );

  const journalRecoveryState = $derived(
    switchStatus?.journalRecoveryState || 'UNKNOWN'
  );

  const isSafetyEstablished = $derived(
    isSafetyAuthoritative &&
    switchStatus !== null &&
    typeof switchStatus.quarantineActive === 'boolean' &&
    isValidJournalRecoveryState(switchStatus.journalRecoveryState)
  );

  // Lifecycle mutations (Save current, Switch, Delete in AccountsTable) may be enabled
  // ONLY after a CURRENT, successfully parsed, authoritative switching-status response proves:
  // quarantineActive === false AND journalRecoveryState === 'NONE'.
  const lifecycleMutationsAllowed = $derived(
    canExecuteLifecycleMutation(isSafetyAuthoritative, switchStatus)
  );

  const quarantineBanner = $derived(
    getBannerCopy(quarantineActive, journalRecoveryState)
  );

  function logActivity(message: string) {
    const timestamp = new Date().toLocaleTimeString();
    activityLogs = [{ timestamp, message }, ...activityLogs.slice(0, 49)];
  }

  // Polling routine
  let pollTimer: ReturnType<typeof setInterval> | null = null;
  const refreshGate = new DashboardRefreshGate();
  let latestRefresh = 0;

  async function refreshAll() {
    const refreshId = ++latestRefresh;
    isRefreshing = true;
    const statusTicket = refreshGate.beginRead('status');
    const accountsTicket = refreshGate.beginRead('accounts');
    const configTicket = refreshGate.beginRead('config');
    const evidenceTicket = refreshGate.beginRead('evidence');
    const switchTicket = refreshGate.beginRead('switch');
    try {
      await Promise.all([
        api.getCandidateEvidence().then(value => {
          if (refreshGate.canPublish(evidenceTicket)) candidateEvidence = value;
        }).catch(() => {
          if (refreshGate.canPublish(evidenceTicket)) candidateEvidence = null;
        }),
        api.getStatus().then(value => {
          if (refreshGate.canPublish(statusTicket)) status = value;
        }).catch(() => {}),
        api.getAccounts().then(value => {
          if (refreshGate.canPublish(accountsTicket)) {
            accounts = value.accounts;
            totalAccountsCount = value.totalCount;
            activeAccountId = value.activeAccountId;
          }
        }).catch(() => {}),
        api.getConfig().then(value => {
          if (value.config && refreshGate.canPublish(configTicket)) routerConfig = value.config;
        }).catch(() => {}),
        api.getSwitchStatus().then(value => {
          if (!refreshGate.canPublish(switchTicket)) return;
          const validated = validateSwitchStatusDto(value?.status);
          if (validated) {
            switchStatus = validated;
            isSafetyAuthoritative = true;
          } else {
            // Malformed, missing required fields, or unknown recovery-state: fail closed
            switchStatus = {
              currentState: 'UNKNOWN',
              quarantineActive: true,
              journalRecoveryState: 'UNKNOWN'
            };
            isSafetyAuthoritative = false;
          }
        }).catch(() => {
          if (!refreshGate.canPublish(switchTicket)) return;
          // Current request failed: fail closed
          switchStatus = {
            currentState: 'UNKNOWN',
            quarantineActive: true,
            journalRecoveryState: 'UNKNOWN'
          };
          isSafetyAuthoritative = false;
        })
      ]);

      if (refreshId === latestRefresh) lastPollTime = new Date().toLocaleTimeString();
    } finally {
      if (refreshId === latestRefresh) {
        isInitialLoading = false;
        isRefreshing = false;
      }
    }
  }

  // Handlers
  async function handleUpdateAlias(id: string, newAlias: string) {
    return updateAlias(id, newAlias, {
      refreshGate,
      updateAccountAlias: (id, alias) => api.updateAccountAlias(id, alias),
      publishAlias: (id, alias) => {
        accounts = accounts.map(a => (a.id === id ? { ...a, alias } : a));
      },
      logActivity,
      notifyError: message => { globalNotification = { type: 'error', message }; }
    });
  }

  function checkLifecycleMutationsAllowed(operationName: string): boolean {
    if (!lifecycleMutationsAllowed) {
      switchModalOpen = false;
      switchTargetAccount = null;
      deleteModalOpen = false;
      deleteTargetAccount = null;
      const message = 'Operation cancelled: safety quarantine is active or recovery is pending.';
      globalNotification = { type: 'error', message };
      logActivity(`${operationName} cancelled: safety quarantine is active or recovery is pending.`);
      return false;
    }
    return true;
  }

  async function handleSaveCurrentAccount() {
    if (!checkLifecycleMutationsAllowed('Session capture')) {
      return;
    }
    isSavingCurrent = true;
    refreshGate.beginMutation();
    try {
      const res = await api.enrollCurrentAccount();
      logActivity(res.message || `Captured running Antigravity 2 session for ${res.account.email}.`);
    } catch (err) {
      const msg = err instanceof Error ? err.message : 'Session capture failed';
      logActivity(`Session capture error: ${msg}`);
      globalNotification = { type: 'error', message: msg };
    } finally {
      refreshGate.endMutation();
      isSavingCurrent = false;
    }
    await refreshAll();
  }

  async function handleConnectAccount(data: ConnectAccountInput) {
    return connectAccount(data, {
      refreshGate,
      createAccount: data => api.createAccount(data),
      logActivity,
      notifyError: message => { globalNotification = { type: 'error', message }; },
      refreshAll
    });
  }

  function handleOpenExecuteSwitch(account: AccountMetadata) {
    switchTargetAccount = account;
    switchModalOpen = true;
  }

  async function handleConfirmSwitch(account: AccountMetadata) {
    if (!checkLifecycleMutationsAllowed('Switch')) {
      return;
    }
    isSwitching = true;
    try {
      const res = await executeDashboardSwitch(account.id, {
        client: api,
        refreshGate,
        invalidateSafety: () => {
          switchStatus = null;
          isSafetyAuthoritative = false;
        },
        refreshAll
      });
      logActivity(res.message || `Switched active account to ${account.email}.`);
    } finally {
      isSwitching = false;
    }
  }

  function handleOpenDelete(account: AccountMetadata) {
    deleteTargetAccount = account;
    deleteModalOpen = true;
  }

  async function handleConfirmDelete(account: AccountMetadata) {
    if (!checkLifecycleMutationsAllowed('Delete')) {
      return;
    }
    isDeleting = true;
    refreshGate.beginMutation();
    try {
      const res = await api.deleteAccount(account.id);
      if (res.vaultRecordDeleted) {
        logActivity(`Deleted account and wiped vaulted session (${account.email}).`);
      } else {
        logActivity(`Deleted account metadata (${account.email}).`);
      }
    } finally {
      refreshGate.endMutation();
      isDeleting = false;
    }
    await refreshAll();
  }

  async function handleSaveConfig(updated: RouterConfigUpdate) {
    refreshGate.beginMutation();
    try {
      const res = await api.saveConfig(updated);
      if (res.success && res.config) {
        routerConfig = res.config;
        logActivity(`Updated router configuration: AutoSwitch=${updated.autoSwitchEnabled}, Threshold=${updated.lowQuotaThresholdPercent}%`);
      } else {
        throw new Error('The server did not confirm that settings were saved.');
      }
    } finally {
      refreshGate.endMutation();
    }
    await refreshAll();
  }

  async function handleConfirmRecovery(): Promise<JournalResolutionResult> {
    isResolvingQuarantine = true;
    refreshGate.beginMutation();
    try {
      const result = await api.resolveQuarantine();
      logActivity(getRecoveryActivityLogMessage(result.status));
      return result;
    } catch {
      logActivity('Journal resolution failed due to an unexpected client or network error.');
      return {
        status: 'PersistenceFailure',
        message: 'A persistence failure occurred during journal resolution.',
        restartRequired: false,
        reasonCode: 'UNKNOWN'
      };
    } finally {
      refreshGate.endMutation();
      isResolvingQuarantine = false;
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

      {#if quarantineBanner}
        <div class="quarantine-banner {quarantineBanner.bannerType}" role="alert">
          <div class="quarantine-banner-content">
            <span class="quarantine-banner-icon" aria-hidden="true">
              {#if quarantineBanner.bannerType === 'warning'}
                ⚠️
              {:else if quarantineBanner.bannerType === 'error'}
                🚫
              {:else}
                ℹ️
              {/if}
            </span>
            <div class="quarantine-banner-text">
              <strong class="quarantine-banner-title">{quarantineBanner.title}</strong>
              <p class="quarantine-banner-message">{quarantineBanner.message}</p>
            </div>
          </div>
          {#if quarantineBanner.showActionButton}
            <button
              type="button"
              class="btn btn-warning btn-sm quarantine-action-btn"
              onclick={() => (isRecoveryModalOpen = true)}
            >
              {quarantineBanner.actionLabel || 'Resolve Journal...'}
            </button>
          {/if}
        </div>
      {/if}

      <div class="page-intro">
        <div>
          <span class="page-eyebrow">Command center</span>
          <h2>{activeTab === 'overview' ? 'Overview' : activeTab === 'accounts' ? 'Accounts' : activeTab === 'telemetry' ? 'Telemetry & quotas' : activeTab === 'routing' ? 'Settings' : 'Activity & safety'}</h2>
          <p>{activeTab === 'overview' ? 'Account health and model capacity at a glance.' : activeTab === 'accounts' ? 'Manage saved identities and account access.' : activeTab === 'telemetry' ? 'Inspect individual model observations and reset windows.' : activeTab === 'routing' ? 'Configure routing and switch thresholds.' : activeTab === 'usage' ? 'Provider-reported conversation token usage across detected Antigravity instances.' : 'Review recent dashboard activity and router state.'}</p>
        </div>
        <span class="sync-indicator"><span aria-hidden="true"></span>{isRefreshing ? 'Syncing' : `Last sync ${lastPollTime}`}</span>
      </div>

      {#if routerConfig?.autoSwitchEnabled && !routerConfig.workloadModelKey?.trim()}
        <div class="quarantine-banner warning" role="status">
          <p>Auto Switch is enabled but cannot operate until a workload model is configured.</p>
          <button type="button" class="btn btn-secondary btn-sm" onclick={() => (activeTab = 'routing')}>Configure workload model</button>
        </div>
      {/if}

      <!-- Summary metrics cards -->
      <SummaryCards
        totalAccounts={totalAccountsCount}
        activeAccountName={activeAccountDisplayName}
        {activeAccountSubtitle}
        {lowestQuota}
        {exhaustedCount}
        {unknownModelsCount}
        {activeModelsCount}
        {autoSwitchEnabled}
        {lowThresholdPercent}
      />

      <!-- Navigation Tabs -->
      <nav class="view-nav" aria-label="Dashboard Views">
        <button
          type="button"
          class="nav-tab {activeTab === 'overview' ? 'active' : ''}"
          aria-current={activeTab === 'overview' ? 'page' : undefined}
          onclick={() => (activeTab = 'overview')}
        >
          Overview
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'accounts' ? 'active' : ''}"
          aria-current={activeTab === 'accounts' ? 'page' : undefined}
          onclick={() => (activeTab = 'accounts')}
        >
          Accounts ({totalAccountsCount})
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'telemetry' ? 'active' : ''}"
          aria-current={activeTab === 'telemetry' ? 'page' : undefined}
          onclick={() => (activeTab = 'telemetry')}
        >
          Telemetry &amp; Quotas
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'routing' ? 'active' : ''}"
          aria-current={activeTab === 'routing' ? 'page' : undefined}
          onclick={() => (activeTab = 'routing')}
        >
          Settings
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'usage' ? 'active' : ''}"
          aria-current={activeTab === 'usage' ? 'page' : undefined}
          onclick={() => (activeTab = 'usage')}
        >
          Usage
        </button>
        <button
          type="button"
          class="nav-tab {activeTab === 'activity' ? 'active' : ''}"
          aria-current={activeTab === 'activity' ? 'page' : undefined}
          onclick={() => (activeTab = 'activity')}
        >
          Activity &amp; Safety
        </button>
      </nav>

      <!-- View Panels -->
      {#if activeTab === 'overview'}
        <ProviderOverview
          quota={quotaSnapshot}
          isAg2Connected={Boolean(status?.ag2?.connected)}
          {lowThresholdPercent}
          currentAccount={status?.telemetry?.currentAccount}
          onNavigateToTelemetry={() => (activeTab = 'telemetry')}
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
          isQuarantined={quarantineActive || !lifecycleMutationsAllowed}
          {lifecycleMutationsAllowed}
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
          isQuarantined={quarantineActive || !lifecycleMutationsAllowed}
          {lifecycleMutationsAllowed}
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
          quota={quotaSnapshot}
          {candidateEvidence}
          {accounts}
          routingReason={status?.router?.lastDecisionReason ?? null}
          onSaveConfig={handleSaveConfig}
        />
      {:else if activeTab === 'usage'}
        <UsageSection {accounts} />
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

<SwitchJournalRecoveryModal
  isOpen={isRecoveryModalOpen}
  isResolving={isResolvingQuarantine}
  {switchStatus}
  {isSafetyAuthoritative}
  onClose={() => (isRecoveryModalOpen = false)}
  onConfirm={handleConfirmRecovery}
/>

<style>
  .app-layout {
    display: flex;
    flex-direction: column;
    min-height: 100vh;
    background: var(--color-canvas);
  }

  .main-content {
    flex: 1;
    padding: 27px 34px 40px;
    max-width: 1440px;
    width: 100%;
    margin: 0 auto;
  }

  .page-intro { display: flex; align-items: end; justify-content: space-between; gap: var(--space-4); margin-bottom: 21px; }
  .page-eyebrow { color: var(--color-primary-text); font-size: 11px; font-weight: 700; letter-spacing: .09em; text-transform: uppercase; }
  .page-intro h2 { margin-top: 3px; font-size: 26px; font-weight: 720; line-height: 1.15; letter-spacing: -.045em; }
  .page-intro p { margin-top: 5px; color: var(--color-text-muted); font-size: 12px; }
  .sync-indicator { display: inline-flex; align-items: center; gap: 7px; color: var(--color-text-muted); font-size: 11px; white-space: nowrap; }
  .sync-indicator span { width: 6px; height: 6px; border-radius: 50%; background: var(--color-success); }

  .view-nav {
    display: flex;
    align-items: center;
    gap: var(--space-1);
    margin: 0 0 var(--space-5);
    padding: 4px;
    width: fit-content;
    max-width: 100%;
    overflow-x: auto;
    background: var(--color-surface-hover);
    border-radius: var(--radius-md);
  }

  .nav-tab {
    background: transparent;
    border: none;
    font-family: var(--font-sans);
    font-size: 12px;
    font-weight: 550;
    color: var(--color-text-secondary);
    padding: 8px 13px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    transition: all var(--transition-fast);
  }

  .nav-tab:hover {
    background-color: var(--color-surface-hover);
    color: var(--color-text-primary);
  }

  .nav-tab.active {
    background-color: var(--color-surface);
    color: var(--color-text-primary);
    font-weight: 700;
    box-shadow: var(--shadow-xs);
  }

  .app-footer {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: var(--space-3) 34px;
    background-color: transparent;
    border-top: 1px solid var(--color-divider);
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
    padding: 13px 16px;
    border-radius: var(--radius-md);
    margin-bottom: var(--space-4);
    font-size: 12.5px;
    font-weight: 550;
    gap: var(--space-3);
  }

  .notification-banner.error {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
    box-shadow: 0 3px 12px rgba(153, 27, 27, .06);
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

  .quarantine-banner {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 14px 18px;
    border-radius: var(--radius-md);
    margin-bottom: var(--space-4);
    gap: var(--space-4);
    box-shadow: var(--shadow-sm);
  }

  .quarantine-banner.warning {
    background-color: var(--color-warning-subtle);
    border: 1px solid var(--color-warning-border);
    color: var(--color-warning-text);
  }

  .quarantine-banner.error {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
  }

  .quarantine-banner.info {
    background-color: var(--color-primary-subtle);
    border: 1px solid var(--color-primary);
    color: var(--color-primary-text);
  }

  .quarantine-banner-content {
    display: flex;
    align-items: flex-start;
    gap: var(--space-3);
  }

  .quarantine-banner-icon {
    font-size: 18px;
    line-height: 1.2;
    flex-shrink: 0;
  }

  .quarantine-banner-text {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .quarantine-banner-title {
    font-size: 13px;
    font-weight: 700;
  }

  .quarantine-banner-message {
    font-size: 12px;
    margin: 0;
    line-height: 1.4;
    opacity: 0.95;
  }

  .quarantine-action-btn {
    flex-shrink: 0;
    background-color: var(--color-warning);
    color: #FFFFFF;
    border-color: var(--color-warning-text);
    font-weight: 600;
  }

  .quarantine-action-btn:hover {
    background-color: var(--color-warning-text);
  }

  @media (max-width: 720px) {
    .main-content { padding: 20px 16px 30px; }
    .page-intro { align-items: start; }
    .sync-indicator { display: none; }
    .view-nav { width: 100%; }
    .nav-tab { flex: 0 0 auto; }
    .app-footer { padding: 12px 16px; }
    .footer-poll { display: none; }
  }
</style>
