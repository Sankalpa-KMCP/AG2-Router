import type { CanonicalModelDto, QuotaSnapshotDto, RawModelQuotaDto } from '../api/types.js';

export type ProviderName = 'Gemini' | 'Claude';
export type FamilyHealth = 'exhausted' | 'low' | 'healthy' | 'unknown';
export type ProviderHealth = FamilyHealth;

export type ModelFamilyKey = 'gemini-flash' | 'gemini-pro' | 'claude-opus' | 'claude-sonnet';

export interface ModelFamilyDefinition {
  key: ModelFamilyKey;
  displayName: string;
  provider: ProviderName;
  matcher: (identity: string) => boolean;
}

export interface ModelFamilySummary {
  key: ModelFamilyKey;
  displayName: string;
  provider: ProviderName;
  percent: number | null;
  health: FamilyHealth;
  modelCount: number;
  unknownCount: number;
  resetSummary: string;
  isExhausted: boolean;
}

export interface OverallCapacitySummary {
  percent: number | null;
  health: FamilyHealth;
  bottleneckFamily: string | null;
  observedFamiliesCount: number;
  totalModelsObserved: number;
  hasUnknownQuota: boolean;
  isExhausted: boolean;
  resetSummary: string;
}

export interface ProviderQuotaSummary {
  provider: ProviderName;
  percent: number | null;
  health: ProviderHealth;
  modelCount: number;
  unknownCount: number;
  resetSummary: string;
}

type ModelRow = Pick<RawModelQuotaDto, 'remainingFraction' | 'resetTime' | 'isExhausted'> & {
  identity: string;
};

/**
 * Verified model families supported by current telemetry evidence.
 * Matched conservatively against model keys and display labels.
 */
export const MODEL_FAMILIES: ModelFamilyDefinition[] = [
  {
    key: 'gemini-flash',
    displayName: 'Gemini 3.6 Flash',
    provider: 'Gemini',
    matcher: (id: string) =>
      /(^|[^a-z])gemini(?=$|[^a-z])/i.test(id) &&
      /(^|[^a-z])flash(?=$|[^a-z])/i.test(id) &&
      /(^|[^0-9])3[._\s-]6([^0-9]|$)/.test(id) &&
      !/(^|[^a-z])pro(?=$|[^a-z])/i.test(id)
  },
  {
    key: 'gemini-pro',
    displayName: 'Gemini 3.1 Pro',
    provider: 'Gemini',
    matcher: (id: string) =>
      /(^|[^a-z])gemini(?=$|[^a-z])/i.test(id) &&
      /(^|[^a-z])pro(?=$|[^a-z])/i.test(id) &&
      /(^|[^0-9])3[._\s-]1([^0-9]|$)/.test(id) &&
      !/(^|[^a-z])flash(?=$|[^a-z])/i.test(id)
  },
  {
    key: 'claude-opus',
    displayName: 'Opus 4.6',
    provider: 'Claude',
    matcher: (id: string) =>
      /(^|[^a-z])opus(?=$|[^a-z])/i.test(id) &&
      /(^|[^0-9])4[._\s-]6([^0-9]|$)/.test(id) &&
      !/(^|[^a-z])sonnet(?=$|[^a-z])/i.test(id)
  },
  {
    key: 'claude-sonnet',
    displayName: 'Sonnet 4.6',
    provider: 'Claude',
    matcher: (id: string) =>
      /(^|[^a-z])sonnet(?=$|[^a-z])/i.test(id) &&
      /(^|[^0-9])4[._\s-]6([^0-9]|$)/.test(id) &&
      !/(^|[^a-z])opus(?=$|[^a-z])/i.test(id)
  }
];

function modelRows(quota: QuotaSnapshotDto | null): ModelRow[] {
  if (!quota) return [];
  if (quota.canonicalModels?.length) {
    return quota.canonicalModels.map((model: CanonicalModelDto) => ({
      identity: `${model.canonicalKey} ${model.displayLabel}`,
      remainingFraction: model.remainingFraction,
      resetTime: model.resetTime,
      isExhausted: model.isExhausted
    }));
  }
  return quota.models.map(model => ({
    identity: `${model.modelOrTier || ''} ${model.label}`,
    remainingFraction: model.remainingFraction,
    resetTime: model.resetTime,
    isExhausted: model.isExhausted
  }));
}

