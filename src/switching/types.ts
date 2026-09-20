/**
 * AG2 Router - Account Switching Domain Types
 *
 * Defines the complete type surface for transactional account switching,
 * preflight readiness planning, rollback snapshots, and status reporting.
 */

import { AG2ProcessLaunchSpec } from '../ag2/process-control.js';
import { WinCredEntry } from '../ag2/wincred.js';

export type SwitchTransactionState =
  | 'IDLE'
  | 'PLANNING'
  | 'WAITING_FOR_IDLE'
  | 'SNAPSHOTTING'
  | 'APPLYING_CREDENTIAL'
  | 'RESTARTING'
  | 'WAITING_FOR_AG2'
  | 'VERIFYING'
  | 'COMPLETE'
  | 'ROLLING_BACK'
  | 'ROLLED_BACK'
  | 'FAILED';

export type SwitchCheckCode =
  | 'TARGET_ACCOUNT_EXISTS'
  | 'TARGET_HAS_VAULTED_SESSION'
  | 'TARGET_NOT_ALREADY_ACTIVE'
  | 'AG2_ACTIVITY_IS_IDLE'
  | 'ROLLBACK_SNAPSHOT_READABLE'
  | 'LAUNCH_SPEC_CAPTURABLE';

export interface SwitchCheckResult {
  readonly code: SwitchCheckCode;
  readonly passed: boolean;
  readonly message: string;
  readonly details?: Record<string, unknown>;
}

export interface SwitchPlanResult {
  readonly ready: boolean;
  readonly targetAccountId: string;
  readonly targetEmail: string;
  readonly currentAccountId: string | null;
  readonly currentEmail: string | null;
  readonly checks: readonly SwitchCheckResult[];
  readonly blockers: readonly string[];
  readonly plannedAt: string;
}

export interface RollbackSnapshot {
  readonly originalAccountId: string | null;
  readonly originalEmail: string | null;
  readonly winCredEntry: WinCredEntry | null;
  readonly launchSpec: AG2ProcessLaunchSpec | null;
  readonly snapshotAt: string;
}

export interface SwitchTransactionResult {
  readonly transactionId: string;
  readonly success: boolean;
  readonly state: SwitchTransactionState;
  readonly targetAccountId: string;
  readonly targetEmail: string;
  readonly previousAccountId: string | null;
  readonly previousEmail: string | null;
  readonly error?: string;
  readonly stagesCompleted: readonly string[];
  readonly startedAt: string;
  readonly finishedAt: string;
}

export interface SwitchStatusResponse {
  readonly activeTransactionId: string | null;
  readonly currentState: SwitchTransactionState;
  readonly lastResult: SwitchTransactionResult | null;
}
