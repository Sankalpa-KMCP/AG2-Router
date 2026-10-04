<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import {
    UsageUnavailableError,
    api,
  } from '../api/client.js';
  import type {
    UsageCollectorStatus,
    UsageModelBreakdownItem,
    UsageRange,
    UsageSummary,
    UsageTimeBucket,
  } from '../api/types.js';
  import { USAGE_RANGES } from '../api/types.js';
  import {
    derivePanelState,
    formatExactTokenCount,
    formatTokenCount,
    hasHistoricalUnknown,
    hasUnattributedRemainder,
    isUsableCollectorStatus,
    isUsableSummary,
    projectBuckets,
    projectModelRows,
    toScopeQuery,
    type UsageAccountOption,
  } from '../utils/usage.js';
  import { createUsageRequestController } from '../utils/usageRequests.js';
  import type { AccountMetadata } from '../api/types.js';

  interface Props {
    accounts: AccountMetadata[];
  }

  let { accounts = [] }: Props = $props();

  let isLoading = $state(true);
  let errorMessage = $state<string | null>(null);
  let unsupported = $state(false);
  let summary = $state<UsageSummary | null>(null);
  let collector = $state<UsageCollectorStatus | null>(null);
  let buckets = $state<UsageTimeBucket[]>([]);
  let models = $state<UsageModelBreakdownItem[]>([]);

  let selectedScope = $state<UsageAccountOption>({ kind: 'all', label: 'All Accounts' });
  let selectedRange = $state<UsageRange>('7d');

  const accountOptions: UsageAccountOption[] = $derived.by(() => {
    const options: UsageAccountOption[] = [{ kind: 'all', label: 'All Accounts' }];
    for (const account of accounts) {
      const label = account.alias?.trim() || account.name?.trim() || account.email;
      options.push({ kind: 'account', accountId: account.id, label, isHistorical: false });
    }
    options.push({ kind: 'unattributed', label: 'Unattributed' });
    return options;
  });

  const panelState = $derived(derivePanelState({
    summaryFetched: summary !== null,
    unsupported,
    errorMessage,
    collector,
    totals: summary?.totals ?? null,
  }));

  const bucketViews = $derived(projectBuckets(buckets));
  const modelRows = $derived(projectModelRows(models));
  const showHistoricalNotice = $derived(hasHistoricalUnknown(summary));
  const showUnattributedNotice = $derived(hasUnattributedRemainder(summary));

  function accountLabel(accountId: string | null): string {
    if (!accountId) return 'Unattributed';
    const known = accounts.find(account => account.id === accountId);
    if (known) {
      return known.alias?.trim() || known.name?.trim() || known.email;
    }
    return 'Historical account';
  }

  const requestController = createUsageRequestController({
    beginRequest: () => {
      isLoading = true;
      errorMessage = null;
      unsupported = false;
    },
    applySuccess: payload => {
      summary = payload.summary;
      collector = payload.collector;
      buckets = payload.buckets;
      models = payload.models;
    },
    applyUnsupported: () => {
      unsupported = true;
    },
    applyError: message => {
      errorMessage = message;
    },
    finalizeRequest: () => {
      isLoading = false;
    },
  });

  async function loadUsage(): Promise<void> {
    // Selection is captured at request start; a concurrent selection change starts a newer
    // request whose outcome alone may mutate state (stale outcomes are dropped by the controller).
    const scope = toScopeQuery(selectedScope);
    const range = selectedRange;
    await requestController.run(async () => {
      const responses = await Promise.all([
        api.getUsageSummary(scope),
        api.getUsageTimeSeries(range, scope),
        api.getUsageModels(scope),
      ]).catch((err: unknown) => {
        if (err instanceof UsageUnavailableError) {
          return { unsupported: true as const };
        }
        throw err;
      });
      if ('unsupported' in responses) {
        return { kind: 'unsupported' as const };
      }
      const [summaryResponse, timeseriesResponse, modelsResponse] = responses;
      if (!isUsableSummary(summaryResponse.summary)) {
        throw new Error('The usage response could not be validated.');
      }
      return {
        kind: 'success' as const,
        summary: summaryResponse.summary,
        collector: isUsableCollectorStatus(summaryResponse.collector) ? summaryResponse.collector : null,
        buckets: Array.isArray(timeseriesResponse.timeseries?.buckets) ? timeseriesResponse.timeseries.buckets : [],
        models: Array.isArray(modelsResponse.models?.models) ? modelsResponse.models.models : [],
      };
    }).catch(() => {
      // Network-level failures surface as errors via the controller's fallback; the
      // unsupported capability is translated below before the controller sees it.
    });
  }

  onDestroy(() => {
    requestController.invalidate();
  });

  function onScopeChange(event: Event): void {
    const target = event.currentTarget as HTMLSelectElement;
    const option = accountOptions.find(option =>
      (option.kind === 'account' ? `account:${option.accountId}` : option.kind) === target.value);
    if (option) {
      selectedScope = option;
      void loadUsage();
    }
  }

  function onRangeChange(event: Event): void {
    const target = event.currentTarget as HTMLSelectElement;
    const value = target.value as UsageRange;
    selectedRange = value;
    void loadUsage();
  }

  onMount(() => {
    void loadUsage();
  });
