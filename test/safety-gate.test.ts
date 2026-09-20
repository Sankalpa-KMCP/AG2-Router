/**
 * Test: Idle Safety Gate
 */

import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import { ActivitySnapshot } from '../src/ag2/types.js';
import {
  InvalidStateTransitionError,
  SafetyGate
} from '../src/router/safety-gate.js';

describe('Idle Safety Gate', () => {
  it('should initialize in IDLE state', () => {
    const gate = new SafetyGate();
    assert.equal(gate.getState(), 'IDLE');
    assert.equal(gate.getTargetAccountId(), null);
  });

  it('should block when Antigravity 2 is BUSY', () => {
    const gate = new SafetyGate();
    const busyActivity: ActivitySnapshot = {
      state: 'BUSY',
      totalTrajectories: 2,
      runningTrajectories: 1,
      timestamp: new Date().toISOString()
    };

    const assessment = gate.assessActivity(busyActivity);
    assert.equal(assessment.canProceed, false);
    assert.equal(assessment.activeTrajectoriesCount, 1);
    assert.match(assessment.reason, /actively executing tasks/i);
  });

  it('should block when runningTrajectories > 0 even if state is not BUSY', () => {
    const gate = new SafetyGate();
    const busyActivity: ActivitySnapshot = {
      state: 'IDLE', // misreported, but runningTrajectories is > 0
      totalTrajectories: 1,
      runningTrajectories: 1,
      timestamp: new Date().toISOString()
    };

    const assessment = gate.assessActivity(busyActivity);
    assert.equal(assessment.canProceed, false);
    assert.equal(assessment.activeTrajectoriesCount, 1);
  });

  it('should allow transition when Antigravity 2 is truly IDLE (0 running trajectories)', () => {
    const gate = new SafetyGate();
    const idleActivity: ActivitySnapshot = {
      state: 'IDLE',
      totalTrajectories: 5,
      runningTrajectories: 0,
      timestamp: new Date().toISOString()
    };

    const assessment = gate.assessActivity(idleActivity);
    assert.equal(assessment.canProceed, true);
    assert.equal(assessment.activeTrajectoriesCount, 0);
    assert.match(assessment.reason, /safe for switching/i);
  });

  it('should block when activity state is UNKNOWN', () => {
    const gate = new SafetyGate();
    const unknownActivity: ActivitySnapshot = {
      state: 'UNKNOWN',
      totalTrajectories: 0,
      runningTrajectories: 0,
      timestamp: new Date().toISOString()
    };

    const assessment = gate.assessActivity(unknownActivity);
    assert.equal(assessment.canProceed, false);
    assert.match(assessment.reason, /Must be confirmed IDLE/i);
  });

  it('should follow valid transitions: IDLE -> LOW_QUOTA_DETECTED -> SWITCH_PENDING -> WAITING_FOR_IDLE', () => {
    const gate = new SafetyGate();

    gate.transition('LOW_QUOTA_DETECTED', 'Low quota on active account', 'acc_2');
    assert.equal(gate.getState(), 'LOW_QUOTA_DETECTED');
    assert.equal(gate.getTargetAccountId(), 'acc_2');

    gate.transition('SWITCH_PENDING', 'Auto-switch confirmed', 'acc_2');
    assert.equal(gate.getState(), 'SWITCH_PENDING');

    gate.transition('WAITING_FOR_IDLE', 'Target currently BUSY');
    assert.equal(gate.getState(), 'WAITING_FOR_IDLE');

    gate.reset('Cancelled');
    assert.equal(gate.getState(), 'IDLE');
    assert.equal(gate.getTargetAccountId(), null);
  });

  it('should reject illegal transitions', () => {
    const gate = new SafetyGate();
    assert.equal(gate.getState(), 'IDLE');

    // IDLE cannot jump directly to SWITCH_IN_PROGRESS or VERIFYING
    assert.throws(() => {
      gate.transition('SWITCH_IN_PROGRESS', 'Illegal bypass');
    }, InvalidStateTransitionError);
  });
});
