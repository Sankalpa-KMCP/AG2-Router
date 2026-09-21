<script lang="ts">
  import type { RouterConfigDto } from '../api/types.js';

  interface Props {
    config: RouterConfigDto | null;
    autoSwitchEnabled: boolean;
    onSaveConfig: (updated: {
      autoSwitchEnabled: boolean;
      lowQuotaThresholdPercent: number;
      minimumCandidateQuotaPercent: number;
      pollingIntervalMs: number;
    }) => Promise<void>;
  }

  let {
    config = null,
    autoSwitchEnabled = false,
    onSaveConfig
  }: Props = $props();

  let localAutoSwitch = $state<boolean>(false);
  let localLowThreshold = $state<number>(15);
  let localMinCandidate = $state<number>(30);
  let localPollingSeconds = $state<number>(10);

  let isSaving = $state<boolean>(false);
  let saveMessage = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);

  // Sync with prop updates if changed externally
  $effect(() => {
    localAutoSwitch = autoSwitchEnabled;
  });

  $effect(() => {
    if (config) {
      localLowThreshold = config.lowQuotaThresholdPercent;
      localMinCandidate = config.minimumCandidateQuotaPercent;
      localPollingSeconds = Math.round(config.pollingIntervalMs / 1000);
    }
  });

  async function handleSubmit(e: Event) {
    e.preventDefault();
    if (isSaving) return;
    isSaving = true;
    saveMessage = null;
    errorMessage = null;

    try {
      await onSaveConfig({
        autoSwitchEnabled: localAutoSwitch,
        lowQuotaThresholdPercent: localLowThreshold,
        minimumCandidateQuotaPercent: localMinCandidate,
        pollingIntervalMs: localPollingSeconds * 1000
      });
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
        />
        <span class="toggle-slider"></span>
      </label>
    </div>

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
          min="2"
          max="60"
          step="1"
          bind:value={localPollingSeconds}
          required
        />
        <span class="form-hint">Telemetry refresh interval from Antigravity 2 loopback.</span>
      </div>
    </div>

    <div class="form-actions">
      <button
        type="submit"
        class="btn btn-primary"
        disabled={isSaving}
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
  </form>
</div>

<style>
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
