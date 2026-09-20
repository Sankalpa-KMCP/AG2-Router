/**
 * Test: AG2 Live Adapter Contract & Safety Boundary
 */

import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import { AG2ProcessDetector, IProcessInspector } from '../src/ag2/discovery.js';
import { AG2LiveAdapter } from '../src/ag2/live-adapter.js';
import { AG2RpcClient } from '../src/ag2/rpc-client.js';
import { NotImplementedError } from '../src/ag2/types.js';

class MockInspector implements IProcessInspector {
  public running = false;
  public pid = 12345;

  public async findProcesses() {
    if (!this.running) return [];
    return [
      {
        pid: this.pid,
        name: 'language_server.exe',
        commandLine: 'language_server.exe --standalone --csrf_token mock-csrf-token-12345'
      }
    ];
  }

  public async getListeningPorts(_pid: number) {
    return this.running ? [49152] : [];
  }

  public isPidAlive(pid: number) {
    return this.running && pid === this.pid;
  }
}

class MockRpc extends AG2RpcClient {
  public shouldFail = false;
  public runningTasksCount = 0;

  public override async probePort() {
    return true;
  }

  public override async getUserStatus() {
    if (this.shouldFail) throw new Error('Simulated RPC failure');
    return {
      userStatus: {
        email: 'developer@example.com',
        name: 'Developer Jane',
        userTier: { name: 'Google AI Pro' },
        cascadeModelConfigData: {
          clientModelConfigs: [
            {
              label: 'Gemini 3.8 Flash (High)',
              quotaInfo: { remainingFraction: 0.75, resetTime: '2026-09-20T18:00:00Z' }
            }
          ]
        }
      }
    };
  }

  public override async getAllCascadeTrajectories() {
    if (this.shouldFail) throw new Error('Simulated RPC failure');
    const summaries: Record<string, { status: string; summary: string }> = {};
    if (this.runningTasksCount > 0) {
      summaries['task-1'] = { status: 'CASCADE_RUN_STATUS_RUNNING', summary: 'Active task' };
    } else {
      summaries['task-1'] = { status: 'CASCADE_RUN_STATUS_IDLE', summary: 'Idle task' };
    }
    return { trajectorySummaries: summaries };
  }
}

describe('AG2LiveAdapter', () => {
  it('should handle offline state truthfully without crashing', async () => {
    const inspector = new MockInspector();
    inspector.running = false;
    const rpcClient = new MockRpc();
    const detector = new AG2ProcessDetector({ inspector, rpcClient, offlineCooldownMs: 0 });
    const adapter = new AG2LiveAdapter({ detector, rpcClient });

    const discovery = await adapter.discover();
    assert.equal(discovery.isRunning, false);
    assert.equal(discovery.status, 'OFFLINE');

    const account = await adapter.getCurrentAccount();
    assert.equal(account, null);

    const quota = await adapter.getQuota();
    assert.equal(quota, null);

    const activity = await adapter.getActivityState();
    assert.equal(activity.state, 'OFFLINE');
    assert.equal(activity.runningTrajectories, 0);
  });

  it('should retrieve normalized telemetry and classify IDLE when zero tasks running', async () => {
    const inspector = new MockInspector();
    inspector.running = true;
    const rpcClient = new MockRpc();
    rpcClient.runningTasksCount = 0;
    const detector = new AG2ProcessDetector({ inspector, rpcClient, offlineCooldownMs: 0 });
    const adapter = new AG2LiveAdapter({ detector, rpcClient });

    const discovery = await adapter.discover();
    assert.equal(discovery.isRunning, true);

    const account = await adapter.getCurrentAccount();
    assert.ok(account);
    assert.equal(account.email, 'developer@example.com');
    assert.equal(account.tierName, 'Google AI Pro');

    const quota = await adapter.getQuota();
    assert.ok(quota);
    assert.equal(quota.models.length, 1);
    assert.equal(quota.models[0].label, 'Gemini 3.8 Flash (High)');
    assert.equal(quota.models[0].remainingFraction, 0.75);

    const activity = await adapter.getActivityState();
    assert.equal(activity.state, 'IDLE');
    assert.equal(activity.runningTrajectories, 0);
    assert.ok(adapter.getLastTelemetryTimestamp());
  });

  it('should classify BUSY when running trajectories exist', async () => {
    const inspector = new MockInspector();
    inspector.running = true;
    const rpcClient = new MockRpc();
    rpcClient.runningTasksCount = 1;
    const detector = new AG2ProcessDetector({ inspector, rpcClient, offlineCooldownMs: 0 });
    const adapter = new AG2LiveAdapter({ detector, rpcClient });

    const activity = await adapter.getActivityState();
    assert.equal(activity.state, 'BUSY');
    assert.equal(activity.runningTrajectories, 1);
  });

  it('should handle RPC failure gracefully returning safe error state', async () => {
    const inspector = new MockInspector();
    inspector.running = true;
    const rpcClient = new MockRpc();
    rpcClient.shouldFail = true;
    const detector = new AG2ProcessDetector({ inspector, rpcClient, offlineCooldownMs: 0 });
    const adapter = new AG2LiveAdapter({ detector, rpcClient });

    const account = await adapter.getCurrentAccount();
    assert.equal(account, null);

    const quota = await adapter.getQuota();
    assert.equal(quota, null);

    const activity = await adapter.getActivityState();
    assert.equal(activity.state, 'ERROR');
  });

  it('should strictly throw NotImplementedError for switchAccount and verifyAccount (Boundary Rule)', async () => {
    const adapter = new AG2LiveAdapter();

    await assert.rejects(
      async () => {
        await adapter.switchAccount({
          targetAccountId: 'acc-1',
          targetEmail: 'test@example.com'
        });
      },
      (err: unknown) => {
        assert.ok(err instanceof NotImplementedError);
        assert.equal(err.code, 'NOT_IMPLEMENTED');
        return true;
      }
    );

    await assert.rejects(
      async () => {
        await adapter.verifyAccount('test@example.com');
      },
      (err: unknown) => {
        assert.ok(err instanceof NotImplementedError);
        assert.equal(err.code, 'NOT_IMPLEMENTED');
        return true;
      }
    );
  });
});
