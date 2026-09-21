<script lang="ts">
  import type { SwitchPlanDto, AccountMetadata } from '../api/types.js';
  import { resolveAccountDisplayName } from '../utils/helpers.js';

  interface Props {
    isOpen: boolean;
    targetAccount: AccountMetadata | null;
    plan: SwitchPlanDto | null;
    isLoading: boolean;
    error: string | null;
    onClose: () => void;
  }

  let {
    isOpen = false,
    targetAccount = null,
    plan = null,
    isLoading = false,
    error = null,
    onClose
  }: Props = $props();

  const checkLabels: Record<string, string> = {
    TARGET_ACCOUNT_EXISTS: 'Target Account Storage',
    TARGET_HAS_VAULTED_SESSION: 'Encrypted Session Vault',
    TARGET_NOT_ALREADY_ACTIVE: 'Active Account Exclusivity',
    AG2_ACTIVITY_IS_IDLE: 'Antigravity 2 Activity & Idle State',
    ROLLBACK_SNAPSHOT_READABLE: 'Rollback Snapshot Integrity',
    LAUNCH_SPEC_CAPTURABLE: 'Process Launch Specification'
  };

  function handleKeyDown(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
  }
</script>

{#if isOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <div
    class="modal-backdrop"
    role="dialog"
    aria-modal="true"
    aria-labelledby="modal-switch-plan-title"
    tabindex="-1"
    onkeydown={handleKeyDown}
    onclick={(e) => { if (e.target === e.currentTarget) onClose(); }}
  >
    <div class="modal-dialog" style="max-width: 560px;">
      <div class="modal-header">
        <h3 class="modal-title" id="modal-switch-plan-title">Account Switch Readiness (Dry-Run)</h3>
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
        <div class="plan-summary-card">
          <div class="plan-summary-left">
            <span class="plan-target-label">Target Candidate</span>
            <span class="plan-target-name">{targetAccount ? resolveAccountDisplayName(targetAccount) : '--'}</span>
            {#if targetAccount?.email}
              <span class="plan-target-email">{targetAccount.email}</span>
            {/if}
          </div>
          <div class="plan-summary-right">
            {#if isLoading}
              <span class="badge badge-neutral">Evaluating...</span>
            {:else if error}
              <span class="badge badge-danger">Evaluation Error</span>
            {:else if plan}
              {#if plan.ready}
                <span class="badge badge-healthy">READY FOR SWITCH</span>
              {:else}
                <span class="badge badge-danger">BLOCKED ({plan.blockers.length} issues)</span>
              {/if}
            {/if}
          </div>
        </div>

        {#if isLoading}
          <div class="loading-state">
            <span>Evaluating live readiness checks against Antigravity 2...</span>
          </div>
        {:else if error}
          <div class="error-banner" role="alert">
            {error}
          </div>
        {:else if plan}
          <div class="checklist-section">
            <h4 class="section-heading">Preflight Safety Checklist (6 Points)</h4>
            <div class="checklist-items">
              {#each plan.checks as check (check.code)}
                <div class="check-item {check.passed ? 'passed' : 'failed'}">
                  <span class="check-icon" aria-hidden="true">
                    {check.passed ? '✓' : '✕'}
                  </span>
                  <div class="check-details">
                    <strong class="check-title">{checkLabels[check.code] || check.code}</strong>
                    <span class="check-desc">{check.message}</span>
                  </div>
                </div>
              {/each}
            </div>
          </div>

          <div class="pipeline-section">
            <h4 class="section-heading">Execution Pipeline Stages (Preview)</h4>
            <ol class="pipeline-list">
              <li>Validate target account &amp; decrypt session from encrypted vault</li>
              <li>Verify Antigravity 2 is idle (0 running tasks/trajectories)</li>
              <li>Take rollback snapshot (WinCred + launch specification)</li>
              <li>Apply candidate credentials to Windows Credential Manager</li>
              <li>Terminate and restart Antigravity 2 process safely</li>
              <li>Verify post-switch identity &amp; quota telemetry stream</li>
            </ol>
          </div>

          <div class="safety-banner">
            <strong>🛡️ Safety Gate Enforced:</strong> This is a read-only readiness evaluation.
            No Windows Credentials have been modified, and Antigravity 2 was not restarted.
          </div>
        {/if}
      </div>

      <div class="modal-footer">
        <button
          type="button"
          class="btn btn-secondary"
          onclick={onClose}
        >
          Close
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

  .plan-summary-card {
    display: flex;
    align-items: center;
    justify-content: space-between;
    background-color: var(--color-surface-subtle);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-sm);
    padding: var(--space-3) var(--space-4);
  }

  .plan-summary-left {
    display: flex;
    flex-direction: column;
    gap: 1px;
  }

  .plan-target-label {
    font-size: 10.5px;
    font-weight: 600;
    text-transform: uppercase;
    color: var(--color-text-muted);
  }

  .plan-target-name {
    font-size: 14px;
    font-weight: 700;
    color: var(--color-text-primary);
  }

  .plan-target-email {
    font-size: 11.5px;
    color: var(--color-text-muted);
  }

  .section-heading {
    font-size: 11px;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
    margin-bottom: var(--space-2);
  }

  .checklist-items {
    display: flex;
    flex-direction: column;
    gap: var(--space-2);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-sm);
    padding: var(--space-2) var(--space-3);
    background-color: var(--color-surface);
  }

  .check-item {
    display: flex;
    align-items: flex-start;
    gap: var(--space-2-5);
    font-size: 12px;
  }

  .check-icon {
    font-size: 13px;
    font-weight: 700;
    width: 16px;
    text-align: center;
    flex-shrink: 0;
  }

  .check-item.passed .check-icon {
    color: var(--color-success);
  }

  .check-item.failed .check-icon {
    color: var(--color-danger);
  }

  .check-details {
    display: flex;
    flex-direction: column;
    gap: 1px;
  }

  .check-title {
    font-size: 12px;
    color: var(--color-text-primary);
  }

  .check-desc {
    font-size: 11px;
    color: var(--color-text-muted);
  }

  .pipeline-list {
    margin: 0;
    padding-left: 20px;
    font-size: 11.5px;
    color: var(--color-text-secondary);
    line-height: 1.6;
  }

  .safety-banner {
    background-color: var(--color-warning-subtle);
    border-left: 3px solid var(--color-warning);
    padding: var(--space-2-5) var(--space-3);
    border-radius: 0 var(--radius-xs) var(--radius-xs) 0;
    font-size: 11.5px;
    color: var(--color-warning-text);
  }

  .loading-state {
    padding: var(--space-6);
    text-align: center;
    color: var(--color-text-muted);
    font-size: 12px;
  }

  .error-banner {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
    padding: var(--space-2-5) var(--space-3);
    border-radius: var(--radius-sm);
    font-size: 12px;
  }
</style>
