/**
 * AG2 Router - Read-Only Switch Readiness Planner
 *
 * Evaluates whether Antigravity 2 is ready to safely switch to a target account.
 * Performs strictly read-only checks:
 * 1. Target account exists in metadata store.
 * 2. Target account has an encrypted session in the vault.
 * 3. Target account is not already the active account.
 * 4. Antigravity 2 process is online and activity is IDLE (0 running trajectories).
 * 5. Current Windows Credential is readable for rollback snapshotting.
 * 6. Running AG2 process launch specification is capturable and verified.
 *
 * GUARANTEE: This class performs ZERO credential writes and ZERO process terminations.
 */

import { IAccountStore } from '../accounts/types.js';
import { IAG2Adapter } from '../ag2/adapter.js';
import { IProcessController } from '../ag2/process-control.js';
import { redactSensitiveText } from '../ag2/security.js';
import { DEFAULT_AG2_WINCRED_TARGET, IWinCredReader } from '../ag2/wincred.js';
import { SessionVault } from '../vault/session-vault.js';
import { SwitchCheckResult, SwitchPlanResult } from './types.js';

export interface SwitchPlannerDependencies {
  readonly accountStore: IAccountStore;
  readonly sessionVault: SessionVault;
  readonly winCredReader: IWinCredReader;
  readonly processController: IProcessController;
  readonly ag2Adapter: IAG2Adapter;
}

export interface SwitchPlannerOptions {
  readonly winCredTarget?: string;
}

export class SwitchPlanner {
  private readonly accountStore: IAccountStore;
  private readonly sessionVault: SessionVault;
  private readonly winCredReader: IWinCredReader;
  private readonly processController: IProcessController;
  private readonly ag2Adapter: IAG2Adapter;
  private readonly winCredTarget: string;

  constructor(
    dependencies: SwitchPlannerDependencies,
    options: SwitchPlannerOptions = {}
  ) {
    this.accountStore = dependencies.accountStore;
    this.sessionVault = dependencies.sessionVault;
    this.winCredReader = dependencies.winCredReader;
    this.processController = dependencies.processController;
    this.ag2Adapter = dependencies.ag2Adapter;
    this.winCredTarget = options.winCredTarget || DEFAULT_AG2_WINCRED_TARGET;
  }

