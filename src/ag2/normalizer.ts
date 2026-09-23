/**
 * AG2 Router - Antigravity 2 Telemetry Normalizer
 *
 * Pure transformation functions that map raw Connect-RPC response payloads
 * into normalized domain types without side effects or unverified aggregation.
 */

import {
  ActivitySnapshot,
  AG2AccountIdentity,
  CanonicalModelQuotaInfo,
  CreditPoolInfo,
  ModelQuotaInfo,
  QuotaSnapshot,
  TrajectoryRunState
} from './types.js';

export interface RawUserStatusResponse {
  userStatus?: {
    email?: string;
    name?: string;
    userTier?: {
      id?: string;
      name?: string;
      description?: string;
      availableCredits?: Array<{ creditType?: string; minimumCreditAmountForUsage?: string }>;
    };
    planStatus?: {
      availablePromptCredits?: number;
      availableFlowCredits?: number;
      planInfo?: {
        planName?: string;
        teamsTier?: string;
        monthlyPromptCredits?: number;
        monthlyFlowCredits?: number;
      };
    };
    cascadeModelConfigData?: {
      clientModelConfigs?: Array<{
        label?: string;
        modelOrTier?: string;
        quotaInfo?: {
          remainingFraction?: number;
          resetTime?: string;
          isExhausted?: boolean;
        };
      }>;
    };
  };
}

export interface RawTrajectoriesResponse {
  trajectorySummaries?: Record<string, {
    cascadeId?: string;
    trajectoryId?: string;
    summary?: string;
    stepCount?: number;
    status?: string;
    lastModifiedTime?: string;
    createdTime?: string;
  }>;
}

/**
 * Extract normalized account identity from GetUserStatus payload.
 */
export function normalizeAccountIdentity(raw?: RawUserStatusResponse | null): AG2AccountIdentity | null {
  const u = raw?.userStatus;
  if (!u || !u.email || typeof u.email !== 'string') {
    return null;
  }

  const email = u.email.trim();
  if (!email) {
    return null;
  }

  const name = u.name?.trim() || undefined;
  const tierId = u.userTier?.id || undefined;
  const tierName = u.userTier?.name || u.planStatus?.planInfo?.planName || undefined;

  return {
    email,
    name,
    tierId,
    tierName,
    rawStatusTimestamp: new Date().toISOString()
  };
}

/**
 * Extract normalized quota snapshot from GetUserStatus payload.
 *
 * Invariant: Model quota pools are preserved individually.
 * Prompt credits and Flow credits are strictly segregated and NEVER combined.
 */
export function normalizeQuotaSnapshot(raw?: RawUserStatusResponse | null): QuotaSnapshot | null {
  const u = raw?.userStatus;
  if (!u) {
    return null;
  }

  // Model-specific quota pools
  const rawConfigs = u.cascadeModelConfigData?.clientModelConfigs || [];
  const models: ModelQuotaInfo[] = rawConfigs.map((cfg) => {
    const label = cfg.label?.trim() || cfg.modelOrTier?.trim() || 'Unknown Model';
    const modelOrTier = cfg.modelOrTier?.trim() || undefined;
    const q = cfg.quotaInfo;

    const remainingFraction =
      typeof q?.remainingFraction === 'number' && Number.isFinite(q.remainingFraction)
        ? Math.max(0, Math.min(1, q.remainingFraction))
        : null;
    const resetTime = q?.resetTime || undefined;
    const isExhausted = Boolean(q?.isExhausted || remainingFraction === 0);

    return {
      label,
      modelOrTier,
      remainingFraction,
      resetTime,
      isExhausted
    };
  });

  // Credit pools (Segregated Prompt & Flow)
  let promptCredits: CreditPoolInfo | undefined = undefined;
  let flowCredits: CreditPoolInfo | undefined = undefined;

  if (u.planStatus) {
    const planInfo = u.planStatus.planInfo;

    const monthlyPrompt = Number.isFinite(planInfo?.monthlyPromptCredits) ? planInfo!.monthlyPromptCredits! : null;
    const availablePrompt = Number.isFinite(u.planStatus.availablePromptCredits) ? u.planStatus.availablePromptCredits! : null;
    promptCredits = {
      availableCredits: availablePrompt,
      monthlyCredits: monthlyPrompt,
      usedCredits: monthlyPrompt === null || availablePrompt === null ? null : Math.max(0, monthlyPrompt - availablePrompt)
    };

    const monthlyFlow = Number.isFinite(planInfo?.monthlyFlowCredits) ? planInfo!.monthlyFlowCredits! : null;
    const availableFlow = Number.isFinite(u.planStatus.availableFlowCredits) ? u.planStatus.availableFlowCredits! : null;
    flowCredits = {
      availableCredits: availableFlow,
      monthlyCredits: monthlyFlow,
      usedCredits: monthlyFlow === null || availableFlow === null ? null : Math.max(0, monthlyFlow - availableFlow)
    };
  }

  const canonicalModels = canonicalizeModelQuotas(models);

  return {
    timestamp: new Date().toISOString(),
    models,
    promptCredits,
    flowCredits,
    canonicalModels
  };
}

