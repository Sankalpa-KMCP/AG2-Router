<script lang="ts">
  /**
   * SwitchJournalRecoveryModal Component
   *
   * Interactive recovery workflow for quarantined account switches.
   *
   * Architectural & Recovery Semantics:
   * - Triggered when `switchStatus.quarantine` is true (indicating an interrupted switch
   *   or unfinalized journal was discovered during startup or health check).
   * - Prevents split-brain state: ordinary switches and account lifecycle mutations are
   *   locked out while in quarantine.
   * - Reconciles recovery copy based on authoritative backend journal analysis (verifying
   *   whether credentials and processes reflect the source or target account).
   * - Submits resolution request (`onConfirm`) to either roll back safely or complete
   *   the switch, releasing quarantine upon successful resolution.
   */
  import type { JournalResolutionResult, SwitchStatusDto } from '../api/types.js';
  import { reconcileRecoveryCopy, type ResolutionCopy } from '../utils/recovery.js';

  interface Props {
    isOpen: boolean;
    isResolving?: boolean;
    switchStatus?: SwitchStatusDto | null;
    isSafetyAuthoritative?: boolean;
    onClose: () => void;
    onConfirm: () => Promise<JournalResolutionResult>;
  }

  let {
    isOpen = false,
    isResolving = false,
    switchStatus = null,
    isSafetyAuthoritative = false,
    onClose,
    onConfirm
  }: Props = $props();

  let resolutionResult = $state<JournalResolutionResult | null>(null);
  let errorCopy = $state<ResolutionCopy | null>(null);
  const outcomeCopy = $derived(resolutionResult
    ? reconcileRecoveryCopy(resolutionResult, switchStatus, isSafetyAuthoritative) : errorCopy);
  let localError = $state<string | null>(null);

  let previouslyFocusedElement: HTMLElement | null = null;
  let closeButtonEl = $state<HTMLButtonElement | null>(null);
  let cancelButtonEl = $state<HTMLButtonElement | null>(null);

  $effect(() => {
    if (isOpen) {
      if (typeof document !== 'undefined' && document.activeElement instanceof HTMLElement) {
        previouslyFocusedElement = document.activeElement;
      }
      setTimeout(() => {
        if (closeButtonEl && typeof closeButtonEl.focus === 'function') {
          closeButtonEl.focus();
        } else if (cancelButtonEl && typeof cancelButtonEl.focus === 'function') {
          cancelButtonEl.focus();
        }
      }, 0);
    } else {
      resolutionResult = null;
      errorCopy = null;
      localError = null;
      if (previouslyFocusedElement && typeof previouslyFocusedElement.focus === 'function' && document.body.contains(previouslyFocusedElement)) {
        previouslyFocusedElement.focus();
      }
      previouslyFocusedElement = null;
    }
  });

  function handleWindowKeyDown(e: KeyboardEvent) {
    if (!isOpen) return;
    if (e.key === 'Escape' && !isResolving) {
      e.preventDefault();
      e.stopPropagation();
      onClose();
    }
  }

  async function handleExecute() {
    if (isResolving) return;
    localError = null;
    try {
      const res = await onConfirm();
      resolutionResult = res;
    } catch {
      localError = 'A communication or unexpected error occurred during resolution.';
      errorCopy = {
        title: 'Resolution Error',
        description: 'A communication or unexpected error occurred during resolution.',
        alertType: 'error',
        canRetry: true,
        isRestartRequired: false
      };
    }
  }
</script>

<svelte:window onkeydown={handleWindowKeyDown} />

