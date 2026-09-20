/**
 * AG2 Router - Antigravity 2 Telemetry Normalizer
 *
 * Pure transformation functions that map raw Connect-RPC response payloads
 * into normalized domain types without side effects or unverified aggregation.
 */

import {
  ActivitySnapshot,
  AG2AccountIdentity,
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
      typeof q?.remainingFraction === 'number'
        ? Math.max(0, Math.min(1, q.remainingFraction))
        : 1.0;
    const resetTime = q?.resetTime || undefined;
    const isExhausted = Boolean(q?.isExhausted || remainingFraction <= 0);

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

    const monthlyPrompt = typeof planInfo?.monthlyPromptCredits === 'number' ? planInfo.monthlyPromptCredits : 0;
    const availablePrompt = typeof u.planStatus.availablePromptCredits === 'number' ? u.planStatus.availablePromptCredits : 0;
    promptCredits = {
      availableCredits: availablePrompt,
      monthlyCredits: monthlyPrompt,
      usedCredits: Math.max(0, monthlyPrompt - availablePrompt)
    };

    const monthlyFlow = typeof planInfo?.monthlyFlowCredits === 'number' ? planInfo.monthlyFlowCredits : 0;
    const availableFlow = typeof u.planStatus.availableFlowCredits === 'number' ? u.planStatus.availableFlowCredits : 0;
    flowCredits = {
      availableCredits: availableFlow,
      monthlyCredits: monthlyFlow,
      usedCredits: Math.max(0, monthlyFlow - availableFlow)
    };
  }

  return {
    timestamp: new Date().toISOString(),
    models,
    promptCredits,
    flowCredits
  };
}

/**
 * Extract normalized activity snapshot from GetAllCascadeTrajectories payload.
 *
 * Invariant:
 * - runningTrajectories > 0 => BUSY
 * - valid response with runningTrajectories === 0 => IDLE
 */
export function normalizeActivitySnapshot(raw?: RawTrajectoriesResponse | null): ActivitySnapshot {
  if (!raw || !raw.trajectorySummaries || typeof raw.trajectorySummaries !== 'object') {
    return {
      state: 'IDLE',
      totalTrajectories: 0,
      runningTrajectories: 0,
      timestamp: new Date().toISOString()
    };
  }

  const entries = Object.values(raw.trajectorySummaries).filter((t) => Boolean(t));
  const totalTrajectories = entries.length;
  const runningTrajectories = entries.filter((t) => t.status === 'CASCADE_RUN_STATUS_RUNNING').length;

  const state: TrajectoryRunState = runningTrajectories > 0 ? 'BUSY' : 'IDLE';

  return {
    state,
    totalTrajectories,
    runningTrajectories,
    timestamp: new Date().toISOString()
  };
}
