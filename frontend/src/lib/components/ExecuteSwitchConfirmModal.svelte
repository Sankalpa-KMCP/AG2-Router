<script lang="ts">
  /**
   * ExecuteSwitchConfirmModal Component
   *
   * Two-step manual switch confirmation dialog.
   * Ensures intentionality before initiating an account switch:
   * - Informs the user that Antigravity will be briefly stopped and restarted with
   *   the target account credentials.
   * - Performs a dry-run switch plan check (ISwitchPlanner / POST /api/accounts/{id}/switch-plan)
   *   to verify admissibility before confirmation.
   * - Invokes onConfirm to execute the switch via the ApiClient.
   * - Maps failures cleanly via getSwitchErrorMessage without exposing raw stack traces.
   */
  import type { AccountMetadata, SwitchPlanResultDto } from '../api/types.js';
  import { resolveAccountDisplayName } from '../utils/helpers.js';
  import { getSwitchErrorMessage } from '../utils/switching.js';

  interface Props {
    isOpen: boolean;
    account: AccountMetadata | null;
    isSwitching?: boolean;
    onClose: () => void;
    onConfirm: (account: AccountMetadata) => Promise<void>;
    onGetPlan?: (id: string) => Promise<SwitchPlanResultDto>;
  }

  let {
    isOpen = false,
    account = null,
    isSwitching = false,
    onClose,
    onConfirm,
    onGetPlan
  }: Props = $props();

  let error = $state<string | null>(null);
  let plan = $state<SwitchPlanResultDto | null>(null);
  let isLoadingPlan = $state<boolean>(false);
  let planError = $state<string | null>(null);

  $effect(() => {
    if (isOpen && account && onGetPlan) {
      let cancelled = false;
      isLoadingPlan = true;
      plan = null;
      planError = null;
      error = null;

      onGetPlan(account.id)
        .then((result) => {
          if (!cancelled) {
            plan = result;
            isLoadingPlan = false;
          }
        })
        .catch((err) => {
          if (!cancelled) {
            planError = err instanceof Error ? err.message : String(err);
            isLoadingPlan = false;
          }
        });

      return () => {
        cancelled = true;
      };
    } else if (!isOpen) {
      plan = null;
      planError = null;
      isLoadingPlan = false;
      error = null;
    }
  });

  let canConfirm = $derived(
    !isSwitching &&
    !isLoadingPlan &&
    (onGetPlan ? Boolean(plan?.admissible) : true)
  );

  function handleKeyDown(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
  }

  async function handleConfirm() {
    if (!account || !canConfirm) return;
    error = null;
    try {
      await onConfirm(account);
      onClose();
    } catch (err) {
      error = getSwitchErrorMessage(err);
    }
  }
</script>

