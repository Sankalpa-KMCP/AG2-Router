/**
 * Test: AG2 Process & Port Discovery (Synthetic Unit Tests)
 */

import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import {
  AG2ProcessDetector,
  DiscoveredProcessRaw,
  IProcessInspector
} from '../src/ag2/discovery.js';
import { AG2RpcClient } from '../src/ag2/rpc-client.js';

class MockProcessInspector implements IProcessInspector {
  public processes: DiscoveredProcessRaw[] = [];
  public listeningPorts: number[] = [];
  public alivePids = new Set<number>();

  public findProcessesCount = 0;
  public getListeningPortsCount = 0;

  public async findProcesses(): Promise<DiscoveredProcessRaw[]> {
    this.findProcessesCount++;
    return [...this.processes];
  }

  public async getListeningPorts(pid: number): Promise<number[]> {
    this.getListeningPortsCount++;
    if (this.alivePids.has(pid)) {
      return [...this.listeningPorts];
    }
    return [];
  }

  public isPidAlive(pid: number): boolean {
    return this.alivePids.has(pid);
  }
}

class MockRpcClient extends AG2RpcClient {
  public workingPort = 45000;
  public workingProtocol: 'http' | 'https' = 'https';

  public override async probePort(
    port: number,
    protocol: 'http' | 'https',
    _csrfToken: string
  ): Promise<boolean> {
    return port === this.workingPort && protocol === this.workingProtocol;
  }
}

