/**
 * AG2 Router - Frontend Dashboard Business Logic & Truthful Telemetry Helpers
 */

export interface AccountDisplayNameInput {
  email?: string | null;
  name?: string | null;
  alias?: string | null;
}

/**
 * Resolves the friendly display name for an account following the strict fallback hierarchy:
 * Alias -> Name -> Email
 */
export function resolveAccountDisplayName(account?: AccountDisplayNameInput | null): string {
  if (!account) return 'Unknown Account';
  const alias = typeof account.alias === 'string' ? account.alias.trim() : '';
  if (alias.length > 0) return alias;

  const name = typeof account.name === 'string' ? account.name.trim() : '';
  if (name.length > 0) return name;

  return account.email || 'Unnamed Account';
}

/**
 * Returns the subtitle for an account (e.g. email when alias or name is displayed).
 */
export function getAccountSubtitle(account?: AccountDisplayNameInput | null): string | null {
  if (!account) return null;
  const alias = typeof account.alias === 'string' ? account.alias.trim() : '';
  const name = typeof account.name === 'string' ? account.name.trim() : '';

  if ((alias.length > 0 || name.length > 0) && account.email) {
    return account.email;
  }
  return null;
}

/**
 * Formats a fractional quota [0.0, 1.0] into a clean integer percentage [0, 100].
 * Clamps strictly to [0, 100].
 */
export function formatQuotaFraction(fraction: number): number {
  if (typeof fraction !== 'number' || isNaN(fraction)) return 0;
  const clamped = Math.max(0, Math.min(1, fraction));
  return Math.round(clamped * 100);
}

export interface LowestQuotaSummary {
  fraction: number;
  percent: number;
  label: string;
  isExhausted: boolean;
}

export interface ModelQuotaLike {
  remainingFraction: number;
  isExhausted?: boolean;
  label?: string;
  displayLabel?: string;
  modelOrTier?: string;
}

/**
 * Derives the lowest remaining quota across model pools truthfully without fabricating totals.
 * CRITICAL RULE: Never sums percentages across models into a fake "Total Quota".
 */
export function deriveLowestModelQuota(
  models?: ModelQuotaLike[] | null
): LowestQuotaSummary | null {
  if (!models || models.length === 0) return null;

  let lowestItem: ModelQuotaLike | null = null;
  let minFraction = Infinity;

  for (const item of models) {
    const fraction = item.remainingFraction;
    if (fraction < minFraction) {
      minFraction = fraction;
      lowestItem = item;
    }
  }

  if (!lowestItem) return null;

  let label = 'Model';
  if (lowestItem.displayLabel && typeof lowestItem.displayLabel === 'string') {
    label = lowestItem.displayLabel;
  } else if (lowestItem.label && typeof lowestItem.label === 'string') {
    label = lowestItem.label;
  } else if (lowestItem.modelOrTier && typeof lowestItem.modelOrTier === 'string') {
    label = lowestItem.modelOrTier;
  }

  return {
    fraction: minFraction,
    percent: formatQuotaFraction(minFraction),
    label,
    isExhausted: Boolean(lowestItem.isExhausted || minFraction <= 0)
  };
}

/**
 * Formats a reset timestamp truthfully without guessing "Weekly" or "5-Hour" window labels.
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
 * Normalizes input for the PATCH /api/accounts/{id} alias endpoint.
 * Trims whitespace; empty strings normalize to empty string (which clears the alias in the store).
 */
export function prepareAliasPayload(input: string): { alias: string } {
  return { alias: input.trim() };
}