{#if isOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <div
    class="modal-backdrop"
    role="dialog"
    aria-modal="true"
    aria-labelledby="modal-recovery-title"
    tabindex="-1"
    onclick={(e) => { if (e.target === e.currentTarget && !isResolving) onClose(); }}
  >
    <div class="modal-dialog recovery-dialog">
      <div class="modal-header">
        <h3 class="modal-title" id="modal-recovery-title">Switch Journal Recovery</h3>
        <button
          bind:this={closeButtonEl}
          type="button"
          class="btn-icon close-btn"
          onclick={onClose}
          disabled={isResolving}
          aria-label="Close dialog"
        >
          ✕
        </button>
      </div>

      <div class="modal-body">
        {#if outcomeCopy}
          <div class="outcome-box outcome-{outcomeCopy.alertType}" role="alert">
            <div class="outcome-header">
              <span class="outcome-icon" aria-hidden="true">
                {#if outcomeCopy.alertType === 'success'}
                  ✓
                {:else if outcomeCopy.alertType === 'error'}
                  🚫
                {:else if outcomeCopy.alertType === 'warning'}
                  ⚠️
                {:else}
                  ℹ️
                {/if}
              </span>
              <strong class="outcome-title">{outcomeCopy.title}</strong>
            </div>
            <p class="outcome-desc">{outcomeCopy.description}</p>
          </div>

          {#if outcomeCopy.isRestartRequired && resolutionResult?.status === 'ResolvedRestartRequired'}
            <div class="restart-guide" role="status">
              <strong>System Tray Restart Required:</strong>
              <p>
                The switch journal artifact has been removed and persistent state is coherent. However, the running router process remains in safety quarantine.
                Please right-click the AG2 Router icon in the <strong>Windows system tray</strong>, select <strong>Exit</strong>, and launch AG2 Router again.
              </p>
            </div>
          {/if}
        {:else}
          <div class="recovery-intro">
            <p class="recovery-lead">
              A previous account switch was interrupted, leaving a switch journal on disk. The router entered safety quarantine to protect credential state and prevent split-brain routing.
            </p>
          </div>

          <div class="proof-pillars-card">
            <div class="proof-title">4-Pillar Coherence Verification</div>
            <p class="proof-subtitle">
              Recovery evaluates whether system state is already coherent across all four pillars:
            </p>
            <ul class="proof-list">
              <li><strong>1. Active Account Metadata:</strong> Current active account recorded in local store.</li>
              <li><strong>2. Enrolled Session Vault:</strong> Valid decrypted session token present in vault.</li>
              <li><strong>3. Live Running Identity:</strong> Account currently active in running Antigravity 2.</li>
              <li><strong>4. Windows Credential Manager:</strong> Windows credential payload matches vault session.</li>
            </ul>
          </div>

          <div class="recovery-warning">
            <strong>Important Safety Notice:</strong>
            Recovery will <em>not</em> force a switch or overwrite live credentials. It will only retire the journal if coherence is proven. If verified coherent, AG2 Router must be restarted from the Windows system tray.
          </div>

          {#if localError}
            <div class="error-banner" role="alert">
              {localError}
            </div>
          {/if}
        {/if}
      </div>

      <div class="modal-footer">
        {#if outcomeCopy}
          {#if outcomeCopy.canRetry}
            <button
              type="button"
              class="btn btn-secondary"
              onclick={onClose}
              disabled={isResolving}
            >
              Close
            </button>
            <button
              type="button"
              class="btn btn-primary"
              onclick={handleExecute}
              disabled={isResolving}
            >
              {isResolving ? 'Verifying...' : 'Retry Recovery'}
            </button>
          {:else}
            <button
              type="button"
              class="btn btn-primary"
              onclick={onClose}
              disabled={isResolving}
            >
              Close
            </button>
          {/if}
        {:else}
          <button
            bind:this={cancelButtonEl}
            type="button"
            class="btn btn-secondary"
            onclick={onClose}
            disabled={isResolving}
          >
            Cancel
          </button>
          <button
            type="button"
            class="btn btn-warning"
            onclick={handleExecute}
            disabled={isResolving}
          >
            {isResolving ? 'Verifying Coherence...' : 'Verify Coherence & Resolve'}
          </button>
        {/if}
      </div>
    </div>
  </div>
{/if}

<style>
  .recovery-dialog {
    max-width: 560px;
  }

  .close-btn {
    font-size: 14px;
    padding: 4px 8px;
  }

  .recovery-lead {
    font-size: 13px;
    color: var(--color-text-primary);
    line-height: 1.5;
  }

  .proof-pillars-card {
    background-color: var(--color-surface-subtle);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-sm);
    padding: var(--space-3);
  }

  .proof-title {
    font-size: 12.5px;
    font-weight: 600;
    color: var(--color-text-primary);
    margin-bottom: var(--space-1);
  }

  .proof-subtitle {
    font-size: 11.5px;
    color: var(--color-text-muted);
    margin-bottom: var(--space-2);
  }

  .proof-list {
    margin: 0;
    padding-left: var(--space-4);
    font-size: 12px;
    color: var(--color-text-secondary);
    display: flex;
    flex-direction: column;
    gap: var(--space-1-5);
  }

  .proof-list strong {
    color: var(--color-text-primary);
  }

  .recovery-warning {
    background-color: var(--color-warning-subtle);
    border-left: 3px solid var(--color-warning);
    padding: var(--space-2-5) var(--space-3);
    border-radius: 0 var(--radius-xs) var(--radius-xs) 0;
    font-size: 11.5px;
    color: var(--color-warning-text);
    line-height: 1.45;
  }

  .recovery-warning strong {
    display: block;
    margin-bottom: 2px;
  }

  .outcome-box {
    padding: var(--space-3) var(--space-3-5);
    border-radius: var(--radius-sm);
    border: 1px solid;
    display: flex;
    flex-direction: column;
    gap: var(--space-1-5);
  }

  .outcome-header {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  .outcome-icon {
    font-size: 15px;
  }

  .outcome-title {
    font-size: 13px;
    font-weight: 600;
  }

  .outcome-desc {
    font-size: 12px;
    line-height: 1.45;
  }

  .outcome-success {
    background-color: var(--color-success-subtle);
    border-color: var(--color-success-border);
    color: var(--color-success-text);
  }

  .outcome-info {
    background-color: var(--color-primary-subtle);
    border-color: var(--color-primary);
    color: var(--color-primary-text);
  }

  .outcome-warning {
    background-color: var(--color-warning-subtle);
    border-color: var(--color-warning-border);
    color: var(--color-warning-text);
  }

  .outcome-error {
    background-color: var(--color-danger-subtle);
    border-color: var(--color-danger-border);
    color: var(--color-danger-text);
  }

  .restart-guide {
    background-color: var(--color-surface-hover);
    border: 1px solid var(--color-card-border);
    border-radius: var(--radius-sm);
    padding: var(--space-3);
    font-size: 12px;
    color: var(--color-text-primary);
    line-height: 1.45;
  }

  .restart-guide strong {
    display: block;
    margin-bottom: var(--space-1);
    color: var(--color-primary-text);
  }

  .restart-guide p {
    margin: 0;
  }

  .error-banner {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
    padding: var(--space-2) var(--space-3);
    border-radius: var(--radius-sm);
    font-size: 12px;
  }

  .btn-warning {
    background-color: var(--color-warning);
    color: #ffffff;
    border-color: var(--color-warning-text);
  }

  .btn-warning:hover:not(:disabled) {
    background-color: var(--color-warning-text);
  }
</style>
