<script lang="ts">
  import { untrack } from 'svelte';
  import type { RouterConfigDto, RouterConfigUpdate, QuotaSnapshotDto, CandidateEvidenceStatusDto, AccountMetadata } from '../api/types.js';
  import { workloadModelKey, observedWorkloadModels, candidateEvidenceCopy } from '../utils/routing.js';
  import { resolveAccountDisplayName } from '../utils/helpers.js';

  interface Props {
    config: RouterConfigDto | null;
    autoSwitchEnabled: boolean;
    quota?: QuotaSnapshotDto | null;
    candidateEvidence?: CandidateEvidenceStatusDto | null;
    accounts?: AccountMetadata[];
    routingReason?: string | null;
    onSaveConfig: (updated: RouterConfigUpdate) => Promise<void>;
  }

  let {
    config = null,
    autoSwitchEnabled = false,
    quota = null,
    candidateEvidence = null,
    accounts = [],
    routingReason = null,
    onSaveConfig
  }: Props = $props();

  const initialConfig = untrack(() => config);
  let localAutoSwitch = $state<boolean>(initialConfig?.autoSwitchEnabled ?? untrack(() => autoSwitchEnabled));
  let localWorkloadModel = $state<string>(initialConfig?.workloadModelKey ?? '');
  let localLowThreshold = $state<number>(initialConfig?.lowQuotaThresholdPercent ?? 15);
  let localMinCandidate = $state<number>(initialConfig?.minimumCandidateQuotaPercent ?? 30);
  let localPollingSeconds = $state<number>((initialConfig?.pollingIntervalMs ?? 10000) / 1000);
  const modelOptions = $derived(observedWorkloadModels(quota));

  let isDirty = $state<boolean>(false);
  let editGeneration = 0;
  let isSaving = $state<boolean>(false);
  let saveMessage = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);

  function markDirty() {
    editGeneration++;
    isDirty = true;
  }

  // Sync with prop updates if changed externally and user has not made unsubmitted changes
  $effect(() => {
    if (!isDirty) {
      localAutoSwitch = config?.autoSwitchEnabled ?? autoSwitchEnabled;
    }
  });

  $effect(() => {
    if (config && !isDirty) {
      localLowThreshold = config.lowQuotaThresholdPercent;
      localWorkloadModel = config.workloadModelKey ?? '';
      localMinCandidate = config.minimumCandidateQuotaPercent;
      localPollingSeconds = config.pollingIntervalMs / 1000;
    }
  });

  async function handleSubmit(e: Event) {
    e.preventDefault();
    if (isSaving) return;
    isSaving = true;
    saveMessage = null;
    errorMessage = null;
    const capturedGeneration = editGeneration;

    try {
      await onSaveConfig({
        workloadModelKey: workloadModelKey(localWorkloadModel),
        autoSwitchEnabled: localAutoSwitch,
        lowQuotaThresholdPercent: localLowThreshold,
        minimumCandidateQuotaPercent: localMinCandidate,
        pollingIntervalMs: Math.round(localPollingSeconds * 1000)
      });
      if (editGeneration === capturedGeneration) {
        isDirty = false;
      }
      saveMessage = 'Settings saved successfully.';
      setTimeout(() => {
        saveMessage = null;
      }, 3500);
    } catch (err) {
      errorMessage = err instanceof Error ? err.message : 'Failed to save configuration';
    } finally {
      isSaving = false;
    }
  }
</script>

