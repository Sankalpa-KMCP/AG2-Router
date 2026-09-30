import type { CandidateQuotaStatusDto, QuotaSnapshotDto } from '../api/types.js';

/** Backend model-key syntax; display/grouping labels are never routing keys. */
export function workloadModelKey(value: string): string | null {
  const key = value.trim().toLowerCase();
  if (!key) return null;
  if (key.length > 128 || !/^[a-z0-9_.:/@-]+$/.test(key)) throw new Error('Enter a valid model key (letters, digits, _ - . : / @; at most 128 characters).');
  return key;
}

export function observedWorkloadModels(quota: QuotaSnapshotDto | null): string[] {
  const keys = new Set<string>();
  for (const row of quota?.models ?? []) {
    if (typeof row.modelOrTier !== 'string') continue;
    try { const key = workloadModelKey(row.modelOrTier); if (key) keys.add(key); } catch { /* Invalid telemetry is not an option. */ }
  }
  return [...keys].sort();
}

/** Present server-owned states without deriving a second freshness/eligibility policy. */
export function candidateEvidenceCopy(row: CandidateQuotaStatusDto): string {
  switch (row.state) {
    case 'UNCONFIGURED': return 'Select a workload model';
    case 'NOT_OBSERVED': return 'Not yet observed — use this account manually to obtain live quota';
    case 'UNKNOWN': return 'Quota unknown — fresh live telemetry required';
    case 'STALE': return 'Stale observation — fresh live telemetry required';
    case 'INVALID': return 'Invalid observation — fresh live telemetry required';
    case 'EXHAUSTED': return 'Exhausted — a reset alone does not prove available quota';
    case 'BELOW_MINIMUM': return 'Below minimum candidate quota';
    case 'USABLE': return 'Recent quota meets minimum — other safety checks still apply';
    default: return 'Evidence unavailable';
  }
}
