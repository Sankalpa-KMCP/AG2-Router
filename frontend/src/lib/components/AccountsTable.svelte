<script lang="ts">
  /**
   * AccountsTable Component
   *
   * Displays enrolled accounts, status badges (active, reserve, candidate),
   * inline alias editing, and manual switch triggers.
   *
   * Architectural & Safety Invariants:
   * - Mutation Fencing: `canMutate` derives from `lifecycleMutationsAllowed && !isQuarantined`.
   *   Mutations (switching, alias update, deletion, enrollment) are strictly fenced during
   *   active switch execution or when quarantine state is active.
   * - Reserve Account Semantics: Accounts flagged with `isReserve` are excluded from automatic
   *   quota-based switching, serving as reserved fallbacks for manual invocation.
   * - Active Account Invariant: The currently active account cannot be deleted or re-switched.
   */
  import { resolveAccountDisplayName, getAccountSubtitle } from '../utils/helpers.js';
  import type { AccountMutationResult } from '../utils/account-mutations.js';
  import type { AccountMetadata } from '../api/types.js';

  interface Props {
    accounts: AccountMetadata[];
    onSaveCurrent: () => void;
    onOpenConnect: () => void;
    onExecuteSwitch: (account: AccountMetadata) => void;
    onDeleteAccount: (account: AccountMetadata) => void;
    onUpdateAlias: (id: string, newAlias: string) => Promise<AccountMutationResult>;
    isSavingCurrent?: boolean;
    isSwitching?: boolean;
    isQuarantined?: boolean;
    lifecycleMutationsAllowed?: boolean;
  }

  let {
    accounts = [],
    onSaveCurrent,
    onOpenConnect,
    onExecuteSwitch,
    onDeleteAccount,
    onUpdateAlias,
    isSavingCurrent = false,
    isSwitching = false,
    isQuarantined = false,
    lifecycleMutationsAllowed = false
  }: Props = $props();

  const canMutate = $derived(lifecycleMutationsAllowed && !isQuarantined);

  // Inline alias editing state
  let editingId = $state<string | null>(null);
  let editValue = $state<string>('');
  let isSavingAlias = $state<boolean>(false);
  let aliasError = $state<string | null>(null);

  function startEditing(account: AccountMetadata) {
    editingId = account.id;
    editValue = account.alias || '';
    aliasError = null;
  }

  function cancelEditing() {
    editingId = null;
    editValue = '';
    aliasError = null;
  }

  async function saveAlias(id: string) {
    if (isSavingAlias) return;
    isSavingAlias = true;
    aliasError = null;
    try {
      const result = await onUpdateAlias(id, editValue);
      if (result.success) editingId = null;
      else aliasError = result.message;
    } catch (err) {
      aliasError = err instanceof Error ? err.message : 'Failed to save alias';
    } finally {
      isSavingAlias = false;
    }
  }

  function focusOnMount(el: HTMLElement) {
    el.focus();
    if (el instanceof HTMLInputElement) el.select();
  }

  function handleKeyDown(e: KeyboardEvent, id: string) {
    if (e.key === 'Enter') {
      e.preventDefault();
      saveAlias(id);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      cancelEditing();
    }
  }
</script>

