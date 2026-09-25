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
export function formatQuotaFraction(fraction: number | null | undefined): number | null {
  if (typeof fraction !== 'number' || !Number.isFinite(fraction)) return null;
  const clamped = Math.max(0, Math.min(1, fraction));
  return Math.round(clamped * 100);
}

export interface LowestQuotaSummary {
  fraction: number | null;
  percent: number | null;
  label: string;
  isExhausted: boolean;
}

export interface ModelQuotaLike {
  remainingFraction: number | null;
  isExhausted?: boolean;
  label?: string;
  displayLabel?: string;
  modelOrTier?: string | null;
}

/**
 * Derives the lowest remaining quota across model pools truthfully without fabricating totals.
 * CRITICAL RULE: Never sums percentages across models into a fake "Total Quota".
 */
export function deriveLowestModelQuota(
  models?: ModelQuotaLike[] | null
): LowestQuotaSummary | null {
  if (!models || models.length === 0) return null;

  const exhausted = models.find(item => item.isExhausted ||
    (typeof item.remainingFraction === 'number' && Number.isFinite(item.remainingFraction) && item.remainingFraction <= 0));
  if (exhausted) {
    const fraction = Number.isFinite(exhausted.remainingFraction) ? exhausted.remainingFraction : null;
    return {
      fraction,
      percent: formatQuotaFraction(fraction),
      label: exhausted.displayLabel || exhausted.label || exhausted.modelOrTier || 'Model',
      isExhausted: true
    };
  }

  if (models.some(item => item.remainingFraction === null || !Number.isFinite(item.remainingFraction))) return null;

  let lowestItem: ModelQuotaLike | null = null;
  let minFraction = Infinity;

  for (const item of models) {
    const fraction = item.remainingFraction;
    if (fraction !== null && fraction < minFraction) {
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

export interface PoolsStatusSummary {
  badgeText: string;
  badgeClass: string;
  metricText: string;
  metricClass: string;
}

/**
 * Derives the truthful status summary for model quota pools.
 * When activeModelsCount === 0, returns 'No Data' / neutral instead of fabricating 'Nominal' / 'All Healthy'.
 */
export function derivePoolsStatusSummary(
  activeModelsCount: number,
  exhaustedCount: number,
  unknownModelsCount: number
): PoolsStatusSummary {
  if (activeModelsCount === 0) {
    return {
      badgeText: 'No Data',
      badgeClass: 'badge-neutral',
      metricText: 'No Data',
      metricClass: 'text-muted'
    };
  }

  if (exhaustedCount > 0) {
    return {
      badgeText: `${exhaustedCount} Near Limit`,
      badgeClass: 'badge-danger',
      metricText: `${exhaustedCount} Exhausted`,
      metricClass: 'text-danger'
    };
  }

  if (unknownModelsCount > 0) {
    return {
      badgeText: `${unknownModelsCount} Unknown`,
      badgeClass: 'badge-neutral',
      metricText: 'Quota Unknown',
      metricClass: 'text-muted'
    };
  }

  return {
    badgeText: 'Nominal',
    badgeClass: 'badge-healthy',
    metricText: 'All Healthy',
    metricClass: 'text-success'
  };
}

export interface FormattedCreditPool {
  hasData: boolean;
  availableText: string;
  totalText: string | null;
  ratioPercent: number | null;
  isIndeterminate: boolean;
}

export type RefreshResource = 'status' | 'accounts' | 'config';
export interface RefreshTicket { resource: RefreshResource; sequence: number; mutationVersion: number }

/** Rejects out-of-order reads and every read crossing a mutating request. */
export class DashboardRefreshGate {
  private readonly sequences: Record<RefreshResource, number> = { status: 0, accounts: 0, config: 0 };
  private mutationVersion = 0;
  private mutationsInFlight = 0;

  beginRead(resource: RefreshResource): RefreshTicket {
    return { resource, sequence: ++this.sequences[resource], mutationVersion: this.mutationVersion };
  }

  canPublish(ticket: RefreshTicket): boolean {
    return this.mutationsInFlight === 0 && ticket.mutationVersion === this.mutationVersion &&
      ticket.sequence === this.sequences[ticket.resource];
  }

  beginMutation(): void {
    this.mutationVersion++;
    this.mutationsInFlight++;
  }

  endMutation(): void {
    if (this.mutationsInFlight === 0) throw new Error('No dashboard mutation is in flight');
    this.mutationsInFlight--;
    this.mutationVersion++;
  }
}

/**
 * Formats a credit pool truthfully, distinguishing unknown totals from zero and avoiding 100% fabrications.
 */
export function formatCreditPool(
  pool?: { availableCredits?: number | null; monthlyCredits?: number | null; usedCredits?: number | null } | null
): FormattedCreditPool {
  if (!pool || (pool.availableCredits == null && pool.monthlyCredits == null)) {
    return {
      hasData: false,
      availableText: '--',
      totalText: null,
      ratioPercent: null,
      isIndeterminate: true
    };
  }

  const avail = pool.availableCredits;
  const monthly = pool.monthlyCredits;

  const availableText = avail != null ? avail.toLocaleString() : '--';
  const totalText = monthly != null && monthly > 0 ? `/ ${monthly.toLocaleString()} total` : null;

  let ratioPercent: number | null = null;
  if (avail != null && monthly != null && monthly > 0) {
    ratioPercent = Math.max(0, Math.min(100, Math.round((avail / monthly) * 100)));
  }

  return {
    hasData: true,
    availableText,
    totalText,
    ratioPercent,
    isIndeterminate: ratioPercent === null
  };
}
