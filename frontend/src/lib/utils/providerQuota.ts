import type { CanonicalModelDto, QuotaSnapshotDto, RawModelQuotaDto } from '../api/types.js';

export type ProviderName = 'Gemini' | 'Claude';
export type ProviderHealth = 'exhausted' | 'low' | 'healthy' | 'unknown';

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

const PROVIDERS: ProviderName[] = ['Gemini', 'Claude'];

function providerFor(identity: string): ProviderName | null {
  if (/(^|[^a-z])gemini(?=$|[^a-z])/i.test(identity)) return 'Gemini';
  if (/(^|[^a-z])claude(?=$|[^a-z])/i.test(identity)) return 'Claude';
  return null;
}

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
    identity: `${model.modelOrTier} ${model.label}`,
    remainingFraction: model.remainingFraction,
    resetTime: model.resetTime,
    isExhausted: model.isExhausted
  }));
}

function summarizeReset(rows: ModelRow[]): string {
  if (rows.length === 0) return 'No reset data';
  const times = rows.map(row => row.resetTime?.trim()).filter((time): time is string => Boolean(time));
  if (times.length === 0) return 'Reset unavailable';
  if (times.length !== rows.length) return 'Reset windows vary or unknown';
  if (new Set(times).size !== 1) return 'Multiple reset windows';
  const parsed = new Date(times[0]);
  return Number.isNaN(parsed.getTime()) ? 'Reset time unavailable' : `Resets ${parsed.toLocaleString()}`;
}

export function summarizeProviderQuotas(
  quota: QuotaSnapshotDto | null,
  lowThresholdPercent = 15
): ProviderQuotaSummary[] {
  const rows = modelRows(quota);
  return PROVIDERS.map(provider => {
    const selected = rows.filter(row => providerFor(row.identity) === provider);
    const exhausted = selected.some(row => row.isExhausted ||
      (row.remainingFraction !== null && Number.isFinite(row.remainingFraction) && row.remainingFraction <= 0));
    const unknownCount = selected.filter(row => !row.isExhausted &&
      (row.remainingFraction === null || !Number.isFinite(row.remainingFraction))).length;
    const known = selected.filter(row => row.remainingFraction !== null && Number.isFinite(row.remainingFraction));
    // A known healthy row cannot conceal another unknown or exhausted row.
    const percent = exhausted ? 0 : selected.length === 0 || unknownCount > 0 || known.length !== selected.length
      ? null
      : Math.round(Math.max(0, Math.min(1, Math.min(...known.map(row => row.remainingFraction!)))) * 100);
    const health: ProviderHealth = exhausted ? 'exhausted' : percent === null ? 'unknown'
      : percent <= lowThresholdPercent ? 'low' : 'healthy';
    return {
      provider,
      percent,
      health,
      modelCount: selected.length,
      unknownCount,
      resetSummary: summarizeReset(selected)
    };
  });
}
