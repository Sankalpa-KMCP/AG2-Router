import test from 'node:test';
import assert from 'node:assert/strict';
import { SwitchPlanner } from '../src/switching/planner.js';
import { IAccountStore, AccountMetadata } from '../src/accounts/types.js';
import { SessionVault } from '../vault/session-vault.js';
import { IWinCredReader, WinCredEntry } from '../ag2/wincred.js';
import { IProcessController, AG2ProcessLaunchSpec } from '../ag2/process-control.js';
import { IAG2Adapter, AG2DiscoveryResult, AG2AccountIdentity, ActivitySnapshot, QuotaSnapshot, SwitchRequest, SwitchResult } from '../ag2/types.js';

class MockAccountStore implements IAccountStore {
  public accounts = new Map<string, AccountMetadata>();
  public activeId: string | null = null;

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
  public async addAccount(): Promise<any> { throw new Error(); }
  public async updateAccount(): Promise<any> { throw new Error(); }
  public async removeAccount(): Promise<any> { throw new Error(); }
  public async getActiveAccountId(): Promise<string | null> { return this.activeId; }
  public async setActiveAccountId(id: string | null): Promise<void> { this.activeId = id; }
}

class MockWinCredReader implements IWinCredReader {
  public cred: WinCredEntry | null = {
    target: 'gemini:antigravity',
    type: 1,
    userName: 'active@example.com',
    persistence: 2,
    blob: Buffer.from('active-cred')
  };

  public async readCredential(target = 'gemini:antigravity'): Promise<WinCredEntry | null> {
    return this.cred;
  }
}

class MockProcessController implements IProcessController {
  public failCapture = false;

  public async captureLaunchSpec(pid: number): Promise<AG2ProcessLaunchSpec> {
    if (this.failCapture) throw new Error('Failed to capture');
    return {
      pid,
      executablePath: 'C:\\Users\\user\\language_server.exe',
      rawArgs: ['--port', '51768'],
      sanitizedArgs: ['--port', '51768'],
      capturedAt: new Date().toISOString()
    };
  }
  public async terminateProcess(): Promise<boolean> { return true; }
  public async launchProcess(): Promise<number> { return 1; }
  public async waitForExit(): Promise<boolean> { return true; }
  public isPidAlive(): boolean { return true; }
}

class MockAG2Adapter implements IAG2Adapter {
  public isRunning = true;
  public status: 'HEALTHY' | 'OFFLINE' | 'DEGRADED' = 'HEALTHY';
  public currentAccount: AG2AccountIdentity | null = {
    email: 'active@example.com',
    name: 'Active User',
    status: 'ACTIVE'
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
        csrfToken: 'mock-csrf',
        executablePath: 'C:\\Users\\user\\language_server.exe'
      } : null,
      message: 'Mock discovery'
    };
  }
  public async getCurrentAccount(): Promise<AG2AccountIdentity | null> {
    return this.currentAccount;
  }
  public async getQuota(): Promise<QuotaSnapshot | null> { return null; }
  public async getActivityState(): Promise<ActivitySnapshot> { return this.activity; }
  public async switchAccount(_request: SwitchRequest): Promise<SwitchResult> { throw new Error(); }
  public async verifyAccount(_expected: AG2AccountIdentity): Promise<any> { throw new Error(); }
}

function createPlannerHarness() {
  const accountStore = new MockAccountStore();
  const sessionVault = {
    sessions: new Set<string>(),
    async hasSession(id: string): Promise<boolean> {
      return this.sessions.has(id);
    }
  } as unknown as SessionVault;

  const winCredReader = new MockWinCredReader();
  const processController = new MockProcessController();
  const ag2Adapter = new MockAG2Adapter();

  const planner = new SwitchPlanner({
    accountStore,
    sessionVault,
    winCredReader,
    processController,
    ag2Adapter
  });

  return { planner, accountStore, sessionVault, winCredReader, processController, ag2Adapter };
}

test('SwitchPlanner - Returns ready: true when all 6 checks pass', async () => {
  const { planner, accountStore, sessionVault } = createPlannerHarness();

  const targetAcc: AccountMetadata = {
    id: 'acc_candidate',
    email: 'candidate@example.com',
    priority: 1,
    isReserve: false,
    validationStatus: 'VALID',
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };
  accountStore.accounts.set(targetAcc.id, targetAcc);
  (sessionVault as any).sessions.add(targetAcc.id);

  const plan = await planner.planSwitch('acc_candidate');
  assert.strictEqual(plan.ready, true);
  assert.strictEqual(plan.blockers.length, 0);
  assert.strictEqual(plan.checks.length, 6);
  assert.strictEqual(plan.checks.every((c) => c.passed), true);
  assert.strictEqual(plan.targetAccountId, 'acc_candidate');
  assert.strictEqual(plan.targetEmail, 'candidate@example.com');
  assert.strictEqual(plan.currentEmail, 'active@example.com');
});

test('SwitchPlanner - Identifies blockers when preflight conditions are not met', async () => {
  const { planner, accountStore, sessionVault, ag2Adapter, winCredReader } = createPlannerHarness();

  // 1. Target account does not exist
  const plan1 = await planner.planSwitch('missing_acc');
  assert.strictEqual(plan1.ready, false);
  assert.strictEqual(plan1.blockers.length, 1);
  assert.match(plan1.blockers[0], /was not found in storage/);

  // Set up target account
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
  accountStore.accounts.set(targetAcc.id, targetAcc);

  // 2. Target has no vaulted session
  const plan2 = await planner.planSwitch('acc_target');
  assert.strictEqual(plan2.ready, false);
  assert.ok(plan2.blockers.some((b) => b.includes('does not have an encrypted session in the vault')));

  (sessionVault as any).sessions.add('acc_target');

  // 3. Target is already active (by live identity)
  ag2Adapter.currentAccount = {
    email: 'target@example.com',
    name: 'Target User',
    status: 'ACTIVE'
  };
  const plan3 = await planner.planSwitch('acc_target');
  assert.strictEqual(plan3.ready, false);
  assert.ok(plan3.blockers.some((b) => b.includes('already active in Antigravity 2')));

  // Reset live account
  ag2Adapter.currentAccount = {
    email: 'other@example.com',
    name: 'Other',
    status: 'ACTIVE'
  };

  // 4. AG2 is busy
  ag2Adapter.activity = {
    state: 'BUSY',
    totalTrajectories: 2,
    runningTrajectories: 2,
    timestamp: new Date().toISOString()
  };
  const plan4 = await planner.planSwitch('acc_target');
  assert.strictEqual(plan4.ready, false);
  assert.ok(plan4.blockers.some((b) => b.includes('Antigravity 2 is currently active')));

  // Reset activity to IDLE
  ag2Adapter.activity = {
    state: 'IDLE',
    totalTrajectories: 0,
    runningTrajectories: 0,
    timestamp: new Date().toISOString()
  };

  // 5. WinCred credential missing
  winCredReader.cred = null;
  const plan5 = await planner.planSwitch('acc_target');
  assert.strictEqual(plan5.ready, false);
  assert.ok(plan5.blockers.some((b) => b.includes('Active Windows Credential')));
});