/**
 * Formats a reset timestamp truthfully as a relative countdown without guessing window types.
 */
export function formatResetTime(resetTime?: string | null, relativeTo: Date = new Date()): string {
  if (!resetTime) return 'No reset scheduled';
  try {
    const target = new Date(resetTime);
    if (isNaN(target.getTime())) return resetTime;

    const diffMs = target.getTime() - relativeTo.getTime();
    if (diffMs <= 0) return 'Reset due';

    const diffMins = Math.floor(diffMs / (1000 * 60));
    const hours = Math.floor(diffMins / 60);
    const mins = diffMins % 60;
    const days = Math.floor(hours / 24);

    if (days > 0) {
      const remainingHours = hours % 24;
      return `Resets in ${days}d ${remainingHours}h`;
    }
    if (hours > 0) {
      return `Resets in ${hours}h ${mins}m`;
    }
    return `Resets in ${mins}m`;
  } catch {
    return resetTime;
  }
}

/**
 * Summarizes reset times across model rows, preserving distinct reset windows truthfully.
 */
export function summarizeReset(rows: ModelRow[], relativeTo: Date = new Date()): string {
  if (rows.length === 0) return 'No reset data';
  const times = rows.map(row => row.resetTime?.trim()).filter((time): time is string => Boolean(time));
  if (times.length === 0) return 'No reset scheduled';
  if (times.length !== rows.length) return 'Reset windows vary or unknown';
  if (new Set(times).size !== 1) return 'Multiple reset windows';
  return formatResetTime(times[0], relativeTo);
}

/**
 * Summarizes model families observed in telemetry.
 * Returns only families that have matching observed model rows (no empty fake cards).
 */
export function summarizeModelFamilies(
  quota: QuotaSnapshotDto | null,
  lowThresholdPercent = 15,
  relativeTo: Date = new Date()
): ModelFamilySummary[] {
  const rows = modelRows(quota);
  if (rows.length === 0) return [];

  const summaries: ModelFamilySummary[] = [];

  for (const def of MODEL_FAMILIES) {
    const selected = rows.filter(row => def.matcher(row.identity));
    if (selected.length === 0) {
      // Do not create an empty fake card for a family not observed
      continue;
    }

    const exhausted = selected.some(
      row => row.isExhausted ||
        (row.remainingFraction !== null && Number.isFinite(row.remainingFraction) && row.remainingFraction <= 0)
    );
    const unknownCount = selected.filter(
      row => !row.isExhausted &&
        (row.remainingFraction === null || !Number.isFinite(row.remainingFraction))
    ).length;
    const known = selected.filter(
      row => row.remainingFraction !== null && Number.isFinite(row.remainingFraction)
    );

    // A known healthy row cannot conceal another unknown or exhausted row.
    const percent = exhausted
      ? 0
      : unknownCount > 0 || known.length !== selected.length
      ? null
      : Math.round(Math.max(0, Math.min(1, Math.min(...known.map(r => r.remainingFraction!)))) * 100);

    const health: FamilyHealth = exhausted
      ? 'exhausted'
      : percent === null
      ? 'unknown'
      : percent <= lowThresholdPercent
      ? 'low'
      : 'healthy';

    summaries.push({
      key: def.key,
      displayName: def.displayName,
      provider: def.provider,
      percent,
      health,
      modelCount: selected.length,
      unknownCount,
      resetSummary: summarizeReset(selected, relativeTo),
      isExhausted: exhausted
    });
  }

  return summaries;
}

/**
 * Derives overall capacity conservatively as the bottleneck (minimum observed)
 * capacity across all observed model families.
 */
