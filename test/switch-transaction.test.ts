import test from 'node:test';
import assert from 'node:assert/strict';
import {
  SwitchTransactionCoordinator,
  SwitchExecutionNotAuthorizedError,
  SwitchCoordinatorDependencies
} from '../src/switching/transaction.js';
import { IAccountStore, AccountMetadata } from '../src/accounts/types.js';
import { SessionVault } from '../src/vault/session-vault.js';
import { IWinCredReader, IWinCredWriter, WinCredEntry } from '../src/ag2/wincred.js';
import { IProcessController, AG2ProcessLaunchSpec } from '../src/ag2/process-control.js';
import { IAG2Adapter, AG2DiscoveryResult, AG2AccountIdentity, ActivitySnapshot, QuotaSnapshot, SwitchRequest, SwitchResult } from '../src/ag2/types.js';

class MockAccountStore implements IAccountStore {
  public accounts: Map<string, AccountMetadata> = new Map();
  public activeAccountId: string | null = null;

  public async listAccounts(): Promise<AccountMetadata[]> {
    return Array.from(this.accounts.values());
  }

  public async getAccount(id: string): Promise<AccountMetadata | null> {
    return this.accounts.get(id) || null;
  }

  public async getAccountByEmail(email: string): Promise<AccountMetadata | null> {
    for (const acc of this.accounts.values()) {
      if (acc.email.toLowerCase() === email.toLowerCase()) return acc;
    }
    return null;
  }

  public async addAccount(input: any): Promise<AccountMetadata> {
    const acc: AccountMetadata = {
      id: 'acc_' + Date.now(),
      email: input.email,
      name: input.name,
      priority: input.priority ?? 1,
      isReserve: input.isReserve ?? false,
      validationStatus: 'VALID',
      hasVaultedSession: input.hasVaultedSession ?? false,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      lastActiveAt: null
    };
    this.accounts.set(acc.id, acc);
    return acc;
  }

  public async updateAccount(id: string, updates: any): Promise<AccountMetadata | null> {
    const existing = this.accounts.get(id);
    if (!existing) return null;
    const updated = { ...existing, ...updates, updatedAt: new Date().toISOString() };
    this.accounts.set(id, updated);
    return updated;
  }

  public async removeAccount(id: string): Promise<boolean> {
    return this.accounts.delete(id);
  }

  public async removeAccountIfUnchanged(expected: AccountMetadata): Promise<boolean> {
    if (this.accounts.get(expected.id) !== expected) return false;
    return this.accounts.delete(expected.id);
  }

  public async getActiveAccountId(): Promise<string | null> {
    return this.activeAccountId;
  }

  public async setActiveAccountId(id: string | null): Promise<void> {
    this.activeAccountId = id;
  }

  public async compareExchangeActiveAccountId(expectedId: string | null, newId: string | null): Promise<boolean> {
    if (this.activeAccountId !== expectedId) return false;
    this.activeAccountId = newId;
    return true;
  }
}

class MockWinCredReader implements IWinCredReader {
  public cred: WinCredEntry | null = {
    target: 'gemini:antigravity',
    type: 1,
    userName: 'user1@example.com',
    persistence: 2,
    blob: Buffer.from('original-token-blob')
  };

  public async readCredential(target = 'gemini:antigravity'): Promise<WinCredEntry | null> {
    if (target) return this.cred;
    return null;
  }
}

class MockWinCredWriter implements IWinCredWriter {
  public writtenEntries: WinCredEntry[] = [];
  public shouldFail = false;

  public async writeCredential(entry: WinCredEntry): Promise<boolean> {
    if (this.shouldFail) return false;
    // Store clone of entry before zeroing
    this.writtenEntries.push({
      ...entry,
      blob: Buffer.from(entry.blob)
    });
    return true;
  }
}

class MockProcessController implements IProcessController {
  public alivePids = new Set<number>([22440]);
  public terminatedPids: number[] = [];
  public launchedSpecs: AG2ProcessLaunchSpec[] = [];
  public nextPid = 30000;

  public async captureLaunchSpec(pid: number): Promise<AG2ProcessLaunchSpec> {
    return {
      pid,
      executablePath: 'C:\\Users\\user\\AppData\\Local\\Programs\\Antigravity\\language_server.exe',
      rawArgs: ['--port', '51768'],
      sanitizedArgs: ['--port', '51768'],
      capturedAt: new Date().toISOString()
    };
  }

