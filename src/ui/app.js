/**
 * AG2 Router - Minimal Dashboard Client Application
 *
 * Lightweight vanilla JavaScript client.
 * Polls loopback API endpoints and renders truthful application states.
 */

(() => {
  // State
  let currentStatus = null;
  let accountsList = [];
  let currentConfig = null;
  const activityLogs = [];

  // DOM Elements
  const statusDot = document.getElementById('status-dot');
  const statusText = document.getElementById('status-text');
  const metricTotalQuota = document.getElementById('metric-total-quota');
  const metricActiveAccount = document.getElementById('metric-active-account');
  const metricSwitchState = document.getElementById('metric-switch-state');
  const metricThreshold = document.getElementById('metric-threshold');
  const gateStateHint = document.getElementById('gate-state-hint');
  const footerLastPoll = document.getElementById('footer-last-poll');

  const accountsContainer = document.getElementById('accounts-container');
  const accountsEmpty = document.getElementById('accounts-empty');
  const accountsTable = document.getElementById('accounts-table');
  const accountsTbody = document.getElementById('accounts-tbody');

  const gateStateBadge = document.getElementById('gate-state-badge');
  const gateDecisionText = document.getElementById('gate-decision-text');
  const activityLogList = document.getElementById('activity-log-list');

  const addAccountModal = document.getElementById('add-account-modal');
  const addAccountForm = document.getElementById('add-account-form');
  const btnOpenAddAccount = document.getElementById('btn-open-add-account');
  const btnEmptyAddAccount = document.getElementById('btn-empty-add-account');
  const btnCloseModal = document.getElementById('btn-close-modal');
  const btnCancelModal = document.getElementById('btn-cancel-modal');

  const settingsForm = document.getElementById('settings-form');
  const cfgAutoSwitch = document.getElementById('cfg-auto-switch');
  const cfgLowThreshold = document.getElementById('cfg-low-threshold');
  const cfgMinCandidate = document.getElementById('cfg-min-candidate');
  const cfgPollingInterval = document.getElementById('cfg-polling-interval');
  const saveSettingsStatus = document.getElementById('save-settings-status');

  const telemetryOfflineNotice = document.getElementById('telemetry-offline-notice');
  const telemetryContent = document.getElementById('telemetry-content');
  const telemetryEmail = document.getElementById('telemetry-email');
  const telemetryTier = document.getElementById('telemetry-tier');
  const telemetryCreditsRow = document.getElementById('telemetry-credits-row');
  const telemetryModelsTbody = document.getElementById('telemetry-models-tbody');
  const telemetrySyncTime = document.getElementById('telemetry-sync-time');

  // Add initial log entry
  logActivity('Dashboard initialized. Polling loopback API.');

  function logActivity(message) {
    const timestamp = new Date().toLocaleTimeString();
    activityLogs.unshift({ timestamp, message });
    if (activityLogs.length > 50) {
      activityLogs.pop();
    }
    renderActivityLog();
  }

  function renderActivityLog() {
    if (!activityLogList) return;
    activityLogList.innerHTML = activityLogs
      .map(
        (log) => `
        <li class="activity-item">
          <span class="activity-timestamp">${escapeHtml(log.timestamp)}</span>
          <span class="activity-message">${escapeHtml(log.message)}</span>
        </li>
      `
      )
      .join('');
  }

  function escapeHtml(str) {
    if (typeof str !== 'string') return '';
    return str
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }

  // API Call Helpers
  async function fetchStatus() {
    try {
      const res = await fetch('/api/status');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      return await res.json();
    } catch (err) {
      console.warn('Failed to fetch /api/status:', err);
      return null;
    }
  }

  async function fetchAccounts() {
    try {
      const res = await fetch('/api/accounts');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      return data.accounts || [];
    } catch (err) {
      console.warn('Failed to fetch /api/accounts:', err);
      return [];
    }
  }

  async function fetchConfig() {
    try {
      const res = await fetch('/api/config');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      return data.config || null;
    } catch (err) {
      console.warn('Failed to fetch /api/config:', err);
      return null;
    }
  }

  // UI Render Updates
  function updateStatusUI(status) {
    if (!status) {
      statusDot.className = 'status-dot status-offline';
      statusText.textContent = 'Server disconnected';
      return;
    }

    currentStatus = status;

    // AG2 Process State & Activity
    const ag2 = status.ag2 || {};
    const ag2Activity = ag2.activity;
    if (ag2.connected) {
      statusDot.className = 'status-dot status-online';
      if (ag2Activity) {
        const runCount = ag2Activity.runningTrajectories ?? 0;
        const actState = ag2Activity.state || 'IDLE';
        statusText.textContent = `${ag2.message || 'Antigravity 2 Connected'} • ${actState}${runCount > 0 ? ` (${runCount} running)` : ''}`;
      } else {
        statusText.textContent = ag2.message || 'Antigravity 2 Connected';
      }
    } else {
      statusDot.className = 'status-dot status-offline';
      statusText.textContent = ag2.message || 'Waiting for Antigravity 2';
    }

    // Telemetry & Metrics
    const telemetry = status.telemetry || {};
    const quota = telemetry.quota;
    const quotaBadge = document.getElementById('quota-badge');
    const quotaHint = document.getElementById('quota-hint');

    if (quota && quota.models && quota.models.length > 0) {
      const activeCount = quota.models.filter((m) => !m.isExhausted).length;
      metricTotalQuota.textContent = `${activeCount}/${quota.models.length}`;
      if (quotaBadge) {
        quotaBadge.textContent = 'Pools Segregated';
        quotaBadge.className = 'metric-badge badge-neutral';
      }
      if (quotaHint) {
        if (quota.promptCredits) {
          quotaHint.textContent = `Prompt: ${quota.promptCredits.availableCredits.toLocaleString()} cr • Flow: ${quota.flowCredits?.availableCredits.toLocaleString() || 0} cr`;
        } else {
          quotaHint.textContent = 'Model-specific quotas preserved separately';
        }
      }
    } else {
      metricTotalQuota.textContent = '--%';
      if (quotaBadge) {
        quotaBadge.textContent = 'Telemetry Offline';
        quotaBadge.className = 'metric-badge badge-neutral';
      }
      if (quotaHint) {
        quotaHint.textContent = 'Waiting for Antigravity 2 telemetry';
      }
    }

    const routerInfo = status.router || {};
    const currentAccount = telemetry.currentAccount;
    const accountHint = document.getElementById('account-hint');

    if (currentAccount && currentAccount.email) {
      const tierLabel = currentAccount.tierName ? ` (${currentAccount.tierName})` : '';
      metricActiveAccount.textContent = `${currentAccount.email}${tierLabel}`;
      if (accountHint) {
        accountHint.textContent = `Antigravity 2 Active${currentAccount.name ? ' • ' + currentAccount.name : ''}`;
      }
    } else if (routerInfo.activeAccountEmail) {
      metricActiveAccount.textContent = routerInfo.activeAccountEmail;
      if (accountHint) accountHint.textContent = 'Configured in account store';
    } else {
      metricActiveAccount.textContent = 'No account connected';
      if (accountHint) accountHint.textContent = 'No active Antigravity 2 session detected';
    }

    // Auto-switch & Threshold
    const isAuto = routerInfo.autoSwitchEnabled;
    metricSwitchState.textContent = isAuto ? 'ON' : 'OFF';
    metricSwitchState.className = isAuto ? 'metric-switch-state active' : 'metric-switch-state';

    const threshold = routerInfo.config?.lowQuotaThresholdPercent ?? 15;
    metricThreshold.textContent = `Switch below ${threshold}%`;

    // Safety Gate
    const gateState = routerInfo.state || 'IDLE';
    gateStateHint.textContent = `Safety gate: ${gateState}`;
    if (gateStateBadge) gateStateBadge.textContent = gateState;
    if (gateDecisionText) gateDecisionText.textContent = routerInfo.lastDecisionReason || 'No routing activity recorded.';

    if (footerLastPoll) {
      footerLastPoll.textContent = `Last poll: ${new Date().toLocaleTimeString()}`;
    }

    // Live Telemetry View Rendering
    if (telemetrySyncTime) {
      const syncTs = telemetry.lastSuccessfulTelemetry || quota?.timestamp;
      telemetrySyncTime.textContent = syncTs ? `Last sync: ${new Date(syncTs).toLocaleTimeString()}` : 'Last sync: Never';
    }

    if (quota && currentAccount) {
      if (telemetryOfflineNotice) telemetryOfflineNotice.classList.add('hidden');
      if (telemetryContent) telemetryContent.classList.remove('hidden');

      if (telemetryEmail) telemetryEmail.textContent = currentAccount.email;
      if (telemetryTier) telemetryTier.textContent = currentAccount.tierName || currentAccount.tierId || 'Pro';

      if (telemetryCreditsRow) {
        let creditHtml = '';
        if (quota.promptCredits) {
          creditHtml += `<span>Prompt Credits: <strong>${quota.promptCredits.availableCredits.toLocaleString()}</strong> / ${quota.promptCredits.monthlyCredits.toLocaleString()}</span>`;
        }
        if (quota.flowCredits) {
          creditHtml += `<span style="margin-left: 16px;">Flow Credits: <strong>${quota.flowCredits.availableCredits.toLocaleString()}</strong> / ${quota.flowCredits.monthlyCredits.toLocaleString()}</span>`;
        }
        telemetryCreditsRow.innerHTML = creditHtml;
      }

      if (telemetryModelsTbody && quota.models) {
        telemetryModelsTbody.innerHTML = quota.models
          .map((m) => {
            const pct = Math.round(m.remainingFraction * 100);
            const statusBadge = m.isExhausted
              ? '<span class="status-badge badge-exhausted" style="color: var(--danger); font-size: 11px; font-weight: 600;">EXHAUSTED</span>'
              : '<span class="status-badge badge-healthy" style="color: var(--success); font-size: 11px; font-weight: 600;">HEALTHY</span>';

            const resetStr = m.resetTime ? new Date(m.resetTime).toLocaleTimeString() : 'N/A';

            return `
              <tr>
                <td><strong>${escapeHtml(m.label)}</strong></td>
                <td>
                  <div style="display: flex; align-items: center; gap: 8px;">
                    <span style="font-family: var(--font-mono);">${pct}%</span>
                    <div style="background: var(--border-subtle); height: 6px; width: 60px; border-radius: 3px; overflow: hidden;">
                      <div style="background: ${pct > 20 ? 'var(--success)' : 'var(--danger)'}; height: 100%; width: ${pct}%;"></div>
                    </div>
                  </div>
                </td>
                <td style="color: var(--text-muted); font-size: 12px;">${escapeHtml(resetStr)}</td>
                <td>${statusBadge}</td>
              </tr>
            `;
          })
          .join('');
      }
    } else {
      if (telemetryOfflineNotice) telemetryOfflineNotice.classList.remove('hidden');
      if (telemetryContent) telemetryContent.classList.add('hidden');
    }
  }

  function updateAccountsUI(accounts) {
    accountsList = accounts;

    if (!accounts || accounts.length === 0) {
      accountsEmpty.classList.remove('hidden');
      accountsTable.classList.add('hidden');
      return;
    }

    accountsEmpty.classList.add('hidden');
    accountsTable.classList.remove('hidden');

    accountsTbody.innerHTML = accounts
      .map((acc) => {
        const activeBadge = acc.isActive ? '<span class="account-active-badge">ACTIVE</span>' : '';
        const roleBadge = acc.isReserve
          ? '<span class="reserve-badge">Reserve</span>'
          : '<span class="metric-badge badge-neutral">Standard</span>';
        const vaultBadge = acc.hasVaultedSession
          ? '<span class="metric-badge badge-healthy" style="color: var(--success); font-weight: 600; font-size: 11px;">🔒 Vaulted</span>'
          : '<span class="metric-badge badge-neutral" style="color: var(--text-muted); font-size: 11px;">Metadata Only</span>';

        let quotaDisplay = '<span style="color: var(--text-muted);" title="Inactive: Quota is inspected live when active in Antigravity 2">--% (Inactive)</span>';
        if (acc.isActive && currentStatus && currentStatus.telemetry && currentStatus.telemetry.quota) {
          const mList = currentStatus.telemetry.quota.models || [];
          const healthy = mList.filter((m) => !m.isExhausted).length;
          quotaDisplay = `<span style="color: var(--success); font-weight: 500;">${healthy}/${mList.length} models</span>`;
        }

        let switchAction = '';
        if (acc.isActive) {
          switchAction = '<span class="account-active-badge" style="font-size: 11px; padding: 3px 8px;">ACTIVE</span>';
        } else if (acc.hasVaultedSession) {
          switchAction = `<button type="button" class="btn btn-secondary btn-plan-switch" data-id="${escapeHtml(acc.id)}" data-email="${escapeHtml(acc.email)}" style="font-size: 11px; padding: 3px 8px;">Plan Switch</button>`;
        } else {
          switchAction = `<button type="button" class="btn btn-secondary btn-plan-switch" data-id="${escapeHtml(acc.id)}" data-email="${escapeHtml(acc.email)}" style="font-size: 11px; padding: 3px 8px; opacity: 0.8;" title="Evaluate switch readiness">Check</button>`;
        }

        const deleteAction = `<button type="button" class="btn btn-danger btn-remove-acc" data-id="${escapeHtml(acc.id)}" data-email="${escapeHtml(acc.email)}" data-vaulted="${Boolean(acc.hasVaultedSession)}" style="font-size: 11px; padding: 3px 8px;">Delete</button>`;

        return `
          <tr>
            <td>
              <div>
                <strong>${escapeHtml(acc.email)}</strong> ${activeBadge}
                ${acc.name ? `<div style="font-size: 11px; color: var(--text-muted);">${escapeHtml(acc.name)}</div>` : ''}
              </div>
            </td>
            <td>Priority ${escapeHtml(String(acc.priority))}</td>
            <td>${roleBadge}</td>
            <td>${vaultBadge}</td>
            <td>${escapeHtml(acc.validationStatus || 'UNVALIDATED')}</td>
            <td>${quotaDisplay}</td>
            <td class="actions-col" style="white-space: nowrap;">
              <div style="display: flex; gap: 6px; align-items: center; justify-content: flex-end;">
                ${switchAction}
                ${deleteAction}
              </div>
            </td>
          </tr>
        `;
      })
      .join('');

    // Bind Plan Switch buttons
    document.querySelectorAll('.btn-plan-switch').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        const id = e.currentTarget.getAttribute('data-id');
        const email = e.currentTarget.getAttribute('data-email');
        if (id && email) {
          openSwitchPlanModal(id, email);
        }
      });
    });

    // Bind Delete buttons
    document.querySelectorAll('.btn-remove-acc').forEach((btn) => {
      btn.addEventListener('click', (e) => {
        const id = e.currentTarget.getAttribute('data-id');
        const email = e.currentTarget.getAttribute('data-email');
        const isVaulted = e.currentTarget.getAttribute('data-vaulted') === 'true';
        if (id && email) {
          openDeleteModal(id, email, isVaulted);
        }
      });
    });
  }

  function updateSettingsUI(config) {
    if (!config) return;
    currentConfig = config;

    if (cfgAutoSwitch) cfgAutoSwitch.checked = Boolean(config.autoSwitchEnabled);
    if (cfgLowThreshold) cfgLowThreshold.value = config.lowQuotaThresholdPercent;
    if (cfgMinCandidate) cfgMinCandidate.value = config.minimumCandidateQuotaPercent;
    if (cfgPollingInterval) cfgPollingInterval.value = Math.round(config.pollingIntervalMs / 1000);
  }

  // Poll routine
  async function refreshAll() {
    const [status, accounts, config] = await Promise.all([
      fetchStatus(),
      fetchAccounts(),
      fetchConfig()
    ]);
    updateStatusUI(status);
    updateAccountsUI(accounts);
    if (config && !currentConfig) {
      updateSettingsUI(config);
    }
  }

  // Navigation tabs
  document.querySelectorAll('.nav-btn').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.nav-btn').forEach((b) => b.classList.remove('active'));
      document.querySelectorAll('.view-panel').forEach((p) => p.classList.add('hidden'));

      btn.classList.add('active');
      const viewId = btn.getAttribute('data-view');
      const targetPanel = document.getElementById(`view-${viewId}`);
      if (targetPanel) {
        targetPanel.classList.remove('hidden');
      }
    });
  });

  // Modal interactions
  function openModal() {
    addAccountModal.classList.remove('hidden');
    document.getElementById('acc-email')?.focus();
  }

  function closeModal() {
    addAccountModal.classList.add('hidden');
    addAccountForm.reset();
  }

  if (btnOpenAddAccount) btnOpenAddAccount.addEventListener('click', openModal);
  if (btnEmptyAddAccount) btnEmptyAddAccount.addEventListener('click', openModal);
  if (btnCloseModal) btnCloseModal.addEventListener('click', closeModal);
  if (btnCancelModal) btnCancelModal.addEventListener('click', closeModal);

  // Save current active Antigravity account into local vault
  async function handleSaveCurrentAccount() {
    const btn = document.getElementById('btn-save-current-account');
    const emptyBtn = document.getElementById('btn-empty-save-current');
    const originalText = btn ? btn.textContent : '';
    if (btn) {
      btn.disabled = true;
      btn.textContent = 'Saving session...';
    }
    if (emptyBtn) {
      emptyBtn.disabled = true;
      emptyBtn.textContent = 'Saving session...';
    }

    try {
      const res = await fetch('/api/accounts/enroll-current', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({})
      });
      const data = await res.json();
      if (!res.ok) {
        throw new Error(data.error || 'Failed to save current account');
      }
      logActivity(data.message || `Saved account ${data.account?.email} into encrypted session vault.`);
      await refreshAll();
    } catch (err) {
      alert('Failed to save current account: ' + err.message);
      logActivity(`Enrollment failed: ${err.message}`);
    } finally {
      if (btn) {
        btn.disabled = false;
        btn.textContent = originalText;
      }
      if (emptyBtn) {
        emptyBtn.disabled = false;
        emptyBtn.textContent = '🔒 Save Current AG2 Account';
      }
    }
  }

  const btnSaveCurrentAccount = document.getElementById('btn-save-current-account');
  const btnEmptySaveCurrent = document.getElementById('btn-empty-save-current');
  if (btnSaveCurrentAccount) btnSaveCurrentAccount.addEventListener('click', handleSaveCurrentAccount);
  if (btnEmptySaveCurrent) btnEmptySaveCurrent.addEventListener('click', handleSaveCurrentAccount);

  // Switch Readiness Modal
  const switchPlanModal = document.getElementById('switch-plan-modal');
  const planTargetEmail = document.getElementById('plan-target-email');
  const planOverallBadge = document.getElementById('plan-overall-badge');
  const planChecklist = document.getElementById('plan-checklist');
  const btnCloseSwitchModal = document.getElementById('btn-close-switch-modal');
  const btnCloseSwitchPlan = document.getElementById('btn-close-switch-plan');

  async function openSwitchPlanModal(id, email) {
    if (!switchPlanModal) return;
    switchPlanModal.classList.remove('hidden');
    if (planTargetEmail) planTargetEmail.textContent = email;
    if (planOverallBadge) {
      planOverallBadge.className = 'metric-badge badge-neutral';
      planOverallBadge.textContent = 'Evaluating Preflight...';
      planOverallBadge.style.color = '';
    }
    if (planChecklist) {
      planChecklist.innerHTML = '<div style="padding: 12px; color: var(--text-muted); font-size: 12px;">Evaluating live readiness checks against Antigravity 2...</div>';
    }

    try {
      const res = await fetch(`/api/accounts/${encodeURIComponent(id)}/switch-plan`, { method: 'POST' });
      const data = await res.json();

      if (!res.ok || !data.plan) {
        throw new Error(data.error || 'Failed to generate switch plan');
      }

      const plan = data.plan;

      if (planOverallBadge) {
        if (plan.ready) {
          planOverallBadge.className = 'metric-badge badge-healthy';
          planOverallBadge.textContent = 'READY FOR SWITCH';
          planOverallBadge.style.color = 'var(--success)';
        } else {
          planOverallBadge.className = 'metric-badge badge-exhausted';
          planOverallBadge.style.color = 'var(--danger)';
          planOverallBadge.textContent = `BLOCKED (${plan.blockers.length} issue${plan.blockers.length === 1 ? '' : 's'})`;
        }
      }

      const checkLabels = {
        TARGET_ACCOUNT_EXISTS: 'Target Account Storage',
        TARGET_HAS_VAULTED_SESSION: 'Encrypted Session Vault',
        TARGET_NOT_ALREADY_ACTIVE: 'Active Account Exclusivity',
        AG2_ACTIVITY_IS_IDLE: 'Antigravity 2 Activity & Idle State',
        ROLLBACK_SNAPSHOT_READABLE: 'Rollback Snapshot Integrity',
        LAUNCH_SPEC_CAPTURABLE: 'Process Launch Specification'
      };

      if (planChecklist) {
        planChecklist.innerHTML = (plan.checks || [])
          .map((c) => {
            const icon = c.passed
              ? '<span class="check-icon-pass">✓</span>'
              : '<span class="check-icon-fail">✕</span>';
            const title = checkLabels[c.code] || c.code;
            return `
              <div class="plan-check-item">
                <div style="width: 20px; text-align: center;">${icon}</div>
                <div class="plan-check-text">
                  <strong>${escapeHtml(title)}</strong>
                  <div>${escapeHtml(c.message)}</div>
                </div>
              </div>
            `;
          })
          .join('');
      }

      logActivity(`Evaluated switch readiness for ${email}: ${plan.ready ? 'READY' : 'BLOCKED'}`);
    } catch (err) {
      if (planOverallBadge) {
        planOverallBadge.className = 'metric-badge badge-exhausted';
        planOverallBadge.style.color = 'var(--danger)';
        planOverallBadge.textContent = 'EVALUATION ERROR';
      }
      if (planChecklist) {
        planChecklist.innerHTML = `<div style="padding: 12px; color: var(--danger); font-size: 12px;">Error: ${escapeHtml(err.message)}</div>`;
      }
    }
  }

  function closeSwitchPlanModal() {
    if (switchPlanModal) switchPlanModal.classList.add('hidden');
  }

  if (btnCloseSwitchModal) btnCloseSwitchModal.addEventListener('click', closeSwitchPlanModal);
  if (btnCloseSwitchPlan) btnCloseSwitchPlan.addEventListener('click', closeSwitchPlanModal);
  if (switchPlanModal) {
    switchPlanModal.addEventListener('click', (e) => {
      if (e.target === switchPlanModal) closeSwitchPlanModal();
    });
  }

  // Account Deletion Modal
  const deleteAccountModal = document.getElementById('delete-account-modal');
  const deleteModalPrompt = document.getElementById('delete-modal-prompt');
  const deleteVaultWarning = document.getElementById('delete-vault-warning');
  const btnCancelDelete = document.getElementById('btn-cancel-delete');
  const btnConfirmDelete = document.getElementById('btn-confirm-delete');
  const btnCloseDeleteModal = document.getElementById('btn-close-delete-modal');

  let pendingDelete = null;

  function openDeleteModal(id, email, isVaulted) {
    if (!deleteAccountModal) return;
    pendingDelete = { id, email, isVaulted };
    if (deleteModalPrompt) {
      deleteModalPrompt.innerHTML = `Are you sure you want to remove account <strong>${escapeHtml(email)}</strong>?`;
    }
    if (deleteVaultWarning) {
      if (isVaulted) {
        deleteVaultWarning.classList.remove('hidden');
      } else {
        deleteVaultWarning.classList.add('hidden');
      }
    }
    deleteAccountModal.classList.remove('hidden');
  }

  function closeDeleteModal() {
    pendingDelete = null;
    if (deleteAccountModal) deleteAccountModal.classList.add('hidden');
  }

  async function handleConfirmDelete() {
    if (!pendingDelete) return;
    const { id, email } = pendingDelete;
    const btn = btnConfirmDelete;
    if (btn) {
      btn.disabled = true;
      btn.textContent = 'Deleting...';
    }

    try {
      const res = await fetch(`/api/accounts/${encodeURIComponent(id)}`, { method: 'DELETE' });
      const data = await res.json();
      if (!res.ok) {
        throw new Error(data.error || 'Delete failed');
      }

      if (data.vaultRecordDeleted) {
        logActivity(`Deleted account and permanently destroyed vaulted credentials (${email}).`);
      } else {
        logActivity(`Deleted account metadata (${email}).`);
      }

      closeDeleteModal();
      await refreshAll();
    } catch (err) {
      alert('Failed to delete account: ' + err.message);
    } finally {
      if (btn) {
        btn.disabled = false;
        btn.textContent = 'Confirm Delete';
      }
    }
  }

  if (btnCloseDeleteModal) btnCloseDeleteModal.addEventListener('click', closeDeleteModal);
  if (btnCancelDelete) btnCancelDelete.addEventListener('click', closeDeleteModal);
  if (btnConfirmDelete) btnConfirmDelete.addEventListener('click', handleConfirmDelete);
  if (deleteAccountModal) {
    deleteAccountModal.addEventListener('click', (e) => {
      if (e.target === deleteAccountModal) closeDeleteModal();
    });
  }

  if (addAccountModal) {
    addAccountModal.addEventListener('click', (e) => {
      if (e.target === addAccountModal) closeModal();
    });
  }

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      if (addAccountModal && !addAccountModal.classList.contains('hidden')) closeModal();
      if (switchPlanModal && !switchPlanModal.classList.contains('hidden')) closeSwitchPlanModal();
      if (deleteAccountModal && !deleteAccountModal.classList.contains('hidden')) closeDeleteModal();
    }
  });

  // Add Account form submission
  if (addAccountForm) {
    addAccountForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const email = document.getElementById('acc-email').value.trim();
      const name = document.getElementById('acc-name').value.trim() || undefined;
      const priority = parseInt(document.getElementById('acc-priority').value, 10) || 1;
      const isReserve = document.getElementById('acc-reserve').checked;

      try {
        const res = await fetch('/api/accounts', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ email, name, priority, isReserve })
        });

        const data = await res.json();
        if (!res.ok) {
          throw new Error(data.error || 'Failed to save account');
        }

        logActivity(`Added account metadata for ${email}.`);
        closeModal();
        await refreshAll();
      } catch (err) {
        alert('Error: ' + err.message);
      }
    });
  }

  // Settings form submission
  if (settingsForm) {
    settingsForm.addEventListener('submit', async (e) => {
      e.preventDefault();
      const autoSwitchEnabled = cfgAutoSwitch.checked;
      const lowQuotaThresholdPercent = parseInt(cfgLowThreshold.value, 10);
      const minimumCandidateQuotaPercent = parseInt(cfgMinCandidate.value, 10);
      const pollingIntervalMs = parseInt(cfgPollingInterval.value, 10) * 1000;

      try {
        const res = await fetch('/api/config', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            autoSwitchEnabled,
            lowQuotaThresholdPercent,
            minimumCandidateQuotaPercent,
            pollingIntervalMs
          })
        });

        const data = await res.json();
        if (!res.ok) throw new Error(data.error || 'Failed to update settings');

        updateSettingsUI(data.config);
        logActivity(`Updated router configuration: AutoSwitch=${autoSwitchEnabled}`);

        if (saveSettingsStatus) {
          saveSettingsStatus.textContent = 'Settings saved successfully.';
          setTimeout(() => {
            saveSettingsStatus.textContent = '';
          }, 3000);
        }

        await refreshAll();
      } catch (err) {
        alert('Failed to save settings: ' + err.message);
      }
    });
  }

  // Initial load & periodic polling
  refreshAll();
  setInterval(refreshAll, 5000);
})();