/**
 * Extract normalized activity snapshot from GetAllCascadeTrajectories payload.
 *
 * Invariant:
 * - runningTrajectories > 0 => BUSY
 * - only an explicit empty collection or known inactive statuses => IDLE
 */
export function normalizeActivitySnapshot(raw?: RawTrajectoriesResponse | null): ActivitySnapshot {
  if (!raw || !raw.trajectorySummaries || typeof raw.trajectorySummaries !== 'object' ||
      Array.isArray(raw.trajectorySummaries)) {
    return {
      state: 'UNKNOWN',
      totalTrajectories: 0,
      runningTrajectories: 0,
      timestamp: new Date().toISOString()
    };
  }

  const entries = Object.values(raw.trajectorySummaries);
  const totalTrajectories = entries.length;
  const runningTrajectories = entries.filter((t) => t?.status === 'CASCADE_RUN_STATUS_RUNNING').length;

  const allKnownInactive = entries.every((t) =>
    t?.status === 'CASCADE_RUN_STATUS_IDLE' || t?.status === 'CASCADE_RUN_STATUS_DONE');
  const state: TrajectoryRunState = runningTrajectories > 0 ? 'BUSY' : allKnownInactive ? 'IDLE' : 'UNKNOWN';

  return {
    state,
    totalTrajectories,
    runningTrajectories,
    timestamp: new Date().toISOString()
  };
}

const VARIANT_ANNOTATION_REGEX = /\s*\((?:thinking|reasoning)\)\s*$/i;

export function cleanModelLabel(label?: string): string {
  if (!label || !label.trim()) return 'Unknown Model';
  const cleaned = label.replace(VARIANT_ANNOTATION_REGEX, '').trim();
  return cleaned || 'Unknown Model';
}

export function extractMode(label?: string): string {
  if (!label) return 'Standard';
  if (/\(thinking\)/i.test(label)) return 'Thinking';
  if (/\(reasoning\)/i.test(label)) return 'Reasoning';
  return 'Standard';
}

export function selectLatestResetTime(resetTimes: Array<string | undefined>): string | undefined {
  const valid = resetTimes
    .filter((t): t is string => Boolean(t && t.trim()))
    .map((t) => t.trim());
  if (valid.length === 0) return undefined;
  if (valid.length === 1) return valid[0];

  return valid.reduce((latest, current) => {
    const latestTime = Date.parse(latest);
    const currentTime = Date.parse(current);
    if (!Number.isNaN(latestTime) && !Number.isNaN(currentTime)) {
      return currentTime > latestTime ? current : latest;
    }
    return current.localeCompare(latest) > 0 ? current : latest;
  });
}

export function canonicalizeModelQuotas(models?: readonly ModelQuotaInfo[]): readonly CanonicalModelQuotaInfo[] {
  if (!models || models.length === 0) {
    return [];
  }

  const groups: Array<{ key: string; items: ModelQuotaInfo[] }> = [];

  for (const [row, model] of models.entries()) {
    const tier = model.modelOrTier?.trim().toLowerCase() || null;
    // Model/tier and reset equality cannot establish shared pool ownership.
    const baseKey = tier ? `tier:${tier}` : `row:${row}`;
    const key = groups.some(group => group.key === baseKey) ? `${baseKey}:row:${row}` : baseKey;

    groups.push({ key, items: [model] });
  }

  return groups.map(({ key, items }) => {
    let displayLabel = cleanModelLabel(items[0].label);
    if (!displayLabel || displayLabel === 'Unknown Model') {
      displayLabel = items[0].modelOrTier?.trim() || 'Unknown Model';
    }

    const modes: string[] = [];
    for (const item of items) {
      const mode = extractMode(item.label);
      if (!modes.some((m) => m.toLowerCase() === mode.toLowerCase())) {
        modes.push(mode);
      }
    }

    const knownFractions = items.map(m => m.remainingFraction).filter((n): n is number => n !== null && Number.isFinite(n));
    const remainingFraction = knownFractions.length > 0 ? Math.max(0, Math.min(1, Math.min(...knownFractions))) : null;
    const isExhausted = items.some((m) => m.isExhausted) || remainingFraction === 0;
    const resetTime = selectLatestResetTime(items.map((m) => m.resetTime));
    const modelOrTier = items.find((m) => m.modelOrTier && m.modelOrTier.trim())?.modelOrTier?.trim();

    return {
      key,
      label: displayLabel,
      modelOrTier,
      remainingFraction,
      resetTime,
      isExhausted,
      modes
    };
  });
}