{#if isOpen && account}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <div
    class="modal-backdrop"
    role="dialog"
    aria-modal="true"
    aria-labelledby="modal-switch-confirm-title"
    tabindex="-1"
    onkeydown={handleKeyDown}
    onclick={(e) => { if (e.target === e.currentTarget) onClose(); }}
  >
    <div class="modal-dialog">
      <div class="modal-header">
        <h3 class="modal-title" id="modal-switch-confirm-title">Switch Active Account</h3>
        <button
          type="button"
          class="btn-icon close-btn"
          onclick={onClose}
          aria-label="Close dialog"
        >
          ✕
        </button>
      </div>

      <div class="modal-body">
        {#if error}
          <div class="switch-error-banner" role="alert">
            {error}
          </div>
        {/if}

        <p class="switch-prompt">
          Switch the active Antigravity 2 session to
          <strong>{resolveAccountDisplayName(account)}</strong>
          ({account.email})?
        </p>

        {#if onGetPlan}
          <div class="switch-plan-container">
            {#if isLoadingPlan}
              <div class="plan-status-card plan-loading" role="status">
                <span class="plan-spinner" aria-hidden="true"></span>
                <span>Evaluating switch safety &amp; quota admissibility (dry run)...</span>
              </div>
            {:else if planError}
              <div class="plan-status-card plan-error" role="alert">
                <div class="plan-card-header">
                  <span class="plan-badge danger">Plan Error</span>
                  <span class="plan-card-title">Pre-Flight Check Failed</span>
                </div>
                <p class="plan-card-message">{planError}</p>
              </div>
            {:else if plan}
              {#if plan.admissible}
                {#if plan.quotaStatus === 'EXHAUSTED' || plan.quotaStatus === 'BELOW_MINIMUM'}
                  <div class="plan-status-card plan-warning" role="status">
                    <div class="plan-card-header">
                      <span class="plan-badge warning">Caution</span>
                      <span class="plan-card-title">Pre-Flight Passed with Quota Warning</span>
                    </div>
                    <p class="plan-card-message">{plan.message}</p>
                    <div class="plan-meta-grid">
                      <div class="plan-meta-item">
                        <span class="plan-meta-label">Session:</span>
                        <span class="plan-meta-value">{plan.hasVaultedSession ? 'Vaulted' : 'Not Vaulted'}</span>
                      </div>
                      <div class="plan-meta-item">
                        <span class="plan-meta-label">Safety Gate:</span>
                        <span class="plan-meta-value">{plan.safetyState}</span>
                      </div>
                      <div class="plan-meta-item">
                        <span class="plan-meta-label">Antigravity:</span>
                        <span class="plan-meta-value">{plan.systemState}</span>
                      </div>
                      {#if plan.targetQuotaPercent != null}
                        <div class="plan-meta-item">
                          <span class="plan-meta-label">Target Quota:</span>
                          <span class="plan-meta-value">{plan.targetQuotaPercent.toFixed(0)}%{plan.workloadModelKey ? ` (${plan.workloadModelKey})` : ''}</span>
                        </div>
                      {/if}
                    </div>
                  </div>
                {:else}
                  <div class="plan-status-card plan-admissible" role="status">
                    <div class="plan-card-header">
                      <span class="plan-badge success">Admissible</span>
                      <span class="plan-card-title">Pre-Flight Check Passed</span>
                    </div>
                    <p class="plan-card-message">{plan.message}</p>
                    <div class="plan-meta-grid">
                      <div class="plan-meta-item">
                        <span class="plan-meta-label">Session:</span>
                        <span class="plan-meta-value">{plan.hasVaultedSession ? 'Vaulted' : 'Not Vaulted'}</span>
                      </div>
                      <div class="plan-meta-item">
                        <span class="plan-meta-label">Safety Gate:</span>
                        <span class="plan-meta-value">{plan.safetyState}</span>
                      </div>
                      <div class="plan-meta-item">
                        <span class="plan-meta-label">Antigravity:</span>
                        <span class="plan-meta-value">{plan.systemState}</span>
                      </div>
                      {#if plan.targetQuotaPercent != null}
                        <div class="plan-meta-item">
                          <span class="plan-meta-label">Target Quota:</span>
                          <span class="plan-meta-value">{plan.targetQuotaPercent.toFixed(0)}%{plan.workloadModelKey ? ` (${plan.workloadModelKey})` : ''}</span>
                        </div>
                      {/if}
                    </div>
                  </div>
                {/if}
              {:else}
                <div class="plan-status-card plan-inadmissible" role="alert">
                  <div class="plan-card-header">
                    <span class="plan-badge danger">Inadmissible</span>
                    <span class="plan-card-title">{plan.reasonCode}</span>
                  </div>
                  <p class="plan-card-message">{plan.message}</p>
                  <div class="plan-meta-grid">
                    <div class="plan-meta-item">
                      <span class="plan-meta-label">Session Vaulted:</span>
                      <span class="plan-meta-value">{plan.hasVaultedSession ? 'Yes' : 'No'}</span>
                    </div>
                    <div class="plan-meta-item">
                      <span class="plan-meta-label">Safety Gate:</span>
                      <span class="plan-meta-value">{plan.safetyState}</span>
                    </div>
                    <div class="plan-meta-item">
                      <span class="plan-meta-label">Antigravity:</span>
                      <span class="plan-meta-value">{plan.systemState}</span>
                    </div>
                    {#if plan.inCooldown}
                      <div class="plan-meta-item">
                        <span class="plan-meta-label">Cooldown:</span>
                        <span class="plan-meta-value">Active</span>
                      </div>
                    {/if}
                    {#if plan.targetQuotaPercent != null}
                      <div class="plan-meta-item">
                        <span class="plan-meta-label">Target Quota:</span>
                        <span class="plan-meta-value">{plan.targetQuotaPercent.toFixed(0)}%</span>
                      </div>
                    {/if}
                  </div>
                </div>
              {/if}
            {/if}
          </div>
        {/if}

        <div class="switch-notice">
          <strong>Process Notice:</strong> This operation will apply vaulted credentials to Windows Credential Manager and gracefully restart Antigravity 2 if running.
        </div>
      </div>

      <div class="modal-footer">
        <button
          type="button"
          class="btn btn-secondary"
          onclick={onClose}
          disabled={isSwitching}
        >
          Cancel
        </button>
        <button
          type="button"
          class="btn btn-primary"
          onclick={handleConfirm}
          disabled={!canConfirm}
        >
          {isSwitching ? 'Switching...' : 'Confirm Switch'}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .close-btn {
    font-size: 14px;
    padding: 4px 8px;
  }

  .switch-prompt {
    font-size: 13.5px;
    color: var(--color-text-primary);
    line-height: 1.5;
  }

  .switch-notice {
    background-color: var(--color-primary-subtle);
    border-left: 3px solid var(--color-primary);
    padding: var(--space-2-5) var(--space-3);
    border-radius: 0 var(--radius-xs) var(--radius-xs) 0;
    font-size: 11.5px;
    color: var(--color-primary-text);
    margin-top: var(--space-2);
  }

  .switch-error-banner {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
    padding: var(--space-2) var(--space-3);
    border-radius: var(--radius-sm);
    font-size: 12px;
  }

  .switch-plan-container {
    margin: var(--space-2) 0;
  }

  .plan-status-card {
    padding: var(--space-2-5) var(--space-3);
    border-radius: var(--radius-sm);
    font-size: 12px;
    line-height: 1.4;
  }

  .plan-loading {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    background-color: var(--color-surface-hover);
    color: var(--color-text-secondary);
    border: 1px solid var(--color-card-border);
  }

  .plan-spinner {
    width: 14px;
    height: 14px;
    border: 2px solid var(--color-text-subtle);
    border-top-color: var(--color-primary);
    border-radius: 50%;
    animation: plan-spin 0.8s linear infinite;
    display: inline-block;
    flex-shrink: 0;
  }

  @keyframes plan-spin {
    to { transform: rotate(360deg); }
  }

  .plan-error {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
  }

  .plan-admissible {
    background-color: var(--color-success-subtle);
    border: 1px solid var(--color-success-border);
    color: var(--color-success-text);
  }

  .plan-warning {
    background-color: var(--color-warning-subtle);
    border: 1px solid var(--color-warning-border);
    color: var(--color-warning-text);
  }

  .plan-inadmissible {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
  }

  .plan-card-header {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    margin-bottom: 4px;
  }

  .plan-badge {
    display: inline-block;
    font-size: 10px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    padding: 2px 6px;
    border-radius: 3px;
  }

  .plan-badge.success {
    background-color: var(--color-success);
    color: #FFFFFF;
  }

  .plan-badge.warning {
    background-color: var(--color-warning);
    color: #FFFFFF;
  }

  .plan-badge.danger {
    background-color: var(--color-danger);
    color: #FFFFFF;
  }

  .plan-card-title {
    font-weight: 600;
    font-size: 12px;
  }

  .plan-card-message {
    margin: 2px 0 6px 0;
    font-size: 11.5px;
    opacity: 0.95;
  }

  .plan-meta-grid {
    display: grid;
    grid-template-columns: repeat(2, 1fr);
    gap: 4px 12px;
    margin-top: 6px;
    padding-top: 6px;
    border-top: 1px solid rgba(0, 0, 0, 0.08);
    font-size: 11px;
  }

  .plan-meta-item {
    display: flex;
    justify-content: space-between;
    gap: 4px;
  }

  .plan-meta-label {
    opacity: 0.8;
  }

  .plan-meta-value {
    font-weight: 600;
  }
</style>
