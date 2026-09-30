/**
 * AG2 Router - Routing Domain Types & State Models
 *
 * Defines contracts for candidate evaluation, deterministic ranking,
 * idle safety gating, and routing lifecycle state.
 */

import { AccountMetadata } from '../accounts/types.js';

export interface RouterConfig {
  /**
   * Whether automatic account switching is enabled.
   * Default: false (conservative default, user must explicitly enable)
   */
  readonly autoSwitchEnabled: boolean;

  /**
   * Threshold percentage below which the active account is considered low.
   * Default: 15 (%)
   */
  readonly lowQuotaThresholdPercent: number;

  /**
   * Minimum quota percentage required for a candidate account to be eligible.
   * Default: 30 (%)
   */
  readonly minimumCandidateQuotaPercent: number;

  /**
   * Telemetry polling interval in milliseconds.
   * Default: 10000 (10s)
   */
  readonly pollingIntervalMs: number;

  /**
   * Target model identifier for workload-directed routing.
   * When null or omitted, automatic model-specific routing remains fail-closed.
   */
  readonly workloadModelKey?: string | null;
}

/**
 * Default conservative router configuration.
 */
export const DEFAULT_ROUTER_CONFIG: RouterConfig = {
  autoSwitchEnabled: false,
  lowQuotaThresholdPercent: 15,
  minimumCandidateQuotaPercent: 30,
  pollingIntervalMs: 10000,
  workloadModelKey: null
};

/**
 * Candidate evaluation record for an account.
 */
export interface CandidateEvaluation {
  readonly account: AccountMetadata;
  readonly remainingFraction: number;
  readonly quotaPercent: number;
  readonly isEligible: boolean;
  readonly ineligibilityReason?: string;
  readonly score: number;
}

/**
 * Result of deterministic account candidate selection.
 */
export interface SelectionResult {
  readonly shouldSwitch: boolean;
  readonly reason: string;
  readonly currentAccountId: string | null;
  readonly currentQuotaFraction: number | null;
  readonly bestCandidate: CandidateEvaluation | null;
  readonly candidates: readonly CandidateEvaluation[];
}

/**
 * States of the safety gate machine enforcing:
 * LOW QUOTA -> SWITCH PENDING -> WAIT FOR IDLE -> SWITCH -> VERIFY
 */
export type SafetyGateState =
  | 'IDLE'
  | 'LOW_QUOTA_DETECTED'
  | 'SWITCH_PENDING'
  | 'WAITING_FOR_IDLE'
  | 'SWITCH_IN_PROGRESS'
  | 'VERIFYING'
  | 'SWITCH_COMPLETED'
  | 'SWITCH_FAILED';

/**
 * Assessment result from the safety gate.
 */
export interface SafetyGateAssessment {
  readonly canProceed: boolean;
  readonly currentState: SafetyGateState;
  readonly reason: string;
  readonly activeTrajectoriesCount: number;
}

/**
 * High-level router status returned to the server/dashboard.
 */
export interface RouterStatusSnapshot {
  readonly state: SafetyGateState;
  readonly autoSwitchEnabled: boolean;
  readonly activeAccountId: string | null;
  readonly activeAccountEmail: string | null;
  readonly pendingTargetAccountId: string | null;
  readonly lastEvaluatedAt: string | null;
  readonly lastDecisionReason: string;
  readonly config: RouterConfig;
}
