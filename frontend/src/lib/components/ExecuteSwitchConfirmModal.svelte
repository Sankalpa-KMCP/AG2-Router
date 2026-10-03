<script lang="ts">
  import type { AccountMetadata } from '../api/types.js';
  import { resolveAccountDisplayName } from '../utils/helpers.js';
  import { getSwitchErrorMessage } from '../utils/switching.js';

  interface Props {
    isOpen: boolean;
    account: AccountMetadata | null;
    isSwitching?: boolean;
    onClose: () => void;
    onConfirm: (account: AccountMetadata) => Promise<void>;
  }

  let {
    isOpen = false,
    account = null,
    isSwitching = false,
    onClose,
    onConfirm
  }: Props = $props();

  let error = $state<string | null>(null);

  function handleKeyDown(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
    }
  }

  async function handleConfirm() {
    if (!account || isSwitching) return;
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
          disabled={isSwitching}
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
  }

  .switch-error-banner {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
    padding: var(--space-2) var(--space-3);
    border-radius: var(--radius-sm);
    font-size: 12px;
  }
</style>
