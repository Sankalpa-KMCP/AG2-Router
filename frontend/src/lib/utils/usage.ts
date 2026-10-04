/**
 * AG2 Router - Usage dashboard helpers.
 *
 * Honesty rules encoded here:
 * - the headline metric is "Tokens used (conversations)" — never "total usage";
 * - cache reads stay separate from conversation tokens;
 * - HistoricalUnknown usage contributes to totals but never to time buckets;
 * - unattributed usage stays visible and explains per-account vs All Accounts gaps;
 * - no fabricated precision: large numbers format compactly with exact values retained.
 */

import type {
  UsageCollectorStatus,
  UsageModelBreakdownItem,
  UsageSummary,
  UsageTimeBucket,
  UsageTokenTotals,
} from '../api/types.js';

export type UsageAccountOption =
  | { kind: 'all'; label: string }
  | { kind: 'unattributed'; label: string }
  | { kind: 'account'; accountId: string; label: string; isHistorical: boolean };

export function toScopeQuery(option: UsageAccountOption): `all` | 'unattributed' | `account:${string}` {
  switch (option.kind) {
    case 'all':
      return 'all';
    case 'unattributed':
      return 'unattributed';
    case 'account':
      return `account:${option.accountId}`;
  }
}

export function formatTokenCount(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '--';
  if (!Number.isInteger(value) || value < 0) return '--';
  const abs = Math.abs(value);
  if (abs >= 1_000_000_000_000) return `${(value / 1_000_000_000_000).toFixed(1)}T`;
  if (abs >= 1_000_000_000) return `${(value / 1_000_000_000).toFixed(1)}B`;
  if (abs >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`;
  if (abs >= 10_000) return `${(value / 1_000).toFixed(1)}K`;
  return value.toLocaleString('en-US');
}

export function formatExactTokenCount(value: number | null | undefined): string {
  if (typeof value !== 'number' || !Number.isFinite(value) || !Number.isInteger(value) || value < 0) {
    return 'Value unavailable';
  }
  return `${value.toLocaleString('en-US')} tokens`;
}

export function isUsableTotals(totals: unknown): totals is UsageTokenTotals {
  if (!totals || typeof totals !== 'object') return false;
  const t = totals as Record<string, unknown>;
  const numericKeys = [
    'calls', 'conversationTokens', 'inputTokens', 'outputTokens',
    'responseOutputTokens', 'responseOutputCalls', 'thinkingOutputTokens',
    'thinkingOutputCalls', 'cacheReadTokens', 'cacheReportedCalls',
    'cacheUnknownCalls', 'outputMismatchCalls',
  ];
  return numericKeys.every(key => typeof t[key] === 'number' && Number.isFinite(t[key] as number));
}

export function isUsableSummary(summary: unknown): summary is UsageSummary {
  if (!summary || typeof summary !== 'object') return false;
  const s = summary as Record<string, unknown>;
  return isUsableTotals(s.totals) && isUsableTotals(s.historicalUnknown);
}

export function isUsableCollectorStatus(status: unknown): status is UsageCollectorStatus {
  if (!status || typeof status !== 'object') return false;
  const c = status as Record<string, unknown>;
  return typeof c.baselineEstablished === 'boolean' && typeof c.integrityAvailable === 'boolean';
}

export interface UsageBucketView {
  bucketStartUtc: string;
  label: string;
  calls: number;
  conversationTokens: number;
  relativeWidth: number;
}

/**
 * Projects time buckets into bar widths. Returns an empty list for empty data and never
 * produces NaN: with a zero denominator every bar gets width 0.
 */
export function projectBuckets(buckets: UsageTimeBucket[] | null | undefined): UsageBucketView[] {
  if (!buckets || buckets.length === 0) return [];
  const maxTokens = buckets.reduce((max, bucket) =>
    Number.isFinite(bucket.conversationTokens) && bucket.conversationTokens > max ? bucket.conversationTokens : max, 0);
  return buckets.map(bucket => ({
    bucketStartUtc: bucket.bucketStartUtc,
    label: formatBucketLabel(bucket.bucketStartUtc),
    calls: Number.isFinite(bucket.calls) ? bucket.calls : 0,
    conversationTokens: Number.isFinite(bucket.conversationTokens) ? bucket.conversationTokens : 0,
    relativeWidth: maxTokens > 0 && Number.isFinite(bucket.conversationTokens)
      ? Math.max(2, Math.round((bucket.conversationTokens / maxTokens) * 100))
      : 0,
  }));
}

function formatBucketLabel(isoTimestamp: string): string {
  const parsed = new Date(isoTimestamp);
  if (Number.isNaN(parsed.getTime())) return 'Unknown time';
  return parsed.toISOString().slice(0, 10);
}

export interface UsageModelRow {
  label: string;
  isUnknownModel: boolean;
  calls: number;
  conversationTokens: number;
  formattedTokens: string;
  exactTokens: string;
  percentOfTotal: number | null;
  relativeWidth: number;
}

/** Model rows with percentages that are null whenever the denominator is zero. */
export function projectModelRows(models: UsageModelBreakdownItem[] | null | undefined): UsageModelRow[] {
  if (!models || models.length === 0) return [];
  const totalTokens = models.reduce((sum, model) =>
    Number.isFinite(model.conversationTokens) ? sum + model.conversationTokens : sum, 0);
  const maxTokens = models.reduce((max, model) =>
    Number.isFinite(model.conversationTokens) && model.conversationTokens > max ? model.conversationTokens : max, 0);
  return models.map(model => ({
    label: model.isUnknownModel ? 'Unknown model' : (model.modelKey ?? 'Unknown model'),
    isUnknownModel: model.isUnknownModel,
    calls: Number.isFinite(model.calls) ? model.calls : 0,
    conversationTokens: Number.isFinite(model.conversationTokens) ? model.conversationTokens : 0,
    formattedTokens: formatTokenCount(model.conversationTokens),
    exactTokens: formatExactTokenCount(model.conversationTokens),
    percentOfTotal: totalTokens > 0 && Number.isFinite(model.conversationTokens)
      ? Math.round((model.conversationTokens / totalTokens) * 100)
      : null,
    relativeWidth: maxTokens > 0 && Number.isFinite(model.conversationTokens)
      ? Math.max(2, Math.round((model.conversationTokens / maxTokens) * 100))
      : 0,
  }));
}

export type UsagePanelState =
  | { kind: 'loading' }
  | { kind: 'unsupported' }
  | { kind: 'error'; message: string }
  | { kind: 'empty'; reason: 'no-usage' | 'antigravity-unavailable' }
  | { kind: 'ready' };

/** Derives the panel state from validated responses; unsupported backends stay explicit. */
export function derivePanelState(input: {
  summaryFetched: boolean;
  unsupported: boolean;
  errorMessage: string | null;
  collector: UsageCollectorStatus | null;
  totals: UsageTokenTotals | null;
}): UsagePanelState {
  if (input.unsupported) return { kind: 'unsupported' };
  if (input.errorMessage) return { kind: 'error', message: input.errorMessage };
  if (!input.summaryFetched || !input.totals) return { kind: 'loading' };
  if (input.totals.calls === 0) {
    return input.collector !== null && !input.collector.integrityAvailable
      ? { kind: 'empty', reason: 'antigravity-unavailable' }
      : { kind: 'empty', reason: 'no-usage' };
  }
  return { kind: 'ready' };
}

export function hasHistoricalUnknown(summary: UsageSummary | null): boolean {
  return !!summary && isUsableTotals(summary.historicalUnknown) && summary.historicalUnknown.calls > 0;
}

export function hasUnattributedRemainder(summary: UsageSummary | null): boolean {
  return !!summary && !!summary.unattributed && isUsableTotals(summary.unattributed) && summary.unattributed.calls > 0;
}
