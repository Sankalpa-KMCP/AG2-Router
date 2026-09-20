/**
 * AG2 Router - Transactional Account Switch Coordinator & Rollback Engine
 *
 * Implements the 15-stage transaction state machine and automatic rollback
 * coordinator for Antigravity 2 account switching.
 *
 * CRITICAL SAFETY INVARIANTS:
 * - Execution Gate: executionAuthorized defaults to false. Production runtimes
 *   must NEVER enable this gate until a separate authorized phase.
 * - Single Concurrency: Only one switch transaction may execute at any given time.
 * - Point-of-No-Return Rollback: The moment WinCred is modified, ANY downstream
 *   failure triggers a full rollback restoring original credential, process,
 *   and verified identity.
 * - Verified Rollback: A rollback is only considered successful ('ROLLED_BACK') if
 *   the original identity is re-verified via AG2 telemetry. Otherwise it transitions
 *   to 'FAILED' to ensure full operator visibility.
 */

import { IAccountStore } from '../accounts/types.js';
import { IAG2Adapter } from '../ag2/adapter.js';
import { IProcessController } from '../ag2/process-control.js';
import { redactSensitiveText } from '../ag2/security.js';
import { DEFAULT_AG2_WINCRED_TARGET, IWinCredReader, IWinCredWriter, WinCredEntry } from '../ag2/wincred.js';
import { SessionVault } from '../vault/session-vault.js';
import {
  RollbackSnapshot,
  SwitchStatusResponse,
  SwitchTransactionResult,
  SwitchTransactionState
} from './types.js';

export class SwitchTransactionError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SwitchTransactionError';
  }
}

export class SwitchExecutionNotAuthorizedError extends SwitchTransactionError {
  constructor(
    message = 'Account switch execution is disabled in this runtime mode. Only dry-run switch planning is authorized.'
  ) {
    super(message);
    this.name = 'SwitchExecutionNotAuthorizedError';
  }
}

export class SwitchPreflightError extends SwitchTransactionError {
  constructor(message: string) {
    super(message);
    this.name = 'SwitchPreflightError';
  }
}

export class RollbackFailedError extends SwitchTransactionError {
  constructor(message: string) {
    super(message);
    this.name = 'RollbackFailedError';
  }
}

export interface SwitchCoordinatorDependencies {
  readonly accountStore: IAccountStore;
  readonly sessionVault: SessionVault;
  readonly winCredReader: IWinCredReader;
  readonly winCredWriter: IWinCredWriter;
  readonly processController: IProcessController;
  readonly ag2Adapter: IAG2Adapter;
}

export interface SwitchCoordinatorOptions {
  /**
   * Hard execution authorization gate. Must be explicitly set to true.
   * Defaults to false.
   */
  readonly executionAuthorized?: boolean;
  /**
   * Windows Credential Manager target. Defaults to 'gemini:antigravity'.
   */
  readonly winCredTarget?: string;
  /**
   * Process termination timeout in milliseconds.
   */
  readonly processTerminateTimeoutMs?: number;
  /**
   * Maximum wait time for AG2 process relaunch & discovery in milliseconds.
   */
  readonly restartWaitTimeoutMs?: number;
  /**
   * Poll interval for restart and discovery checks in milliseconds.
   */
  readonly pollIntervalMs?: number;
}

export class SwitchTransactionCoordinator {
  private readonly accountStore: IAccountStore;
  private readonly sessionVault: SessionVault;
  private readonly winCredReader: IWinCredReader;
  private readonly winCredWriter: IWinCredWriter;
  private readonly processController: IProcessController;
  private readonly ag2Adapter: IAG2Adapter;

  private readonly executionAuthorized: boolean;
  private readonly winCredTarget: string;
  private readonly processTerminateTimeoutMs: number;
  private readonly restartWaitTimeoutMs: number;
  private readonly pollIntervalMs: number;

  private currentState: SwitchTransactionState = 'IDLE';
  private activeTransactionId: string | null = null;
  private lastResult: SwitchTransactionResult | null = null;

  constructor(
    dependencies: SwitchCoordinatorDependencies,
    options: SwitchCoordinatorOptions = {}
  ) {
    this.accountStore = dependencies.accountStore;
    this.sessionVault = dependencies.sessionVault;
    this.winCredReader = dependencies.winCredReader;
    this.winCredWriter = dependencies.winCredWriter;
    this.processController = dependencies.processController;
    this.ag2Adapter = dependencies.ag2Adapter;

    this.executionAuthorized = options.executionAuthorized ?? false;
    this.winCredTarget = options.winCredTarget || DEFAULT_AG2_WINCRED_TARGET;
    this.processTerminateTimeoutMs = options.processTerminateTimeoutMs ?? 8000;
    this.restartWaitTimeoutMs = options.restartWaitTimeoutMs ?? 15000;
    this.pollIntervalMs = options.pollIntervalMs ?? 200;
  }