<div class="card accounts-card">
  <div class="accounts-header">
    <div>
      <h2 class="accounts-title">Connected Accounts</h2>
      <p class="accounts-subtitle">Accounts managed for quota-aware switching and telemetry</p>
    </div>
    <div class="header-actions">
      <button
        type="button"
        class="btn btn-secondary btn-sm"
        onclick={onSaveCurrent}
        disabled={isSavingCurrent || !canMutate}
        title={!canMutate ? "Account operations are disabled while safety status is unresolved or quarantine is active" : "Capture active session from Antigravity 2 into local encrypted vault"}
      >
        <span>🔒</span>
        <span>{isSavingCurrent ? 'Saving...' : 'Save Current AG2 Account'}</span>
      </button>
      <button
        type="button"
        class="btn btn-primary btn-sm"
        onclick={onOpenConnect}
      >
        <span>+</span>
        <span>Connect Account</span>
      </button>
    </div>
  </div>

  {#if accounts.length === 0}
    <div class="empty-state">
      <div class="empty-icon" aria-hidden="true">👤</div>
      <h3 class="empty-title">No Accounts Connected</h3>
      <p class="empty-desc">
        Capture the running Antigravity 2 session into the encrypted vault or register account credentials.
      </p>
      <div class="empty-actions">
        <button
          type="button"
          class="btn btn-primary"
          onclick={onSaveCurrent}
          disabled={isSavingCurrent || isQuarantined}
          title={isQuarantined ? "Account enrollment is disabled while safety quarantine is active" : "Capture active session from Antigravity 2 into local encrypted vault"}
        >
          <span>🔒 Save Current AG2 Account</span>
        </button>
        <button
          type="button"
          class="btn btn-secondary"
          onclick={onOpenConnect}
        >
          <span>Connect Account</span>
        </button>
      </div>
    </div>
  {:else}
    <div class="table-container">
      <table class="data-table" aria-label="Accounts Table">
        <thead>
          <tr>
            <th>Account &amp; Alias</th>
            <th>Priority</th>
            <th>Role</th>
            <th>Vault Storage</th>
            <th>Status</th>
            <th class="actions-col">Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each accounts as acc (acc.id)}
            {@const displayName = resolveAccountDisplayName(acc)}
            {@const subtitle = getAccountSubtitle(acc)}
            {@const isEditing = editingId === acc.id}
            <tr class={acc.isActive ? 'row-active' : ''}>
              <td>
                <div class="account-cell">
                  {#if isEditing}
                    <div class="alias-edit-form">
                      <label for="edit-alias-{acc.id}" class="sr-only">Edit Friendly Alias for {acc.email}</label>
                      <input
                        id="edit-alias-{acc.id}"
                        maxlength="64"
                        type="text"
                        class="input-text alias-input"
                        placeholder="Friendly alias (e.g. Work, Personal)"
                        bind:value={editValue}
                        use:focusOnMount
                        onkeydown={(e) => handleKeyDown(e, acc.id)}
                        disabled={isSavingAlias}
                      />
                      <div class="alias-edit-actions">
                        <button
                          type="button"
                          class="btn btn-primary btn-sm"
                          onclick={() => saveAlias(acc.id)}
                          disabled={isSavingAlias}
                          title="Save alias (Enter)"
                        >
                          {isSavingAlias ? 'Saving...' : 'Save'}
                        </button>
                        <button
                          type="button"
                          class="btn btn-secondary btn-sm"
                          onclick={cancelEditing}
                          disabled={isSavingAlias}
                          title="Cancel edit (Esc)"
                        >
                          Cancel
                        </button>
                      </div>
                      {#if aliasError}
                        <span class="alias-error">{aliasError}</span>
                      {/if}
                    </div>
                  {:else}
                    <div class="identity-row">
                      <div class="name-wrap">
                        <strong class="account-primary-name">{displayName}</strong>
                        {#if acc.isActive}
                          <span class="badge badge-healthy">ACTIVE</span>
                        {/if}
                      </div>
                      <button
                        type="button"
                        class="btn-icon alias-pencil"
                        onclick={() => startEditing(acc)}
                        title="Edit friendly alias"
                        aria-label="Edit alias for {displayName}"
                      >
                        ✎
                      </button>
                    </div>
                    {#if subtitle}
                      <span class="account-email-subtitle">{subtitle}</span>
                    {/if}
                  {/if}
                </div>
              </td>
              <td>Priority {acc.priority}</td>
              <td>
                <span class="badge {acc.isReserve ? 'badge-warning' : 'badge-neutral'}">
                  {acc.isReserve ? 'Reserve' : 'Standard'}
                </span>
              </td>
              <td>
                <span class="badge {acc.hasVaultedSession ? 'badge-healthy' : 'badge-neutral'}">
                  {acc.hasVaultedSession ? '🔒 Vaulted' : 'Metadata Only'}
                </span>
              </td>
              <td>
                <span class="status-cell">{acc.validationStatus || 'VALIDATED'}</span>
              </td>
              <td class="actions-col">
                <div class="row-actions">
                  {#if acc.isActive}
                    <span class="badge badge-healthy active-indicator">ACTIVE</span>
                  {:else}
                    {#if acc.hasVaultedSession}
                      <button
                        type="button"
                        class="btn btn-primary btn-sm"
                        onclick={() => onExecuteSwitch(acc)}
                        disabled={isSwitching || !canMutate}
                        title={!canMutate ? "Account switching is disabled while safety status is unresolved or quarantine is active" : "Switch active session to this account"}
                      >
                        Switch
                      </button>
                    {/if}
                  {/if}
                  <button
                    type="button"
                    class="btn btn-danger btn-sm"
                    onclick={() => onDeleteAccount(acc)}
                    disabled={!canMutate}
                    title={!canMutate ? "Account deletion is disabled while safety status is unresolved or quarantine is active" : "Remove account"}
                  >
                    Delete
                  </button>
                </div>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

<style>
  .accounts-card {
    padding: var(--space-5);
    margin-bottom: var(--space-5);
  }

  .accounts-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: var(--space-4);
    gap: var(--space-3);
  }

  .accounts-title {
    font-size: 15px;
    font-weight: 700;
    color: var(--color-text-primary);
  }

  .accounts-subtitle {
    font-size: 12px;
    color: var(--color-text-muted);
    margin-top: 2px;
  }

  .header-actions {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  .account-cell {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .identity-row {
    display: flex;
    align-items: center;
    gap: var(--space-1-5);
  }

  .name-wrap {
    display: flex;
    align-items: center;
    gap: var(--space-2);
  }

  .account-primary-name {
    font-size: 13px;
    color: var(--color-text-primary);
  }

  .account-email-subtitle {
    font-size: 11.5px;
    color: var(--color-text-muted);
  }

  .alias-pencil {
    opacity: 0.7;
    font-size: 13px;
    min-width: 24px;
    min-height: 24px;
    padding: 0;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    border-radius: var(--radius-xs);
  }

  .alias-pencil:hover {
    opacity: 1;
    background-color: var(--color-surface-hover);
  }

  .alias-edit-form {
    display: flex;
    flex-direction: column;
    gap: var(--space-1-5);
    min-width: 200px;
    max-width: 320px;
  }

  .alias-input {
    padding: 4px 8px;
    font-size: 12px;
  }

  .alias-edit-actions {
    display: flex;
    gap: var(--space-1);
  }

  .alias-error {
    font-size: 11px;
    color: var(--color-danger-text);
  }

  .row-active {
    background-color: var(--color-success-subtle);
  }

  .actions-col {
    text-align: right;
  }

  .row-actions {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: var(--space-1-5);
  }

  .active-indicator {
    padding: 3px 8px;
  }

  .status-cell {
    font-size: 11.5px;
    color: var(--color-text-secondary);
  }

  .empty-state {
    text-align: center;
    padding: var(--space-8) var(--space-4);
  }

  .empty-icon {
    font-size: 32px;
    margin-bottom: var(--space-2);
  }

  .empty-title {
    font-size: 15px;
    font-weight: 600;
    color: var(--color-text-primary);
    margin-bottom: var(--space-1);
  }

  .empty-desc {
    font-size: 12.5px;
    color: var(--color-text-muted);
    max-width: 440px;
    margin: 0 auto var(--space-4) auto;
  }

  .empty-actions {
    display: flex;
    justify-content: center;
    gap: var(--space-2);
  }

  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    padding: 0;
    margin: -1px;
    overflow: hidden;
    clip: rect(0, 0, 0, 0);
    border: 0;
  }
</style>