<div class="card settings-card">
  <div class="settings-header">
    <h2 class="settings-title">Router Configuration</h2>
    <p class="settings-subtitle">Safety thresholds and automated switching parameters</p>
  </div>

  <form onsubmit={handleSubmit} class="settings-form">
    <fieldset disabled={!config}>
    <!-- Auto Switch Toggle -->
    <div class="form-toggle-row">
      <div class="toggle-label-wrap">
        <label for="cfg-auto-switch" class="form-label-bold">Auto Switch</label>
        <p class="form-desc">
          Automatically schedule a native account switch when active account quota reaches or falls below the threshold.
        </p>
      </div>
      <label class="toggle-switch">
        <input
          id="cfg-auto-switch"
          type="checkbox"
          bind:checked={localAutoSwitch}
          onchange={markDirty}
        />
        <span class="toggle-slider"></span>
      </label>
    </div>

    <div class="form-group">
      <label for="cfg-workload-model" class="form-label">Workload Model</label>
      <input id="cfg-workload-model" class="input-text" type="text"
        list="workload-model-options" maxlength="128" autocomplete="off"
        bind:value={localWorkloadModel} oninput={markDirty}
        placeholder="Select an observed model or enter its exact key"
        aria-describedby="workload-model-help" />
      <datalist id="workload-model-options">
        {#each modelOptions as key}<option value={key}></option>{/each}
      </datalist>
      <span id="workload-model-help" class="form-hint">
        {modelOptions.length ? 'Suggestions are model keys from the latest account telemetry.' : 'No model keys are available from current telemetry. Enter the exact model key, or wait for telemetry.'}
        Automatic routing protects only this model. Display labels are not model keys.
      </span>
    </div>
    {#if localAutoSwitch && !localWorkloadModel.trim()}
      <p class="routing-warning" role="status">Auto Switch cannot operate until a workload model is configured.</p>
    {/if}

    <!-- Thresholds row -->
    <div class="form-row">
      <div class="form-group">
        <label for="cfg-low-threshold" class="form-label">Low Quota Threshold (%)</label>
        <input
          id="cfg-low-threshold"
          type="number"
          class="input-number"
          min="5"
          max="50"
          step="1"
          bind:value={localLowThreshold}
          oninput={markDirty}
          required
        />
        <span class="form-hint">Switch triggers when quota drops to or below this level.</span>
      </div>

      <div class="form-group">
        <label for="cfg-min-candidate" class="form-label">Minimum Candidate Quota (%)</label>
        <input
          id="cfg-min-candidate"
          type="number"
          class="input-number"
          min="10"
          max="90"
          step="1"
          bind:value={localMinCandidate}
          oninput={markDirty}
          required
        />
        <span class="form-hint">A candidate account must meet this quota floor to be selected.</span>
      </div>

      <div class="form-group">
        <label for="cfg-polling-interval" class="form-label">Polling Interval (seconds)</label>
        <input
          id="cfg-polling-interval"
          type="number"
          class="input-number"
          min="0.001"
          step="0.001"
          bind:value={localPollingSeconds}
          oninput={markDirty}
          required
        />
        <span class="form-hint">Telemetry refresh interval from Antigravity 2 loopback.</span>
      </div>
    </div>

    <div class="form-actions">
      <button
        type="submit"
        class="btn btn-primary"
        disabled={isSaving || !config}
      >
        {isSaving ? 'Saving...' : 'Save Settings'}
      </button>

      {#if saveMessage}
        <span class="save-feedback success" role="status" aria-live="polite">✓ {saveMessage}</span>
      {/if}
      {#if errorMessage}
        <span class="save-feedback error" role="alert">✕ {errorMessage}</span>
      {/if}
    </div>
    </fieldset>
  </form>
  <section class="candidate-evidence" aria-label="Candidate quota evidence">
    <h3>Candidate quota evidence</h3>
    {#if routingReason}<p class="form-hint">Router: {routingReason}</p>{/if}
    {#if !candidateEvidence?.available}
      <p class="form-hint">Candidate evidence unavailable. Automatic routing requires verified recent evidence.</p>
    {:else if !candidateEvidence.modelKey}
      <p class="form-hint">Configure a workload model to inspect its candidate quota.</p>
    {:else}
      <p class="form-hint">Saved model: {candidateEvidence.modelKey} · Minimum: {candidateEvidence.minimumCandidateQuotaPercent}%. Observations expire after two hours. Manual switching is independent of this evidence.</p>
      {#if isDirty}<p class="form-hint">Evidence below reflects saved settings. Save to evaluate the new settings.</p>{/if}
      {#if candidateEvidence.candidates.length === 0}<p class="form-hint">No inactive candidate accounts.</p>{/if}
      <ul class="evidence-list">
        {#each candidateEvidence.candidates as row (row.accountId)}
          {@const account = accounts.find(a => a.id === row.accountId)}
          <li>
            <strong>{account ? resolveAccountDisplayName(account) : 'Candidate account'}</strong>
            <span>{candidateEvidenceCopy(row)}</span>
            {#if ['USABLE', 'BELOW_MINIMUM', 'EXHAUSTED'].includes(row.state) && row.remainingFraction !== null && Number.isFinite(row.remainingFraction) && row.remainingFraction >= 0 && row.remainingFraction <= 1}
              <span>{Math.floor(row.remainingFraction * 100)}% observed remaining</span>
            {/if}
            {#if row.observedAtUtc}
              <small>Observed: {row.observedAtUtc}{row.ageSeconds !== null && Number.isFinite(row.ageSeconds) && row.ageSeconds >= 0 ? ` · ${Math.floor(row.ageSeconds / 60)} min old at last refresh` : ''}</small>
            {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </section>
</div>

<style>
  fieldset { border: 0; padding: 0; margin: 0; min-width: 0; display: flex; flex-direction: column; gap: var(--space-4); }
  .routing-warning { padding: 12px; border: 1px solid var(--color-warning-border); border-radius: var(--radius-sm); background: var(--color-warning-subtle); color: var(--color-warning-text); font-size: 13px; }
  .candidate-evidence { margin-top: var(--space-5); padding-top: var(--space-4); border-top: 1px solid var(--color-divider); }
  .candidate-evidence h3 { font-size: 14px; margin-bottom: var(--space-2); }
  .evidence-list { list-style: none; padding: 0; margin-top: var(--space-3); }
  .evidence-list li { display: flex; flex-direction: column; gap: 4px; padding: 12px 0; border-bottom: 1px solid var(--color-divider); font-size: 12px; }
  .evidence-list small { color: var(--color-text-muted); }
  .settings-card {
    padding: var(--space-5);
    margin-bottom: var(--space-5);
  }

  .settings-header {
    margin-bottom: var(--space-4);
  }

  .settings-title {
    font-size: 15px;
    font-weight: 700;
    color: var(--color-text-primary);
  }

  .settings-subtitle {
    font-size: 12px;
    color: var(--color-text-muted);
    margin-top: 2px;
  }

  .settings-form {
    display: flex;
    flex-direction: column;
    gap: var(--space-4);
  }

  .form-toggle-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding-bottom: var(--space-4);
    border-bottom: 1px solid var(--color-divider);
    gap: var(--space-4);
  }

  .toggle-label-wrap {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .form-label-bold {
    font-size: 13.5px;
    font-weight: 600;
    color: var(--color-text-primary);
    cursor: pointer;
  }

  .form-desc {
    font-size: 12px;
    color: var(--color-text-muted);
    max-width: 600px;
  }

  .toggle-switch {
    position: relative;
    display: inline-block;
    width: 44px;
    height: 24px;
    flex-shrink: 0;
  }

  .toggle-switch input {
    opacity: 0;
    width: 0;
    height: 0;
  }

  .toggle-slider {
    position: absolute;
    cursor: pointer;
    inset: 0;
    background-color: var(--color-card-border);
    transition: var(--transition-fast);
    border-radius: var(--radius-full);
  }

  .toggle-slider::before {
    position: absolute;
    content: "";
    height: 18px;
    width: 18px;
    left: 3px;
    bottom: 3px;
    background-color: #FFFFFF;
    transition: var(--transition-fast);
    border-radius: var(--radius-full);
    box-shadow: var(--shadow-xs);
  }

  .toggle-switch input:checked + .toggle-slider {
    background-color: var(--color-primary);
  }

  .toggle-switch input:checked + .toggle-slider::before {
    transform: translateX(20px);
  }

  .form-row {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: var(--space-4);
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

  .form-hint {
    font-size: 11px;
    color: var(--color-text-muted);
  }

  .form-actions {
    display: flex;
    align-items: center;
    gap: var(--space-3);
    padding-top: var(--space-2);
  }

  .save-feedback {
    font-size: 12px;
    font-weight: 500;
  }

  .save-feedback.success {
    color: var(--color-success);
  }

  .save-feedback.error {
    color: var(--color-danger);
  }
</style>