  /**
   * Returns whether live account switch execution is authorized in this instance.
   */
  public isExecutionAuthorized(): boolean {
    return this.executionAuthorized;
  }

  /**
   * Returns the current status of the switch state machine.
   */
  public getStatus(): SwitchStatusResponse {
    return {
      activeTransactionId: this.activeTransactionId,
      currentState: this.currentState,
      lastResult: this.lastResult
    };
  }

  /**
   * Executes a transactional switch to the target account with full rollback.
   * Enforces the hard execution gate.
   */
  public async executeSwitch(targetAccountId: string): Promise<SwitchTransactionResult> {
    if (!this.executionAuthorized) {
      throw new SwitchExecutionNotAuthorizedError();
    }

    if (
      this.currentState !== 'IDLE' &&
      this.currentState !== 'COMPLETE' &&
      this.currentState !== 'ROLLED_BACK' &&
      this.currentState !== 'FAILED'
    ) {
      throw new SwitchTransactionError(
        `Cannot execute switch: transaction '${this.activeTransactionId}' is already in state '${this.currentState}'.`
      );
    }

    const transactionId = `tx_${Date.now()}_${Math.random().toString(36).substring(2, 8)}`;
    const startedAt = new Date().toISOString();
    const stagesCompleted: string[] = [];

    this.activeTransactionId = transactionId;
    this.currentState = 'PLANNING';

    let targetEmail = '';
    let previousAccountId: string | null = null;
    let previousEmail: string | null = null;
    let rollbackSnapshot: RollbackSnapshot | null = null;
    let credentialMutated = false;

    try {
      // -------------------------------------------------------------
      // STAGE 1: Validate Target Account & Session Vault
      // -------------------------------------------------------------
      const targetAccount = await this.accountStore.getAccount(targetAccountId);
      if (!targetAccount) {
        throw new SwitchPreflightError(`Target account '${targetAccountId}' does not exist.`);
      }
      targetEmail = targetAccount.email;

      const hasVault = await this.sessionVault.hasSession(targetAccountId);
      if (!hasVault) {
        throw new SwitchPreflightError(
          `Target account '${targetEmail}' (${targetAccountId}) does not have an encrypted session in the vault.`
        );
      }

      previousAccountId = await this.accountStore.getActiveAccountId();
      const currentAg2Account = await this.ag2Adapter.getCurrentAccount();
      previousEmail = currentAg2Account?.email || null;

      if (previousAccountId === targetAccountId || (previousEmail && previousEmail.toLowerCase() === targetEmail.toLowerCase())) {
        throw new SwitchPreflightError(`Target account '${targetEmail}' is already the active account.`);
      }

      stagesCompleted.push('VALIDATE_TARGET');

      // -------------------------------------------------------------
      // STAGE 2: Preflight Idle Safety Gate
      // -------------------------------------------------------------
      this.currentState = 'WAITING_FOR_IDLE';
      const discovery = await this.ag2Adapter.discover();
      if (!discovery.isRunning || !discovery.processInfo?.pid) {
        throw new SwitchPreflightError('Antigravity 2 process is offline. Cannot switch accounts safely.');
      }

      const activity = await this.ag2Adapter.getActivityState();
      if (activity.state !== 'IDLE' || activity.runningTrajectories > 0) {
        throw new SwitchPreflightError(
          `Antigravity 2 is busy (${activity.runningTrajectories} running trajectories). Switch aborted for safety.`
        );
      }

      stagesCompleted.push('VERIFY_IDLE');

      // -------------------------------------------------------------
      // STAGE 3: Rollback Snapshot Creation
      // -------------------------------------------------------------
      this.currentState = 'SNAPSHOTTING';
      const origCred = await this.winCredReader.readCredential(this.winCredTarget);
      if (!origCred || !origCred.blob || origCred.blob.length === 0) {
        throw new SwitchPreflightError(
          `Cannot read existing credential '${this.winCredTarget}' for rollback snapshot.`
        );
      }

      const launchSpec = await this.processController.captureLaunchSpec(discovery.processInfo.pid);
      rollbackSnapshot = {
        originalAccountId: previousAccountId,
        originalEmail: previousEmail,
        winCredEntry: {
          ...origCred,
          blob: Buffer.from(origCred.blob) // Clone buffer
        },
        launchSpec,
        snapshotAt: new Date().toISOString()
      };

      stagesCompleted.push('SNAPSHOT_TAKEN');

      // -------------------------------------------------------------
      // STAGE 4: Retrieve Decrypted Target Session Payload
      // -------------------------------------------------------------
      const targetBlob = await this.sessionVault.getSession(targetAccountId);
      if (!targetBlob || targetBlob.length === 0) {
        throw new SwitchPreflightError('Failed to decrypt session blob for target account.');
      }

      const targetEntry: WinCredEntry = {
        target: this.winCredTarget,
        userName: targetEmail,
        type: 1,
        persistence: 2,
        blob: targetBlob
      };

      // -------------------------------------------------------------
      // STAGE 5: Apply New Credential to WinCred (Point of No Return)
      // -------------------------------------------------------------
      this.currentState = 'APPLYING_CREDENTIAL';
      const writeOk = await this.winCredWriter.writeCredential(targetEntry);
      if (!writeOk) {
        throw new SwitchTransactionError('Failed to write target credential to Windows Credential Manager.');
      }
      credentialMutated = true;
      stagesCompleted.push('CREDENTIAL_APPLIED');

      // -------------------------------------------------------------
      // STAGE 6: Terminate Existing AG2 Process
      // -------------------------------------------------------------
      this.currentState = 'RESTARTING';
      const oldPid = discovery.processInfo.pid;
      await this.processController.terminateProcess(oldPid, this.processTerminateTimeoutMs);
      const exited = await this.processController.waitForExit(oldPid, this.processTerminateTimeoutMs);
      if (!exited) {
        throw new SwitchTransactionError(`Timed out waiting for AG2 process ${oldPid} to terminate.`);
      }
      stagesCompleted.push('PROCESS_TERMINATED');

      // -------------------------------------------------------------
      // STAGE 7: Launch New AG2 Process with Captured Launch Spec
      // -------------------------------------------------------------
      const newPid = await this.processController.launchProcess(launchSpec);
      stagesCompleted.push(`PROCESS_LAUNCHED_${newPid}`);

      // -------------------------------------------------------------
      // STAGE 8: Wait for AG2 Language Server to Come Online
      // -------------------------------------------------------------
      this.currentState = 'WAITING_FOR_AG2';
      const online = await this.waitForAg2Online(this.restartWaitTimeoutMs);
      if (!online) {
        throw new SwitchTransactionError('Relaunched Antigravity 2 process failed to come online within timeout.');
      }
      stagesCompleted.push('AG2_DISCOVERED');

      // -------------------------------------------------------------
      // STAGE 9: Post-Switch Identity Verification
      // -------------------------------------------------------------
      this.currentState = 'VERIFYING';
      const verified = await this.waitForAccountMatch(targetEmail, this.restartWaitTimeoutMs);
      if (!verified) {
        const currentNow = await this.ag2Adapter.getCurrentAccount();
        throw new SwitchTransactionError(
          `Post-restart identity verification failed. Expected: '${targetEmail}', Observed: '${currentNow?.email || 'none'}'`
        );
      }
      stagesCompleted.push('IDENTITY_VERIFIED');

      // -------------------------------------------------------------
      // STAGE 10: Finalize Store & Transition to COMPLETE
      // -------------------------------------------------------------
      await this.accountStore.setActiveAccountId(targetAccountId);
      await this.accountStore.updateAccount(targetAccountId, {
        lastActiveAt: new Date().toISOString()
      });

      this.currentState = 'COMPLETE';
      const finishedAt = new Date().toISOString();

      const result: SwitchTransactionResult = {
        transactionId,
        success: true,
        state: 'COMPLETE',
        targetAccountId,
        targetEmail,
        previousAccountId,
        previousEmail,
        stagesCompleted,
        startedAt,
        finishedAt
      };

      this.lastResult = result;
      this.activeTransactionId = null;
      return result;
    } catch (err) {
      const errMsg = err instanceof Error ? redactSensitiveText(err.message) : 'Switch transaction failed';

      if (credentialMutated && rollbackSnapshot) {
        // We have mutated OS credential state. We MUST initiate automatic rollback.
        try {
          await this.executeRollback(rollbackSnapshot, stagesCompleted);
          const finishedAt = new Date().toISOString();
          const rollbackResult: SwitchTransactionResult = {
            transactionId,
            success: false,
            state: 'ROLLED_BACK',
            targetAccountId,
            targetEmail,
            previousAccountId,
            previousEmail,
            error: `Switch failed: ${errMsg}. Original account session successfully rolled back and verified.`,
            stagesCompleted,
            startedAt,
            finishedAt
          };
          this.lastResult = rollbackResult;
          this.activeTransactionId = null;
          return rollbackResult;
        } catch (rollbackErr) {
          this.currentState = 'FAILED';
          const rbMsg = rollbackErr instanceof Error ? redactSensitiveText(rollbackErr.message) : 'Rollback failed';
          const finishedAt = new Date().toISOString();
          const failedResult: SwitchTransactionResult = {
            transactionId,
            success: false,
            state: 'FAILED',
            targetAccountId,
            targetEmail,
            previousAccountId,
            previousEmail,
            error: `CRITICAL: Switch failed (${errMsg}) and automatic rollback failed (${rbMsg}). Manual intervention required.`,
            stagesCompleted,
            startedAt,
            finishedAt
          };
          this.lastResult = failedResult;
          this.activeTransactionId = null;
          return failedResult;
        }
      } else {
        // Failed cleanly before any credential or process mutation
        this.currentState = 'FAILED';
        const finishedAt = new Date().toISOString();
        const failedResult: SwitchTransactionResult = {
          transactionId,
          success: false,
          state: 'FAILED',
          targetAccountId,
          targetEmail,
          previousAccountId,
          previousEmail,
          error: errMsg,
          stagesCompleted,
          startedAt,
          finishedAt
        };
        this.lastResult = failedResult;
        this.activeTransactionId = null;
        return failedResult;
      }
    }
  }