  public async terminateProcess(pid: number): Promise<boolean> {
    this.alivePids.delete(pid);
    this.terminatedPids.push(pid);
    return true;
  }

  public async launchProcess(spec: AG2ProcessLaunchSpec): Promise<number> {
    const pid = this.nextPid++;
    this.alivePids.add(pid);
    this.launchedSpecs.push(spec);
    return pid;
  }

  public async waitForExit(pid: number): Promise<boolean> {
    return !this.alivePids.has(pid);
  }

  public isPidAlive(pid: number): boolean {
    return this.alivePids.has(pid);
  }
}

class MockAG2Adapter implements IAG2Adapter {
  public isRunning = true;
  public status: 'HEALTHY' | 'OFFLINE' | 'DEGRADED' = 'HEALTHY';
  public currentAccount: AG2AccountIdentity | null = {
    email: 'user1@example.com',
    name: 'User One'
  };
  public activity: ActivitySnapshot = {
    state: 'IDLE',
    totalTrajectories: 0,
    runningTrajectories: 0,
    timestamp: new Date().toISOString()
  };

  public async discover(): Promise<AG2DiscoveryResult> {
    return {
      isRunning: this.isRunning,
      status: this.status,
      processInfo: this.isRunning ? {
        pid: 22440,
        port: 51768,
        protocol: 'https',
        csrfToken: 'mock-csrf',
        binaryPath: 'C:\\Users\\user\\AppData\\Local\\Programs\\Antigravity\\language_server.exe',
        discoveredAt: new Date().toISOString()
      } : null,
      message: 'Mock discovery'
    };
  }

  public async getCurrentAccount(): Promise<AG2AccountIdentity | null> {
    return this.currentAccount;
  }

  public async getQuota(): Promise<QuotaSnapshot | null> {
    return null;
  }

  public async getActivityState(): Promise<ActivitySnapshot> {
    return this.activity;
  }

  public async switchAccount(_request: SwitchRequest): Promise<SwitchResult> {
    throw new Error('Not implemented');
  }

  public async verifyAccount(expectedEmail: string): Promise<boolean> {
    return this.currentAccount?.email === expectedEmail;
  }
}

class MockSessionVault {
  public sessions = new Map<string, Buffer>();

  public async hasSession(id: string): Promise<boolean> {
    return this.sessions.has(id);
  }

  public async getSession(id: string): Promise<Buffer | null> {
    return this.sessions.get(id) || null;
  }
}

function createTestHarness() {
  const accountStore = new MockAccountStore();
  const sessionVault = new MockSessionVault();
  const winCredReader = new MockWinCredReader();
  const winCredWriter = new MockWinCredWriter();
  const processController = new MockProcessController();
  const ag2Adapter = new MockAG2Adapter();

  const deps: SwitchCoordinatorDependencies = {
    accountStore,
    sessionVault: sessionVault as unknown as SessionVault,
    winCredReader,
    winCredWriter,
    processController,
    ag2Adapter
  };

  return { deps, accountStore, sessionVault, winCredReader, winCredWriter, processController, ag2Adapter };
}

test('SwitchTransactionCoordinator - Hard execution gate prevents unauthorized execution', async () => {
  const { deps } = createTestHarness();
  // executionAuthorized omitted -> defaults to false
  const coordinator = new SwitchTransactionCoordinator(deps);

  assert.strictEqual(coordinator.isExecutionAuthorized(), false);

  await assert.rejects(
    () => coordinator.executeSwitch('acc_123'),
    (err: SwitchExecutionNotAuthorizedError) => {
      assert.strictEqual(err.name, 'SwitchExecutionNotAuthorizedError');
      assert.match(err.message, /Account switch execution is disabled in this runtime mode/);
      return true;
    }
  );

  const status = coordinator.getStatus();
  assert.strictEqual(status.currentState, 'IDLE');
  assert.strictEqual(status.activeTransactionId, null);
});