</script>

<section class="usage-section" aria-labelledby="usage-heading">
  <div class="usage-header">
    <div>
      <h3 id="usage-heading" class="usage-heading">Tokens used (conversations)</h3>
      <p class="usage-caption">
        Provider-reported per-call token counts across all detected Antigravity instances.
        Cache reads are shown separately. Usage outside conversations is not included.
      </p>
    </div>
    <div class="usage-controls">
      <label class="usage-control">
        <span class="control-label">Account</span>
        <select class="input-select" onchange={onScopeChange}>
          {#each accountOptions as option (option.kind === 'account' ? option.accountId : option.kind)}
            <option value={option.kind === 'account' ? `account:${option.accountId}` : option.kind}>
              {option.label}
            </option>
          {/each}
        </select>
      </label>
      <label class="usage-control">
        <span class="control-label">Range</span>
        <select class="input-select" onchange={onRangeChange}>
          {#each USAGE_RANGES as rangeOption (rangeOption)}
            <option value={rangeOption}>{rangeOption}</option>
          {/each}
        </select>
      </label>
      <button type="button" class="btn btn-secondary btn-sm" onclick={() => void loadUsage()}>
        Refresh
      </button>
    </div>
  </div>

  {#if panelState.kind === 'loading'}
    <div class="usage-state" role="status">Loading usage…</div>
  {:else if panelState.kind === 'unsupported'}
    <div class="usage-state" role="status">
      Usage accounting is not available on this backend. Run the native AG2 Router app to see
      conversation token usage.
    </div>
  {:else if panelState.kind === 'error'}
    <div class="usage-state usage-state-error" role="alert">
      {panelState.message}
      <button type="button" class="btn btn-secondary btn-sm" onclick={() => void loadUsage()}>Retry</button>
    </div>
  {:else if panelState.kind === 'empty'}
    <div class="usage-state" role="status">
      {panelState.reason === 'antigravity-unavailable'
        ? 'No usage collected yet. Connect Antigravity and run a conversation to begin.'
        : 'No conversation usage has been observed yet.'}
    </div>
  {:else if summary}
    {#if showHistoricalNotice}
      <div class="usage-notice" role="note">
        Historical usage without call timestamps is included in totals but not in the timeline.
      </div>
    {/if}

    <div class="usage-cards">
      <div class="usage-card usage-card-primary" title={formatExactTokenCount(summary.totals.conversationTokens)}>
        <span class="card-label">Conversation tokens</span>
        <span class="card-value">{formatTokenCount(summary.totals.conversationTokens)}</span>
        <span class="card-detail">{formatExactTokenCount(summary.totals.conversationTokens)}</span>
      </div>
      <div class="usage-card" title={formatExactTokenCount(summary.totals.inputTokens)}>
        <span class="card-label">Input</span>
        <span class="card-value">{formatTokenCount(summary.totals.inputTokens)}</span>
      </div>
      <div class="usage-card" title={formatExactTokenCount(summary.totals.outputTokens)}>
        <span class="card-label">Output</span>
        <span class="card-value">{formatTokenCount(summary.totals.outputTokens)}</span>
      </div>
      <div class="usage-card" title={formatExactTokenCount(summary.totals.thinkingOutputTokens)}>
        <span class="card-label">Thinking</span>
        <span class="card-value">{formatTokenCount(summary.totals.thinkingOutputTokens)}</span>
      </div>
      <div class="usage-card usage-card-cache" title={formatExactTokenCount(summary.totals.cacheReadTokens)}>
        <span class="card-label">Cache reads (separate)</span>
        <span class="card-value">{formatTokenCount(summary.totals.cacheReadTokens)}</span>
        <span class="card-detail">
          {summary.totals.cacheReportedCalls} reported · {summary.totals.cacheUnknownCalls} unreported
        </span>
      </div>
      <div class="usage-card" title={`${summary.totals.calls} conversation model calls`}>
        <span class="card-label">Calls</span>
        <span class="card-value">{formatTokenCount(summary.totals.calls)}</span>
      </div>
    </div>

    <div class="usage-grid">
      <div class="usage-panel">
        <h4 class="panel-title">Trend ({selectedRange})</h4>
        {#if bucketViews.length === 0}
          <p class="panel-empty">No observation-time usage in this range.</p>
        {:else}
          <ul class="trend-list" aria-label="Conversation tokens over time">
            {#each bucketViews as bucket (bucket.bucketStartUtc)}
              <li class="trend-row">
                <span class="trend-label">{bucket.label}</span>
                <span class="trend-bar-track">
                  <span class="trend-bar" style="width: {bucket.relativeWidth}%"></span>
                </span>
                <span class="trend-value" title={formatExactTokenCount(bucket.conversationTokens)}>
                  {formatTokenCount(bucket.conversationTokens)} · {bucket.calls} calls
                </span>
              </li>
            {/each}
          </ul>
        {/if}
      </div>

      <div class="usage-panel">
        <h4 class="panel-title">By model</h4>
        {#if modelRows.length === 0}
          <p class="panel-empty">No model usage recorded.</p>
        {:else}
          <ul class="model-list" aria-label="Conversation tokens by model">
            {#each modelRows as model (model.label)}
              <li class="model-row">
                <span class="model-label" class:model-unknown={model.isUnknownModel}>{model.label}</span>
                <span class="model-bar-track">
                  <span class="model-bar" style="width: {model.relativeWidth}%"></span>
                </span>
                <span class="model-value" title={model.exactTokens}>
                  {model.formattedTokens}{model.percentOfTotal !== null ? ` · ${model.percentOfTotal}%` : ''}
                </span>
              </li>
            {/each}
          </ul>
        {/if}
      </div>

      {#if selectedScope.kind === 'all' && (summary.accounts?.length ?? 0) > 0}
        <div class="usage-panel">
          <h4 class="panel-title">By account</h4>
          <ul class="account-list" aria-label="Conversation tokens by account">
            {#each summary.accounts ?? [] as accountRow (accountRow.accountId ?? 'unattributed')}
              <li class="account-row">
                <span class="account-label" class:account-unattributed={accountRow.isUnattributed}>
                  {accountRow.isUnattributed ? 'Unattributed' : accountLabel(accountRow.accountId)}
                </span>
                <span class="account-value">{formatTokenCount(accountRow.conversationTokens)} · {accountRow.calls} calls</span>
              </li>
            {/each}
          </ul>
          {#if showUnattributedNotice}
            <p class="panel-note">
              Unattributed usage counts toward All Accounts but not toward any single account,
              so account totals may sum to less than the global total.
            </p>
          {/if}
        </div>
      {/if}
    </div>

    {#if collector}
      <p class="collector-line">
        Collector:
        {#if !collector.integrityAvailable}
          integrity issue detected — history is preserved but collection is paused.
        {:else if collector.lastSuccessfulCollectionAtUtc}
          last observation {new Date(collector.lastSuccessfulCollectionAtUtc).toLocaleString()}
          ({collector.instancesHealthy}/{collector.instancesDiscovered} instances healthy).
        {:else}
          awaiting first successful observation.
        {/if}
      </p>
    {/if}
  {/if}
</section>

<style>
  .usage-section { display: flex; flex-direction: column; gap: var(--space-4); }
  .usage-header { display: flex; align-items: flex-start; justify-content: space-between; gap: var(--space-4); flex-wrap: wrap; }
  .usage-heading { margin: 0; font-size: 18px; font-weight: 700; letter-spacing: -0.02em; }
  .usage-caption { margin: 4px 0 0; color: var(--color-text-muted); font-size: 12px; max-width: 640px; }
  .usage-controls { display: flex; align-items: end; gap: var(--space-2); flex-wrap: wrap; }
  .usage-control { display: flex; flex-direction: column; gap: 3px; }
  .control-label { font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--color-text-muted); }
  .input-select { background: var(--color-surface); color: var(--color-text-primary); border: 1px solid var(--color-card-border); border-radius: var(--radius-sm); padding: 6px 8px; font-size: 12px; }

  .usage-state { padding: var(--space-5); border: 1px dashed var(--color-card-border); border-radius: var(--radius-md); color: var(--color-text-muted); font-size: 13px; display: flex; gap: var(--space-3); align-items: center; }
  .usage-state-error { border-color: var(--color-danger-border); color: var(--color-danger-text); background: var(--color-danger-subtle); }
  .usage-notice { padding: 10px 14px; border-radius: var(--radius-md); background: var(--color-warning-subtle); border: 1px solid var(--color-warning-border); color: var(--color-warning-text); font-size: 12px; }

  .usage-cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: var(--space-3); }
  .usage-card { display: flex; flex-direction: column; gap: 4px; padding: 14px 16px; background: var(--color-surface); border: 1px solid var(--color-card-border); border-radius: var(--radius-md); min-width: 0; }
  .usage-card-primary { border-color: var(--color-primary); }
  .usage-card-cache { border-style: dashed; }
  .card-label { font-size: 10px; text-transform: uppercase; letter-spacing: 0.08em; color: var(--color-text-muted); }
  .card-value { font-size: 22px; font-weight: 700; letter-spacing: -0.02em; color: var(--color-text-primary); }
  .card-detail { font-size: 11px; color: var(--color-text-muted); }

  .usage-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: var(--space-3); }
  .usage-panel { padding: 14px 16px; background: var(--color-surface); border: 1px solid var(--color-card-border); border-radius: var(--radius-md); min-width: 0; }
  .panel-title { margin: 0 0 10px; font-size: 12px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.08em; color: var(--color-text-secondary); }
  .panel-empty { margin: 0; font-size: 12px; color: var(--color-text-muted); }
  .panel-note { margin: 10px 0 0; font-size: 11px; color: var(--color-text-muted); }

  .trend-list, .model-list, .account-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
  .trend-row, .model-row, .account-row { display: grid; grid-template-columns: minmax(64px, auto) 1fr minmax(90px, auto); align-items: center; gap: 8px; font-size: 12px; min-width: 0; }
  .trend-label, .model-label, .account-label { color: var(--color-text-secondary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .model-unknown { font-style: italic; }
  .account-unattributed { font-style: italic; color: var(--color-warning-text); }
  .trend-bar-track, .model-bar-track { display: block; height: 8px; background: var(--color-surface-hover); border-radius: var(--radius-full); overflow: hidden; }
  .trend-bar, .model-bar { display: block; height: 100%; background: var(--color-primary); border-radius: var(--radius-full); }
  .trend-value, .model-value, .account-value { color: var(--color-text-primary); white-space: nowrap; }

  .collector-line { margin: 0; font-size: 11px; color: var(--color-text-muted); }
</style>
