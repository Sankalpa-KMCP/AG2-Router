<script lang="ts">
  interface Props {
    isOpen: boolean;
    onClose: () => void;
    onSubmit: (data: {
      email: string;
      name?: string;
      alias?: string;
      priority: number;
      isReserve: boolean;
    }) => Promise<void>;
  }

  let {
    isOpen = false,
    onClose,
    onSubmit
  }: Props = $props();

  let email = $state('');
  let name = $state('');
  let alias = $state('');
  let priority = $state(1);
  let isReserve = $state(false);

  let isSubmitting = $state(false);
  let error = $state<string | null>(null);

  function resetForm() {
    email = '';
    name = '';
    alias = '';
    priority = 1;
    isReserve = false;
    error = null;
    isSubmitting = false;
  }

  function handleClose() {
    resetForm();
    onClose();
  }

  function focusOnMount(el: HTMLElement) {
    el.focus();
  }

  function handleKeyDown(e: KeyboardEvent) {
    if (e.key === 'Escape') {
      e.preventDefault();
      handleClose();
    }
  }

  async function handleSubmit(e: Event) {
    e.preventDefault();
    if (isSubmitting) return;

    if (!email.trim() || !email.includes('@')) {
      error = 'Please enter a valid email address.';
      return;
    }

    isSubmitting = true;
    error = null;

    try {
      await onSubmit({
        email: email.trim(),
        name: name.trim() || undefined,
        alias: alias.trim() || undefined,
        priority: Number(priority) || 1,
        isReserve
      });
      resetForm();
      onClose();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to connect account';
    } finally {
      isSubmitting = false;
    }
  }
</script>

{#if isOpen}
  <!-- svelte-ignore a11y_click_events_have_key_events -->
  <div
    class="modal-backdrop"
    role="dialog"
    aria-modal="true"
    aria-labelledby="modal-connect-title"
    tabindex="-1"
    onkeydown={handleKeyDown}
    onclick={(e) => { if (e.target === e.currentTarget) handleClose(); }}
  >
    <div class="modal-dialog">
      <div class="modal-header">
        <h3 class="modal-title" id="modal-connect-title">Connect Account</h3>
        <button
          type="button"
          class="btn-icon close-btn"
          onclick={handleClose}
          aria-label="Close dialog"
        >
          ✕
        </button>
      </div>

      <form onsubmit={handleSubmit}>
        <div class="modal-body">
          {#if error}
            <div class="form-error-banner" role="alert">
              {error}
            </div>
          {/if}

          <div class="form-group">
            <label for="acc-email" class="form-label">
              Account Email <span class="required" aria-hidden="true">*</span>
            </label>
            <input
              id="acc-email"
              type="email"
              class="input-text"
              placeholder="developer@example.com"
              bind:value={email}
              use:focusOnMount
              required
              disabled={isSubmitting}
            />
          </div>

          <div class="form-group">
            <label for="acc-alias" class="form-label">
              Friendly Name / Alias (Optional)
            </label>
            <input
              id="acc-alias"
              type="text"
              class="input-text"
              placeholder="Work, Personal, Backup 1"
              bind:value={alias}
              disabled={isSubmitting}
            />
            <span class="form-hint">Displayed prominently throughout the dashboard.</span>
          </div>

          <div class="form-group">
            <label for="acc-name" class="form-label">
              Profile Name / Organization (Optional)
            </label>
            <input
              id="acc-name"
              type="text"
              class="input-text"
              placeholder="Google Developer Account"
              bind:value={name}
              disabled={isSubmitting}
            />
          </div>

          <div class="form-row">
            <div class="form-group">
              <label for="acc-priority" class="form-label">Priority</label>
              <input
                id="acc-priority"
                type="number"
                class="input-number"
                min="1"
                max="100"
                bind:value={priority}
                disabled={isSubmitting}
              />
              <span class="form-hint">1 = Highest routing preference</span>
            </div>

            <div class="form-group checkbox-group">
              <label class="checkbox-label">
                <input
                  type="checkbox"
                  bind:checked={isReserve}
                  disabled={isSubmitting}
                />
                <span>Reserve account</span>
              </label>
              <span class="form-hint">Selected only when standard accounts are exhausted.</span>
            </div>
          </div>

          <div class="security-note">
            <strong>Security Notice:</strong> Registers account identity with local storage.
            Vaulted encrypted session tokens are established securely via Antigravity session capture.
          </div>
        </div>

        <div class="modal-footer">
          <button
            type="button"
            class="btn btn-secondary"
            onclick={handleClose}
            disabled={isSubmitting}
          >
            Cancel
          </button>
          <button
            type="submit"
            class="btn btn-primary"
            disabled={isSubmitting}
          >
            {isSubmitting ? 'Saving...' : 'Save Account'}
          </button>
        </div>
      </form>
    </div>
  </div>
{/if}

<style>
  .close-btn {
    font-size: 14px;
    padding: 4px 8px;
  }

  .form-group {
    display: flex;
    flex-direction: column;
    gap: var(--space-1);
  }

  .form-label {
    font-size: 12.5px;
    font-weight: 500;
    color: var(--color-text-secondary);
  }

  .required {
    color: var(--color-danger);
  }

  .form-hint {
    font-size: 11px;
    color: var(--color-text-muted);
  }

  .form-row {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: var(--space-4);
  }

  .checkbox-group {
    justify-content: flex-end;
  }

  .checkbox-label {
    display: flex;
    align-items: center;
    gap: var(--space-2);
    font-size: 12.5px;
    font-weight: 500;
    color: var(--color-text-primary);
    cursor: pointer;
  }

  .security-note {
    background-color: var(--color-surface-subtle);
    border-left: 3px solid var(--color-primary);
    padding: var(--space-2-5) var(--space-3);
    border-radius: 0 var(--radius-xs) var(--radius-xs) 0;
    font-size: 11.5px;
    color: var(--color-text-secondary);
  }

  .form-error-banner {
    background-color: var(--color-danger-subtle);
    border: 1px solid var(--color-danger-border);
    color: var(--color-danger-text);
    padding: var(--space-2) var(--space-3);
    border-radius: var(--radius-sm);
    font-size: 12px;
  }
</style>
