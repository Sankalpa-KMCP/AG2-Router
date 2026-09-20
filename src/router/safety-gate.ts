/**
 * AG2 Router - Idle Safety Gate
 *
 * Enforces the safety lifecycle contract:
 * LOW QUOTA → SWITCH PENDING → WAIT FOR IDLE → SWITCH → VERIFY
 *
 * Prevents account switching or process interruption while Antigravity 2
 * is actively executing cascade runs / agent trajectories.
 */

import { ActivitySnapshot } from '../ag2/types.js';
import { SafetyGateAssessment, SafetyGateState } from './types.js';

export class InvalidStateTransitionError extends Error {
  constructor(from: SafetyGateState, to: SafetyGateState) {
    super(`Illegal safety gate state transition from '${from}' to '${to}'.`);
    this.name = 'InvalidStateTransitionError';
  }
}

/**
 * Valid state transitions mapping for the safety gate machine.
 */
const VALID_TRANSITIONS: Record<SafetyGateState, readonly SafetyGateState[]> = {
  IDLE: ['LOW_QUOTA_DETECTED', 'SWITCH_PENDING'],
  LOW_QUOTA_DETECTED: ['IDLE', 'SWITCH_PENDING'],
  SWITCH_PENDING: ['IDLE', 'WAITING_FOR_IDLE', 'SWITCH_IN_PROGRESS'],
  WAITING_FOR_IDLE: ['IDLE', 'WAITING_FOR_IDLE', 'SWITCH_IN_PROGRESS'],
  SWITCH_IN_PROGRESS: ['VERIFYING', 'SWITCH_FAILED'],
  VERIFYING: ['SWITCH_COMPLETED', 'SWITCH_FAILED'],
  SWITCH_COMPLETED: ['IDLE'],
  SWITCH_FAILED: ['IDLE']
};

export class SafetyGate {
  private state: SafetyGateState = 'IDLE';
  private lastTransitionReason = 'Initialized';
  private targetAccountId: string | null = null;

  public getState(): SafetyGateState {
    return this.state;
  }

  public getTargetAccountId(): string | null {
    return this.targetAccountId;
  }

  public getLastTransitionReason(): string {
    return this.lastTransitionReason;
  }

  /**
   * Assess whether an account switch can safely proceed based on current AG2 activity.
   */
  public assessActivity(activity: ActivitySnapshot): SafetyGateAssessment {
    if (activity.state === 'BUSY' || activity.runningTrajectories > 0) {
      return {
        canProceed: false,
        currentState: this.state,
        reason: `Antigravity 2 is actively executing tasks (${activity.runningTrajectories} running trajectories). Switching is blocked to prevent data loss.`,
        activeTrajectoriesCount: activity.runningTrajectories
      };
    }

    if (activity.state === 'IDLE' && activity.runningTrajectories === 0) {
      return {
        canProceed: true,
        currentState: this.state,
        reason: 'Antigravity 2 is IDLE with 0 running trajectories. Safe for switching.',
        activeTrajectoriesCount: 0
      };
    }

    return {
      canProceed: false,
      currentState: this.state,
      reason: `Antigravity 2 activity state is '${activity.state}'. Must be confirmed IDLE before proceeding.`,
      activeTrajectoriesCount: activity.runningTrajectories
    };
  }

  /**
   * Transition the gate to a new state if valid.
   */
  public transition(to: SafetyGateState, reason: string, targetAccountId?: string): void {
    const allowed = VALID_TRANSITIONS[this.state];
    if (!allowed.includes(to)) {
      throw new InvalidStateTransitionError(this.state, to);
    }

    this.state = to;
    this.lastTransitionReason = reason;
    if (targetAccountId !== undefined) {
      this.targetAccountId = targetAccountId;
    }
    if (to === 'IDLE') {
      this.targetAccountId = null;
    }
  }

  /**
   * Force reset the gate back to IDLE state.
   */
  public reset(reason = 'Reset to IDLE'): void {
    this.state = 'IDLE';
    this.targetAccountId = null;
    this.lastTransitionReason = reason;
  }
}