test('SwitchTransactionCoordinator - Preflight checks fail before any mutation', async () => {
  const { deps, accountStore, sessionVault, winCredWriter, ag2Adapter } = createTestHarness();
  const coordinator = new SwitchTransactionCoordinator(deps, { executionAuthorized: true });

  // 1. Target account does not exist
  const res1 = await coordinator.executeSwitch('nonexistent');
  assert.strictEqual(res1.success, false);
  assert.strictEqual(res1.state, 'FAILED');
  assert.match(res1.error || '', /does not exist/);
  assert.strictEqual(winCredWriter.writtenEntries.length, 0);

  // Add target account
  const acc1: AccountMetadata = {
    id: 'acc_target',
    email: 'target@example.com',
    priority: 1,
    isReserve: false,
    validationStatus: 'VALID',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };
  accountStore.accounts.set(acc1.id, acc1);

  // 2. Target has no session in vault
  const res2 = await coordinator.executeSwitch('acc_target');
  assert.strictEqual(res2.success, false);
  assert.strictEqual(res2.state, 'FAILED');
  assert.match(res2.error || '', /does not have an encrypted session in the vault/);
  assert.strictEqual(winCredWriter.writtenEntries.length, 0);

  // Add session to vault
  sessionVault.sessions.set('acc_target', Buffer.from('target-token-blob'));

  // 3. Target is already active
  accountStore.activeAccountId = 'acc_target';
  const res3 = await coordinator.executeSwitch('acc_target');
  assert.strictEqual(res3.success, false);
  assert.strictEqual(res3.state, 'FAILED');
  assert.match(res3.error || '', /already the active account/);
  assert.strictEqual(winCredWriter.writtenEntries.length, 0);

  // 4. AG2 is busy
  accountStore.activeAccountId = 'acc_source';
  ag2Adapter.activity = {
    state: 'BUSY',
    totalTrajectories: 1,
    runningTrajectories: 1,
    timestamp: new Date().toISOString()
  };
  const res4 = await coordinator.executeSwitch('acc_target');
  assert.strictEqual(res4.success, false);
  assert.strictEqual(res4.state, 'FAILED');
  assert.match(res4.error || '', /Antigravity 2 is busy/);
  assert.strictEqual(winCredWriter.writtenEntries.length, 0);
});

test('SwitchTransactionCoordinator - Happy path executes all stages and completes successfully', async () => {
  const { deps, accountStore, sessionVault, winCredWriter, processController, ag2Adapter } = createTestHarness();
  const coordinator = new SwitchTransactionCoordinator(deps, {
    executionAuthorized: true,
    pollIntervalMs: 10,
    restartWaitTimeoutMs: 500
  });

  const sourceAcc: AccountMetadata = {
    id: 'acc_source',
    email: 'user1@example.com',
    priority: 1,
    isReserve: false,
    validationStatus: 'VALID',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };
  const targetAcc: AccountMetadata = {
    id: 'acc_target',
    email: 'target@example.com',
    priority: 1,
    isReserve: false,
    validationStatus: 'VALID',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };

  accountStore.accounts.set(sourceAcc.id, sourceAcc);
  accountStore.accounts.set(targetAcc.id, targetAcc);
  accountStore.activeAccountId = sourceAcc.id;

  sessionVault.sessions.set(targetAcc.id, Buffer.from('target-token-blob'));

  // During restart, simulate account update in AG2 adapter
  const originalLaunchProcess = processController.launchProcess.bind(processController);
  processController.launchProcess = async (spec) => {
    const pid = await originalLaunchProcess(spec);
    // Switch adapter identity to target
    ag2Adapter.currentAccount = {
      email: 'target@example.com',
      name: 'Target User'
    };
    return pid;
  };

  const result = await coordinator.executeSwitch(targetAcc.id);
  assert.strictEqual(result.success, true);
  assert.strictEqual(result.state, 'COMPLETE');
  assert.strictEqual(result.targetAccountId, targetAcc.id);
  assert.strictEqual(result.previousAccountId, sourceAcc.id);

  // Verify store was updated
  assert.strictEqual(accountStore.activeAccountId, targetAcc.id);

  // Verify stages
  assert.ok(result.stagesCompleted.includes('VALIDATE_TARGET'));
  assert.ok(result.stagesCompleted.includes('VERIFY_IDLE'));
  assert.ok(result.stagesCompleted.includes('SNAPSHOT_TAKEN'));
  assert.ok(result.stagesCompleted.includes('CREDENTIAL_APPLIED'));
  assert.ok(result.stagesCompleted.includes('PROCESS_TERMINATED'));
  assert.ok(result.stagesCompleted.includes('IDENTITY_VERIFIED'));

  // Verify WinCred writer received target payload
  assert.strictEqual(winCredWriter.writtenEntries.length, 1);
  assert.strictEqual(winCredWriter.writtenEntries[0].userName, 'target@example.com');
});

