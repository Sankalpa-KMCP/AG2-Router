/**
 * AG2 Router - Deterministic Account Candidate Selector
 *
 * Implements pure domain logic for evaluating quota health and selecting
 * the optimal candidate account when quota drops below threshold.
 *
 * Requirements:
 * - Deterministic behavior with clear tie-breaking.
 * - Standard accounts preferred over reserve accounts.
 * - Minimum candidate quota threshold strictly enforced.
 */

import { AccountMetadata } from '../accounts/types.js';
import {
  CandidateEvaluation,
  RouterConfig,
  SelectionResult
} from './types.js';

export interface SelectionInput {
  readonly currentAccountId: string | null;
  readonly currentQuotaFraction: number | null;
  readonly accounts: readonly AccountMetadata[];
  readonly accountQuotas: ReadonlyMap<string, number>;
  readonly config: RouterConfig;
}

/**
 * Pure candidate evaluation and deterministic selection function.
 */
export function selectBestCandidate(input: SelectionInput): SelectionResult {
  const { currentAccountId, currentQuotaFraction, accounts, accountQuotas, config } = input;
  const lowThresholdFraction = config.lowQuotaThresholdPercent / 100;
  const minCandidateFraction = config.minimumCandidateQuotaPercent / 100;

  if (currentAccountId === null || currentQuotaFraction === null || !Number.isFinite(currentQuotaFraction)) {
    return {
      shouldSwitch: false,
      reason: 'Current account quota is unknown; automatic switching requires observed low quota.',
      currentAccountId,
      currentQuotaFraction: null,
      bestCandidate: null,
      candidates: []
    };
  }

  // 1. Check if current account quota is healthy
  if (currentAccountId !== null && currentQuotaFraction !== null) {
    if (currentQuotaFraction > lowThresholdFraction) {
      const pct = Math.round(currentQuotaFraction * 100);
      return {
        shouldSwitch: false,
        reason: `Current account quota is healthy (${pct}% > ${config.lowQuotaThresholdPercent}%).`,
        currentAccountId,
        currentQuotaFraction,
        bestCandidate: null,
        candidates: []
      };
    }
  }

  // 2. Evaluate each registered account
  const evaluations: CandidateEvaluation[] = [];

  for (const account of accounts) {
    // Current account is not a candidate to switch to
    if (account.id === currentAccountId) {
      continue;
    }

    const remainingFraction = accountQuotas.get(account.id) ?? 0;
    const quotaPercent = Math.round(remainingFraction * 100);

    let isEligible = true;
    let ineligibilityReason: string | undefined;

    if (account.validationStatus === 'EXPIRED' || account.validationStatus === 'FAILED') {
      isEligible = false;
      ineligibilityReason = `Account validation status is ${account.validationStatus}`;
    } else if (remainingFraction < minCandidateFraction) {
      isEligible = false;
      ineligibilityReason = `Quota (${quotaPercent}%) is below minimum candidate threshold (${config.minimumCandidateQuotaPercent}%)`;
    }

    // Score calculation:
    // Base: 0 to 100 based on remaining quota
    // Standard account bonus: +50 (so reserve accounts are only used if all normal accounts are exhausted)
    // Priority bonus: up to 10 points based on priority (lower priority integer = higher preference)
    let score = remainingFraction * 100;
    if (!account.isReserve) {
      score += 50;
    }
    score += Math.max(0, 10 - account.priority);

    evaluations.push({
      account,
      remainingFraction,
      quotaPercent,
      isEligible,
      ineligibilityReason,
      score: Math.round(score * 100) / 100
    });
  }

  // 3. Filter eligible candidates
  const eligible = evaluations.filter((e) => e.isEligible);

  if (eligible.length === 0) {
    return {
      shouldSwitch: false,
      reason: evaluations.length === 0
        ? 'No secondary accounts are registered in the store.'
        : `No candidate accounts meet the minimum quota threshold (${config.minimumCandidateQuotaPercent}%).`,
      currentAccountId,
      currentQuotaFraction,
      bestCandidate: null,
      candidates: evaluations
    };
  }

  // 4. Deterministic sorting:
  // Non-reserve first -> Higher usable quota -> Lower priority integer -> Alphabetical ID
  eligible.sort((a, b) => {
    // 1. Non-reserve vs Reserve
    if (a.account.isReserve !== b.account.isReserve) {
      return a.account.isReserve ? 1 : -1;
    }

    // 2. Usable Quota (highest fraction first)
    const quotaDiff = b.remainingFraction - a.remainingFraction;
    if (Math.abs(quotaDiff) > 0.001) {
      return quotaDiff;
    }

    // 3. Priority (1 is better than 2)
    const priorityDiff = a.account.priority - b.account.priority;
    if (priorityDiff !== 0) {
      return priorityDiff;
    }

    // 4. Deterministic ID tie-breaker
    return a.account.id.localeCompare(b.account.id);
  });

  const best = eligible[0];
  const currentPct = currentQuotaFraction !== null ? `${Math.round(currentQuotaFraction * 100)}%` : 'unknown';

  return {
    shouldSwitch: true,
    reason: `Low quota on current account (${currentPct} <= ${config.lowQuotaThresholdPercent}%). Optimal candidate selected: ${best.account.email} (${best.quotaPercent}%).`,
    currentAccountId,
    currentQuotaFraction,
    bestCandidate: best,
    candidates: evaluations
  };
}
