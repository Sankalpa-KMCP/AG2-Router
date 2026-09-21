<script lang="ts">
  import type { AccountMetadata } from '../api/types.js';
  import { resolveAccountDisplayName } from '../utils/helpers.js';

  interface Props {
    isOpen: boolean;
    account: AccountMetadata | null;
    isDeleting?: boolean;
    onClose: () => void;
    onConfirm: (account: AccountMetadata) => Promise<void>;
  }

  let {
    isOpen = false,
    account = null,
    isDeleting = false,
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
    if (!account || isDeleting) return;
    error = null;
    try {
      await onConfirm(account);
      onClose();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to delete account';
    }
  }
</script>

{#if isOpen && account}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <div
    class="modal-backdrop"
    role="dialog"
    aria-modal="true"
    aria-labelledby="modal-delete-title"
    tabindex="-1"
    onkeydown={handleKeyDown}
    onclick={(e) => { if (e.target === e.currentTarget) onClose(); }}
  >
    <div class="modal-dialog">
      <div class="modal-header">
        <h3 class="modal-title" id="modal-delete-title">Delete Account</h3>
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
          <div class="delete-error-banner" role="alert">
            {error}
          </div>
        {/if}

        <p class="delete-prompt">
          Are you sure you want to remove account
          <strong>{resolveAccountDisplayName(account)}</strong>
          ({account.email})?
        </p>

        {#if account.hasVaultedSession}
          <div class="vault-warning" role="alert">
            <strong>⚠️ Permanent Vault Deletion:</strong>
            This account has an encrypted session vault record. Deleting it will permanently remove its encrypted credentials from local storage (<code>sessions.dat</code>).
          </div>
        {/if}
      </div>

      <div class="modal-footer">
        <button
          type="button"
          class="btn btn-secondary"
          onclick={onClose}
          disabled={isDeleting}
        >
          Cancel
        </button>
        <button
          type="button"
          class="btn btn-danger"
          onclick={handleConfirm}
          disabled={isDeleting}
        >
          {isDeleting ? 'Deleting...' : 'Confirm Delete'}
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

  .delete-prompt {
    font-size: 13.5px;
    color: var(--color-text-primary);
    line-height: 1.5;
  }

  .vault-warning {
    background-color: var(--color-danger-subtle);
    border-left: 3px solid var(--color-danger);
    padding: var(--space-2-5) var(--space-3);
    border-radius: 0 var(--radius-xs) var(--radius-xs) 0;
    font-size: 11.5px;
    color: var(--color-danger-text);
  }

  .delete-error-banner {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
    padding: var(--space-2) var(--space-3);
    border-radius: var(--radius-sm);
    font-size: 12px;
  }
</style>