test('SwitchTransactionCoordinator - Post-write verification failure triggers verified rollback', async () => {
  const { deps, accountStore, sessionVault, winCredWriter, processController, ag2Adapter } = createTestHarness();
  const coordinator = new SwitchTransactionCoordinator(deps, {
    executionAuthorized: true,
    pollIntervalMs: 10,
    restartWaitTimeoutMs: 200
  });

  const sourceAcc: AccountMetadata = {
    id: 'acc_source',
    email: 'user1@example.com',
    priority: 1,
    isReserve: false,
    validationStatus: 'VALID',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };
  const targetAcc: AccountMetadata = {
    id: 'acc_target',
    email: 'target@example.com',
    priority: 1,
    isReserve: false,
    validationStatus: 'VALID',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };

  accountStore.accounts.set(sourceAcc.id, sourceAcc);
  accountStore.accounts.set(targetAcc.id, targetAcc);
  accountStore.activeAccountId = sourceAcc.id;

  sessionVault.sessions.set(targetAcc.id, Buffer.from('target-token-blob'));

  // In this test, target relaunch fails to verify (remains user1 or mismatch)
  // When rollback relaunches, adapter recovers as original user1
  let launchCount = 0;
  processController.launchProcess = async (_spec) => {
    launchCount++;
    if (launchCount === 1) {
      // First launch (switch attempt): returns unexpected account or stays wrong
      ag2Adapter.currentAccount = {
        email: 'unexpected@example.com',
        name: 'Unexpected'
      };
    } else {
      // Second launch (rollback attempt): restores user1
      ag2Adapter.currentAccount = {
        email: 'user1@example.com',
        name: 'User One'
      };
    }
    return 31000 + launchCount;
  };

  const result = await coordinator.executeSwitch(targetAcc.id);
  assert.strictEqual(result.success, false);
  assert.strictEqual(result.state, 'ROLLED_BACK');
  assert.match(result.error || '', /Original account session successfully rolled back and verified/);

  // Active account remains sourceAcc
  assert.strictEqual(accountStore.activeAccountId, sourceAcc.id);

  // WinCred writer had 2 writes: first target, then rollback restoring original!
  assert.strictEqual(winCredWriter.writtenEntries.length, 2);
  assert.strictEqual(winCredWriter.writtenEntries[0].userName, 'target@example.com');
  assert.strictEqual(winCredWriter.writtenEntries[1].userName, 'user1@example.com');

  assert.ok(result.stagesCompleted.includes('ROLLBACK_INITIATED'));
  assert.ok(result.stagesCompleted.includes('ROLLBACK_CREDENTIAL_RESTORED'));
  assert.ok(result.stagesCompleted.includes('ROLLBACK_COMPLETED_SUCCESSFULLY'));
});

test('SwitchTransactionCoordinator - Rollback failure transitions to FAILED for operator visibility', async () => {
  const { deps, accountStore, sessionVault, winCredWriter, ag2Adapter } = createTestHarness();
  const coordinator = new SwitchTransactionCoordinator(deps, {
    executionAuthorized: true,
    pollIntervalMs: 10,
    restartWaitTimeoutMs: 200
  });

  const sourceAcc: AccountMetadata = {
    id: 'acc_source',
    email: 'user1@example.com',
    priority: 1,
    isReserve: false,
    validationStatus: 'VALID',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };
  const targetAcc: AccountMetadata = {
    id: 'acc_target',
    email: 'target@example.com',
    priority: 1,
    isReserve: false,
    validationStatus: 'VALID',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };

  accountStore.accounts.set(sourceAcc.id, sourceAcc);
  accountStore.accounts.set(targetAcc.id, targetAcc);
  accountStore.activeAccountId = sourceAcc.id;

  sessionVault.sessions.set(targetAcc.id, Buffer.from('target-token-blob'));

  // First write succeeds. Verification fails. Then rollback write fails!
  let writes = 0;
  const originalWrite = winCredWriter.writeCredential.bind(winCredWriter);
  winCredWriter.writeCredential = async (entry) => {
    writes++;
    if (writes === 1) {
      return originalWrite(entry);
    }
    // Fail during rollback!
    return false;
  };

  // Switch attempt: identity mismatch
  ag2Adapter.currentAccount = {
    email: 'mismatch@example.com',
    name: 'Mismatch'
  };

  const result = await coordinator.executeSwitch(targetAcc.id);
  assert.strictEqual(result.success, false);
  assert.strictEqual(result.state, 'FAILED');
  assert.match(result.error || '', /CRITICAL: Switch failed .* and automatic rollback failed/);
});