export function deriveOverallCapacity(
  families: ModelFamilySummary[],
  lowThresholdPercent = 15
): OverallCapacitySummary {
  if (families.length === 0) {
    return {
      percent: null,
      health: 'unknown',
      bottleneckFamily: null,
      observedFamiliesCount: 0,
      totalModelsObserved: 0,
      hasUnknownQuota: false,
      isExhausted: false,
      resetSummary: 'No telemetry available'
    };
  }

  const isExhausted = families.some(f => f.isExhausted || (f.percent !== null && f.percent <= 0));
  const hasUnknownQuota = families.some(f => f.percent === null && !f.isExhausted);
  const knownFamilies = families.filter(f => f.percent !== null && Number.isFinite(f.percent));

  let percent: number | null = null;
  let bottleneckFamily: string | null = null;

  if (isExhausted) {
    percent = 0;
    const exhaustedFam = families.find(f => f.isExhausted || (f.percent !== null && f.percent <= 0));
    bottleneckFamily = exhaustedFam ? exhaustedFam.displayName : null;
  } else if (hasUnknownQuota || knownFamilies.length !== families.length) {
    // If any required observations are unknown, do not display false 100%/healthy state
    percent = null;
    const unknownFam = families.find(f => f.percent === null);
    bottleneckFamily = unknownFam ? unknownFam.displayName : null;
  } else {
    let minVal = 100;
    for (const fam of knownFamilies) {
      if (fam.percent! < minVal) {
        minVal = fam.percent!;
        bottleneckFamily = fam.displayName;
      }
    }
    if (!bottleneckFamily && knownFamilies.length > 0) {
      bottleneckFamily = knownFamilies[0].displayName;
      minVal = knownFamilies[0].percent!;
    }
    percent = minVal;
  }

  const health: FamilyHealth = isExhausted
    ? 'exhausted'
    : percent === null
    ? 'unknown'
    : percent <= lowThresholdPercent
    ? 'low'
    : 'healthy';

  const totalModelsObserved = families.reduce((acc, f) => acc + f.modelCount, 0);

  const resetSummaries = families
    .map(f => f.resetSummary)
    .filter(s => s && s !== 'No reset scheduled' && s !== 'No reset data');
  const distinctResets = new Set(resetSummaries);
  const resetSummary = distinctResets.size === 1
    ? Array.from(distinctResets)[0]
    : distinctResets.size > 1
    ? 'Multiple reset windows'
    : 'No reset scheduled';

  return {
    percent,
    health,
    bottleneckFamily,
    observedFamiliesCount: families.length,
    totalModelsObserved,
    hasUnknownQuota,
    isExhausted,
    resetSummary
  };
}

/**
 * Backward-compatible provider-level summaries (Gemini and Claude).
 */
export function summarizeProviderQuotas(
  quota: QuotaSnapshotDto | null,
  lowThresholdPercent = 15,
  relativeTo: Date = new Date()
): ProviderQuotaSummary[] {
  const rows = modelRows(quota);
  const PROVIDERS: ProviderName[] = ['Gemini', 'Claude'];
  return PROVIDERS.map(provider => {
    const selected = rows.filter(row => {
      if (provider === 'Gemini') return /(^|[^a-z])gemini(?=$|[^a-z])/i.test(row.identity);
      if (provider === 'Claude') return /(^|[^a-z])(claude|opus|sonnet)(?=$|[^a-z])/i.test(row.identity);
      return false;
    });
    const exhausted = selected.some(
      row => row.isExhausted ||
        (row.remainingFraction !== null && Number.isFinite(row.remainingFraction) && row.remainingFraction <= 0)
    );
    const unknownCount = selected.filter(
      row => !row.isExhausted &&
        (row.remainingFraction === null || !Number.isFinite(row.remainingFraction))
    ).length;
    const known = selected.filter(
      row => row.remainingFraction !== null && Number.isFinite(row.remainingFraction)
    );
    const percent = exhausted
      ? 0
      : selected.length === 0 || unknownCount > 0 || known.length !== selected.length
      ? null
      : Math.round(Math.max(0, Math.min(1, Math.min(...known.map(r => r.remainingFraction!)))) * 100);
    const health: ProviderHealth = exhausted
      ? 'exhausted'
      : percent === null
      ? 'unknown'
      : percent <= lowThresholdPercent
      ? 'low'
      : 'healthy';
    return {
      provider,
      percent,
      health,
      modelCount: selected.length,
      unknownCount,
      resetSummary: summarizeReset(selected, relativeTo)
    };
  });
}