  /**
   * Plans and evaluates switch readiness for a target account.
   * Completely non-mutating and read-only.
   */
  public async planSwitch(targetAccountId: string): Promise<SwitchPlanResult> {
    const plannedAt = new Date().toISOString();
    const checks: SwitchCheckResult[] = [];
    const blockers: string[] = [];

    let targetEmail = '';
    let currentAccountId: string | null = null;
    let currentEmail: string | null = null;

    try {
      currentAccountId = await this.accountStore.getActiveAccountId();
    } catch {
      // ignore
    }

    try {
      const activeIdentity = await this.ag2Adapter.getCurrentAccount();
      currentEmail = activeIdentity?.email || null;
    } catch {
      // ignore
    }

    // -------------------------------------------------------------
    // CHECK 1: Target Account Exists
    // -------------------------------------------------------------
    const targetAccount = await this.accountStore.getAccount(targetAccountId);
    if (!targetAccount) {
      const msg = `Target account '${targetAccountId}' was not found in storage.`;
      checks.push({
        code: 'TARGET_ACCOUNT_EXISTS',
        passed: false,
        message: msg
      });
      blockers.push(msg);

      return {
        ready: false,
        targetAccountId,
        targetEmail: '',
        currentAccountId,
        currentEmail,
        checks,
        blockers,
        plannedAt
      };
    }

    targetEmail = targetAccount.email;
    checks.push({
      code: 'TARGET_ACCOUNT_EXISTS',
      passed: true,
      message: `Target account exists in store (${targetEmail}).`
    });

    // -------------------------------------------------------------
    // CHECK 2: Target Has Vaulted Session
    // -------------------------------------------------------------
    const hasVault = await this.sessionVault.hasSession(targetAccountId);
    if (hasVault) {
      checks.push({
        code: 'TARGET_HAS_VAULTED_SESSION',
        passed: true,
        message: 'Target account has an encrypted session vault record.'
      });
    } else {
      const msg = `Target account '${targetEmail}' does not have an encrypted session in the vault.`;
      checks.push({
        code: 'TARGET_HAS_VAULTED_SESSION',
        passed: false,
        message: msg
      });
      blockers.push(msg);
    }

    // -------------------------------------------------------------
    // CHECK 3: Target Not Already Active
    // -------------------------------------------------------------
    const isStoreActive = currentAccountId === targetAccountId;
    const isLiveActive = Boolean(
      currentEmail && currentEmail.toLowerCase() === targetEmail.toLowerCase()
    );

    if (isStoreActive || isLiveActive) {
      const msg = `Target account '${targetEmail}' is already active in Antigravity 2.`;
      checks.push({
        code: 'TARGET_NOT_ALREADY_ACTIVE',
        passed: false,
        message: msg
      });
      blockers.push(msg);
    } else {
      checks.push({
        code: 'TARGET_NOT_ALREADY_ACTIVE',
        passed: true,
        message: 'Target account is not currently active.'
      });
    }

    // -------------------------------------------------------------
    // CHECK 4: AG2 Activity is IDLE
    // -------------------------------------------------------------
    let discoveryRunning = false;
    let ag2Pid: number | null = null;
    try {
      const discovery = await this.ag2Adapter.discover();
      discoveryRunning = discovery.isRunning;
      ag2Pid = discovery.processInfo?.pid || null;

      if (!discoveryRunning || !ag2Pid) {
        const msg = 'Antigravity 2 is offline. Process must be running and healthy to plan switch.';
        checks.push({
          code: 'AG2_ACTIVITY_IS_IDLE',
          passed: false,
          message: msg
        });
        blockers.push(msg);
      } else {
        const activity = await this.ag2Adapter.getActivityState();
        if (activity.state === 'IDLE' && activity.runningTrajectories === 0) {
          checks.push({
            code: 'AG2_ACTIVITY_IS_IDLE',
            passed: true,
            message: 'Antigravity 2 is online and idle (0 running tasks).'
          });
        } else if (activity.runningTrajectories > 0) {
          const msg = `Antigravity 2 is currently active (${activity.runningTrajectories} running tasks).`;
          checks.push({
            code: 'AG2_ACTIVITY_IS_IDLE',
            passed: false,
            message: msg
          });
          blockers.push(msg);
        } else {
          const msg = `Antigravity 2 activity state is '${activity.state}'. Must be IDLE.`;
          checks.push({
            code: 'AG2_ACTIVITY_IS_IDLE',
            passed: false,
            message: msg
          });
          blockers.push(msg);
        }
      }
    } catch (err) {
      const safeMsg = err instanceof Error ? redactSensitiveText(err.message) : 'Discovery error';
      const msg = `Failed to query Antigravity 2 activity state: ${safeMsg}`;
      checks.push({
        code: 'AG2_ACTIVITY_IS_IDLE',
        passed: false,
        message: msg
      });
      blockers.push(msg);
    }

    // -------------------------------------------------------------
    // CHECK 5: Rollback Snapshot Readable
    // -------------------------------------------------------------
    try {
      const origCred = await this.winCredReader.readCredential(this.winCredTarget);
      if (origCred && origCred.blob && origCred.blob.length > 0) {
        checks.push({
          code: 'ROLLBACK_SNAPSHOT_READABLE',
          passed: true,
          message: `Active Windows Credential ('${this.winCredTarget}') is readable for rollback snapshot.`
        });
      } else {
        const msg = `Active Windows Credential ('${this.winCredTarget}') was not found or is empty.`;
        checks.push({
          code: 'ROLLBACK_SNAPSHOT_READABLE',
          passed: false,
          message: msg
        });
        blockers.push(msg);
      }
    } catch (err) {
      const safeMsg = err instanceof Error ? redactSensitiveText(err.message) : 'Read error';
      const msg = `Cannot read active Windows Credential: ${safeMsg}`;
      checks.push({
        code: 'ROLLBACK_SNAPSHOT_READABLE',
        passed: false,
        message: msg
      });
      blockers.push(msg);
    }

    // -------------------------------------------------------------
    // CHECK 6: Launch Specification Capturable
    // -------------------------------------------------------------
    if (ag2Pid) {
      try {
        const spec = await this.processController.captureLaunchSpec(ag2Pid);
        checks.push({
          code: 'LAUNCH_SPEC_CAPTURABLE',
          passed: true,
          message: `AG2 launch specification (PID ${ag2Pid}) was successfully captured and verified.`,
          details: {
            pid: spec.pid,
            executablePath: spec.executablePath,
            argsCount: spec.rawArgs.length
          }
        });
      } catch (err) {
        const safeMsg = err instanceof Error ? redactSensitiveText(err.message) : 'Inspection error';
        const msg = `Failed to capture AG2 launch specification: ${safeMsg}`;
        checks.push({
          code: 'LAUNCH_SPEC_CAPTURABLE',
          passed: false,
          message: msg
        });
        blockers.push(msg);
      }
    } else {
      const msg = 'AG2 launch specification cannot be captured because process is not running.';
      checks.push({
        code: 'LAUNCH_SPEC_CAPTURABLE',
        passed: false,
        message: msg
      });
      blockers.push(msg);
    }

    const ready = blockers.length === 0;

    return {
      ready,
      targetAccountId,
      targetEmail,
      currentAccountId,
      currentEmail,
      checks,
      blockers,
      plannedAt
    };
  }
}