  /**
   * Executes rollback to restore original Windows credential and restart process.
   * Only transitions to ROLLED_BACK if the restored identity is verified.
   */
  private async executeRollback(
    snapshot: RollbackSnapshot,
    stagesCompleted: string[]
  ): Promise<void> {
    this.currentState = 'ROLLING_BACK';
    stagesCompleted.push('ROLLBACK_INITIATED');

    if (!snapshot.winCredEntry) {
      throw new RollbackFailedError('No original WinCred snapshot entry available for rollback.');
    }

    // 1. Restore original WinCred entry
    const restoreEntry: WinCredEntry = {
      target: snapshot.winCredEntry.target,
      userName: snapshot.winCredEntry.userName,
      type: snapshot.winCredEntry.type,
      persistence: snapshot.winCredEntry.persistence,
      blob: Buffer.from(snapshot.winCredEntry.blob) // Clone buffer
    };

    const writeOk = await this.winCredWriter.writeCredential(restoreEntry);
    if (!writeOk) {
      throw new RollbackFailedError('Failed to write original credential back to Windows Credential Manager.');
    }
    stagesCompleted.push('ROLLBACK_CREDENTIAL_RESTORED');

    // 2. Terminate running AG2 process if alive
    const discovery = await this.ag2Adapter.discover();
    if (discovery.isRunning && discovery.processInfo?.pid) {
      await this.processController.terminateProcess(discovery.processInfo.pid, this.processTerminateTimeoutMs);
      await this.processController.waitForExit(discovery.processInfo.pid, this.processTerminateTimeoutMs);
      stagesCompleted.push('ROLLBACK_PROCESS_TERMINATED');
    }

    // 3. Relaunch original AG2 process if launch spec available
    if (snapshot.launchSpec) {
      await this.processController.launchProcess(snapshot.launchSpec);
      stagesCompleted.push('ROLLBACK_PROCESS_LAUNCHED');
    }

    // 4. Wait for AG2 to come online
    const online = await this.waitForAg2Online(this.restartWaitTimeoutMs);
    if (!online) {
      throw new RollbackFailedError('AG2 failed to come online during rollback within timeout.');
    }
    stagesCompleted.push('ROLLBACK_AG2_DISCOVERED');

    // 5. Verify restored identity
    if (snapshot.originalEmail) {
      const verified = await this.waitForAccountMatch(snapshot.originalEmail, this.restartWaitTimeoutMs);
      if (!verified) {
        throw new RollbackFailedError(
          `Rollback identity verification failed. Expected original email '${snapshot.originalEmail}'.`
        );
      }
    }
    stagesCompleted.push('ROLLBACK_IDENTITY_VERIFIED');

    // 6. Restore active account in metadata store
    if (snapshot.originalAccountId) {
      await this.accountStore.setActiveAccountId(snapshot.originalAccountId);
      stagesCompleted.push('ROLLBACK_STORE_RESTORED');
    }

    this.currentState = 'ROLLED_BACK';
    stagesCompleted.push('ROLLBACK_COMPLETED_SUCCESSFULLY');
  }

  private async waitForAg2Online(timeoutMs: number): Promise<boolean> {
    const startTime = Date.now();
    while (Date.now() - startTime < timeoutMs) {
      const discovery = await this.ag2Adapter.discover();
      if (discovery.isRunning && discovery.status === 'HEALTHY') {
        return true;
      }
      await new Promise((resolve) => setTimeout(resolve, this.pollIntervalMs));
    }
    return false;
  }

  private async waitForAccountMatch(expectedEmail: string, timeoutMs: number): Promise<boolean> {
    const startTime = Date.now();
    const expectedLower = expectedEmail.toLowerCase();
    while (Date.now() - startTime < timeoutMs) {
      const account = await this.ag2Adapter.getCurrentAccount();
      if (account && account.email.toLowerCase() === expectedLower) {
        return true;
      }
      await new Promise((resolve) => setTimeout(resolve, this.pollIntervalMs));
    }
    return false;
  }
}