describe('AG2ProcessDetector', () => {
  it('should discriminate and select Antigravity 2 from process list', () => {
    const detector = new AG2ProcessDetector();

    const processes: DiscoveredProcessRaw[] = [
      {
        pid: 1001,
        name: 'chrome.exe',
        commandLine: 'C:\\chrome\\chrome.exe --type=renderer'
      },
      {
        pid: 1002,
        name: 'language_server.exe',
        // IDE unified language server (should NOT be picked for AG2)
        commandLine: 'C:\\ide\\language_server.exe --app_data_dir antigravity-ide --csrf_token token-ide'
      },
      {
        pid: 1003,
        name: 'language_server.exe',
        // Antigravity 2 Standalone Daemon
        commandLine:
          'C:\\Users\\user\\resources\\bin\\language_server.exe --standalone --override_ide_name antigravity --subclient_type hub --csrf_token mock-secret-token-12345 --https_server_port 0'
      }
    ];

    const selected = detector.selectAntigravity2Process(processes);
    assert.ok(selected);
    assert.equal(selected.pid, 1003);
  });

  it('should extract CSRF token dynamically from command line', () => {
    const detector = new AG2ProcessDetector();
    const token = detector.extractCsrfToken(
      'language_server.exe --standalone --csrf_token 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d --port 0'
    );
    assert.equal(token, '1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d');

    const tokenEqual = detector.extractCsrfToken(
      'language_server.exe --standalone --csrf_token=alpha_beta_123'
    );
    assert.equal(tokenEqual, 'alpha_beta_123');

    assert.equal(detector.extractCsrfToken('no-token-here'), null);
  });

  it('should perform cold discovery and cache session for zero-subprocess steady-state', async () => {
    const inspector = new MockProcessInspector();
    const rpcClient = new MockRpcClient();

    inspector.processes = [
      {
        pid: 22000,
        name: 'language_server.exe',
        commandLine:
          'C:\\ag2\\resources\\bin\\language_server.exe --standalone --override_ide_name antigravity --csrf_token mock-ag2-token-99999'
      }
    ];
    inspector.listeningPorts = [45000, 45001];
    inspector.alivePids.add(22000);

    const detector = new AG2ProcessDetector({
      inspector,
      rpcClient,
      offlineCooldownMs: 5000
    });

    // 1. Initial Cold Discovery
    const discovery1 = await detector.discover();
    assert.equal(discovery1.isRunning, true);
    assert.equal(discovery1.status, 'DISCOVERED');
    assert.equal(discovery1.processInfo?.pid, 22000);
    assert.equal(discovery1.processInfo?.port, 45000);
    assert.equal(discovery1.processInfo?.protocol, 'https');
    assert.equal(discovery1.processInfo?.csrfToken, 'mock...9999'); // Masked in public info!
    assert.equal(inspector.findProcessesCount, 1);
    assert.equal(inspector.getListeningPortsCount, 1);

    // 2. Steady-State Call: should hit in-memory cache without spawning commands!
    const discovery2 = await detector.discover();
    assert.equal(discovery2.isRunning, true);
    assert.equal(discovery2.status, 'HEALTHY');
    assert.equal(discovery2.processInfo?.pid, 22000);
    assert.equal(inspector.findProcessesCount, 1); // DID NOT INCREMENT (Zero subprocesses!)
    assert.equal(inspector.getListeningPortsCount, 1); // DID NOT INCREMENT!
  });

  it('should invalidate cache when cached PID terminates and rediscover cleanly', async () => {
    const inspector = new MockProcessInspector();
    const rpcClient = new MockRpcClient();

    inspector.processes = [
      {
        pid: 22000,
        name: 'language_server.exe',
        commandLine: 'language_server.exe --standalone --csrf_token mock-token-11111'
      }
    ];
    inspector.listeningPorts = [45000];
    inspector.alivePids.add(22000);

    const detector = new AG2ProcessDetector({
      inspector,
      rpcClient,
      offlineCooldownMs: 100
    });

    await detector.discover();
    assert.equal(inspector.findProcessesCount, 1);

    // Simulate process death
    inspector.alivePids.delete(22000);

    // Next discovery should detect dead PID, clear cache, and run cold discovery
    inspector.processes = [
      {
        pid: 23000,
        name: 'language_server.exe',
        commandLine: 'language_server.exe --standalone --csrf_token mock-token-22222'
      }
    ];
    inspector.alivePids.add(23000);

    const discoveryAfterDeath = await detector.discover();
    assert.equal(discoveryAfterDeath.isRunning, true);
    assert.equal(discoveryAfterDeath.processInfo?.pid, 23000);
    assert.equal(inspector.findProcessesCount, 2); // Cold rediscovery was triggered
  });

  it('should invalidate cache when circuit breaker failure threshold is exceeded', async () => {
    const inspector = new MockProcessInspector();
    const rpcClient = new MockRpcClient();

    inspector.processes = [
      {
        pid: 22000,
        name: 'language_server.exe',
        commandLine: 'language_server.exe --standalone --csrf_token mock-token-11111'
      }
    ];
    inspector.listeningPorts = [45000];
    inspector.alivePids.add(22000);

    const detector = new AG2ProcessDetector({
      inspector,
      rpcClient,
      failureThreshold: 3
    });

    await detector.discover();
    assert.ok(detector.getCachedSession());

    // Record 2 failures (under threshold)
    detector.recordRpcFailure();
    detector.recordRpcFailure();
    assert.ok(detector.getCachedSession());

    // Record 3rd failure (trips circuit breaker)
    detector.recordRpcFailure();
    assert.equal(detector.getCachedSession(), null);
  });

  it('should throttle cold discovery while Antigravity is offline (offline cooldown)', async () => {
    const inspector = new MockProcessInspector();
    const rpcClient = new MockRpcClient();
    inspector.processes = []; // AG2 not running

    const detector = new AG2ProcessDetector({
      inspector,
      rpcClient,
      offlineCooldownMs: 5000
    });

    const res1 = await detector.discover();
    assert.equal(res1.isRunning, false);
    assert.equal(inspector.findProcessesCount, 1);

    // Second call immediately within cooldown window
    const res2 = await detector.discover();
    assert.equal(res2.isRunning, false);
    assert.match(res2.message || '', /cooldown/i);
    // Inspector was NOT called a second time
    assert.equal(inspector.findProcessesCount, 1);
  });
});
