/**
 * Test: Loopback HTTP Server & API Surface
 */

import { describe, it, before, after } from 'node:test';
import * as assert from 'node:assert/strict';
import * as http from 'node:http';
import * as path from 'node:path';
import { InMemoryAccountStore } from '../src/accounts/account-store.js';
import { AG2AdapterFoundation } from '../src/ag2/adapter.js';
import { AppConfig } from '../src/config/config.js';
import { QuotaRouter } from '../src/router/router.js';
import { AppServer } from '../src/server/server.js';

describe('AppServer (Loopback HTTP & API)', () => {
  let server: AppServer;
  let accountStore: InMemoryAccountStore;
  let adapter: AG2AdapterFoundation;
  let router: QuotaRouter;

  // Use an ephemeral test port
  const testPort = 39299;

  const testConfig: AppConfig = {
    host: '127.0.0.1',
    port: testPort,
    storageDir: path.resolve('data'),
    uiDir: path.resolve('src', 'ui'),
    router: {
      autoSwitchEnabled: false,
      lowQuotaThresholdPercent: 15,
      minimumCandidateQuotaPercent: 30,
      pollingIntervalMs: 10000
    },
    isDev: true
  };

  before(async () => {
    accountStore = new InMemoryAccountStore();
    adapter = new AG2AdapterFoundation();
    router = new QuotaRouter(accountStore, adapter, testConfig.router);
    server = new AppServer(testConfig, accountStore, adapter, router);

    await server.start();
  });

  after(async () => {
    await server.stop();
  });

  interface ExtendedRequestOptions extends http.RequestOptions {
    body?: string;
  }

  function request(
    urlPath: string,
    options: ExtendedRequestOptions = {}
  ): Promise<{ status: number; headers: http.IncomingHttpHeaders; body: string }> {
    return new Promise((resolve, reject) => {
      const req = http.request(
        {
          hostname: '127.0.0.1',
          port: testPort,
          path: urlPath,
          method: options.method || 'GET',
          headers: options.headers
        },
        (res) => {
          let body = '';
          res.on('data', (chunk) => (body += chunk));
          res.on('end', () => {
            resolve({
              status: res.statusCode || 0,
              headers: res.headers,
              body
            });
          });
        }
      );
      req.on('error', reject);
      if (options.body) {
        req.write(options.body);
      }
      req.end();
    });
  }

  it('should serve GET /api/status with truthful foundation state', async () => {
    const res = await request('/api/status');
    assert.equal(res.status, 200);
    assert.equal(res.headers['x-content-type-options'], 'nosniff');
    assert.equal(res.headers['x-frame-options'], 'DENY');

    const json = JSON.parse(res.body);
    assert.equal(json.status, 'ok');
    assert.equal(json.ag2.connected, false);
    assert.equal(json.ag2.status, 'OFFLINE');
    assert.equal(json.telemetry.totalAvailableQuotaPercent, null);
    assert.equal(json.router.autoSwitchEnabled, false);
    assert.equal(json.router.state, 'IDLE');
  });

  it('should handle accounts CRUD via /api/accounts', async () => {
    // 1. Initial list empty
    const listRes1 = await request('/api/accounts');
    assert.equal(listRes1.status, 200);
    const list1 = JSON.parse(listRes1.body);
    assert.equal(list1.accounts.length, 0);

    // 2. Add an account
    const postRes = await request('/api/accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: 'api-test@example.com', name: 'API Test', priority: 1 })
    } as http.RequestOptions & { body: string });

    assert.equal(postRes.status, 201);
    const created = JSON.parse(postRes.body).account;
    assert.equal(created.email, 'api-test@example.com');

    // 3. List contains newly created account
    const listRes2 = await request('/api/accounts');
    const list2 = JSON.parse(listRes2.body);
    assert.equal(list2.accounts.length, 1);
    assert.equal(list2.accounts[0].id, created.id);

    // 4. Test POST /api/accounts/enroll-current without enrollment service configured -> 501
    const enrollNoServiceRes = await request('/api/accounts/enroll-current', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({})
    } as http.RequestOptions & { body: string });
    assert.equal(enrollNoServiceRes.status, 501);

    // 5. Delete account
    const deleteRes = await request(`/api/accounts/${created.id}`, { method: 'DELETE' });
    assert.equal(deleteRes.status, 200);
    assert.equal(JSON.parse(deleteRes.body).success, true);
  });

  it('should get and update configuration via /api/config', async () => {
    const getRes = await request('/api/config');
    assert.equal(getRes.status, 200);
    const cfg = JSON.parse(getRes.body).config;
    assert.equal(cfg.autoSwitchEnabled, false);

    const postRes = await request('/api/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ autoSwitchEnabled: true, lowQuotaThresholdPercent: 20 })
    } as http.RequestOptions & { body: string });

    assert.equal(postRes.status, 200);
    const updated = JSON.parse(postRes.body).config;
    assert.equal(updated.autoSwitchEnabled, true);
    assert.equal(updated.lowQuotaThresholdPercent, 20);
  });

  it('should serve static dashboard assets (index.html, styles.css, app.js)', async () => {
    const htmlRes = await request('/');
    assert.equal(htmlRes.status, 200);
    assert.match(htmlRes.headers['content-type'] || '', /text\/html/);
    assert.match(htmlRes.body, /<title>AG2 Router<\/title>/);

    const cssRes = await request('/styles.css');
    assert.equal(cssRes.status, 200);
    assert.match(cssRes.headers['content-type'] || '', /text\/css/);

    const jsRes = await request('/app.js');
    assert.equal(jsRes.status, 200);
    assert.match(jsRes.headers['content-type'] || '', /application\/javascript/);
  });

  it('should prevent path traversal attempts with 403 Forbidden', async () => {
    const traversalRes = await request('/%2e%2e/%2e%2e/package.json');
    assert.equal(traversalRes.status, 403);
    assert.match(traversalRes.body, /path traversal detected/i);
  });

  it('should return 404 for unknown endpoints and non-existent assets', async () => {
    const notFoundApi = await request('/api/unknown-route');
    assert.equal(notFoundApi.status, 404);

    const notFoundAsset = await request('/does-not-exist.png');
    assert.equal(notFoundAsset.status, 404);
  });

  it('should serve normalized telemetry via GET /api/status without leaking secrets', async () => {
    // Create an AppServer instance with a mock live adapter returning real telemetry structures
    const mockLiveAdapter = {
      discover: async () => ({
        isRunning: true,
        status: 'HEALTHY' as const,
        processInfo: {
          pid: 22440,
          port: 51768,
          protocol: 'https' as const,
          csrfToken: 'mock...56bb',
          discoveredAt: new Date().toISOString()
        },
        message: 'Antigravity 2 connected'
      }),
      getCurrentAccount: async () => ({
        email: 'dev@example.com',
        tierName: 'Google AI Pro'
      }),
      getQuota: async () => ({
        timestamp: new Date().toISOString(),
        models: [
          {
            label: 'Gemini 3.8 Flash (High)',
            remainingFraction: 0.85,
            isExhausted: false
          }
        ],
        promptCredits: { availableCredits: 500, monthlyCredits: 50000, usedCredits: 49500 }
      }),
      getActivityState: async () => ({
        state: 'IDLE' as const,
        totalTrajectories: 10,
        runningTrajectories: 0,
        timestamp: new Date().toISOString()
      }),
      getLastTelemetryTimestamp: () => new Date().toISOString(),
      switchAccount: async () => { throw new Error('Not implemented'); },
      verifyAccount: async () => { throw new Error('Not implemented'); }
    };

    const liveServer = new AppServer(
      { ...testConfig, port: testPort + 1 },
      accountStore,
      mockLiveAdapter,
      router
    );

    await liveServer.start();
    try {
      const res = await new Promise<{ status: number; body: string }>((resolve, reject) => {
        const req = http.request(
          { hostname: '127.0.0.1', port: testPort + 1, path: '/api/status', method: 'GET' },
          (r) => {
            let body = '';
            r.on('data', (c) => (body += c));
            r.on('end', () => resolve({ status: r.statusCode || 0, body }));
          }
        );
        req.on('error', reject);
        req.end();
      });

      assert.equal(res.status, 200);
      const data = JSON.parse(res.body);

      // Verify connection & activity
      assert.equal(data.ag2.connected, true);
      assert.equal(data.ag2.status, 'HEALTHY');
      assert.equal(data.ag2.activity.state, 'IDLE');
      assert.equal(data.ag2.activity.runningTrajectories, 0);

      // Verify account and quota telemetry
      assert.equal(data.telemetry.currentAccount.email, 'dev@example.com');
      assert.equal(data.telemetry.currentAccount.tierName, 'Google AI Pro');
      assert.equal(data.telemetry.quota.models[0].label, 'Gemini 3.8 Flash (High)');
      assert.equal(data.telemetry.quota.models[0].remainingFraction, 0.85);
      assert.equal(data.telemetry.totalAvailableQuotaPercent, null);
      assert.ok(data.telemetry.lastSuccessfulTelemetry);

      // Invariant: no raw secret tokens in the response JSON
      assert.doesNotMatch(res.body, /--csrf_token/i);
      assert.doesNotMatch(res.body, /x-codeium-csrf-token/i);
      assert.doesNotMatch(res.body, /authorization/i);
    } finally {
      await liveServer.stop();
    }
  });

  it('should successfully enroll current account via POST /api/accounts/enroll-current and report hasVaultedSession: true in GET /api/accounts', async () => {
    const mockAdapter = {
      discover: async () => ({ isRunning: true, status: 'HEALTHY' as const, processInfo: null }),
      getCurrentAccount: async () => ({ email: 'enrolled@example.com', name: 'Enrolled User' }),
      getQuota: async () => null,
      getActivityState: async () => ({ state: 'IDLE' as const, totalTrajectories: 0, runningTrajectories: 0, timestamp: '' }),
      switchAccount: async () => { throw new Error('Not implemented'); },
      verifyAccount: async () => true
    };

    const mockWincred = {
      readCredential: async () => ({
        target: 'gemini:antigravity',
        type: 1,
        userName: 'antigravity',
        persistence: 2,
        blob: Buffer.from(JSON.stringify({ token: 'mock-auth-token-xyz' }), 'utf8')
      })
    };

    const mockDpapi = {
      encrypt: async (b: Buffer) => Buffer.concat([Buffer.from('ENC:'), b]),
      decrypt: async (b: Buffer) => Buffer.from(b.subarray(4))
    };

    const fs = await import('node:fs');
    const os = await import('node:os');
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-server-vault-'));

    try {
      const { SessionVault } = await import('../src/vault/session-vault.js');
      const { AccountEnrollmentService } = await import('../src/accounts/enrollment.js');

      const testVault = new SessionVault({ vaultDir: tempDir, dpapiProvider: mockDpapi });
      const testStore = new InMemoryAccountStore();
      const testEnrollService = new AccountEnrollmentService({
        adapter: mockAdapter,
        wincredReader: mockWincred,
        sessionVault: testVault,
        accountStore: testStore
      });

      const enrollServer = new AppServer(
        { ...testConfig, port: testPort + 2 },
        testStore,
        mockAdapter,
        router,
        testEnrollService,
        testVault
      );

      await enrollServer.start();
      try {
        // 1. Trigger enrollment
        const enrollRes = await new Promise<{ status: number; body: string }>((resolve, reject) => {
          const postData = JSON.stringify({ priority: 1, notes: 'Enrolled via API test' });
          const req = http.request(
            {
              hostname: '127.0.0.1',
              port: testPort + 2,
              path: '/api/accounts/enroll-current',
              method: 'POST',
              headers: {
                'Content-Type': 'application/json',
                'Content-Length': Buffer.byteLength(postData)
              }
            },
            (r) => {
              let b = '';
              r.on('data', (c) => (b += c));
              r.on('end', () => resolve({ status: r.statusCode || 0, body: b }));
            }
          );
          req.on('error', reject);
          req.write(postData);
          req.end();
        });

        assert.equal(enrollRes.status, 200);
        const enrollData = JSON.parse(enrollRes.body);
        assert.equal(enrollData.success, true);
        assert.equal(enrollData.account.email, 'enrolled@example.com');
        assert.equal(enrollData.account.hasVaultedSession, true);

        // 2. Fetch /api/accounts and verify hasVaultedSession is true and isActive is true
        const listRes = await new Promise<{ status: number; body: string }>((resolve, reject) => {
          const req = http.request(
            {
              hostname: '127.0.0.1',
              port: testPort + 2,
              path: '/api/accounts',
              method: 'GET'
            },
            (r) => {
              let b = '';
              r.on('data', (c) => (b += c));
              r.on('end', () => resolve({ status: r.statusCode || 0, body: b }));
            }
          );
          req.on('error', reject);
          req.end();
        });

        assert.equal(listRes.status, 200);
        const listData = JSON.parse(listRes.body);
        assert.equal(listData.accounts.length, 1);
        assert.equal(listData.accounts[0].email, 'enrolled@example.com');
        assert.equal(listData.accounts[0].hasVaultedSession, true);
        assert.equal(listData.accounts[0].isActive, true);

        // 3. Test DELETE /api/accounts/:id reports vaultRecordDeleted: true
        const delRes = await new Promise<{ status: number; body: string }>((resolve, reject) => {
          const req = http.request(
            {
              hostname: '127.0.0.1',
              port: testPort + 2,
              path: `/api/accounts/${enrollData.account.id}`,
              method: 'DELETE'
            },
            (r) => {
              let b = '';
              r.on('data', (c) => (b += c));
              r.on('end', () => resolve({ status: r.statusCode || 0, body: b }));
            }
          );
          req.on('error', reject);
          req.end();
        });

        assert.equal(delRes.status, 200);
        const delData = JSON.parse(delRes.body);
        assert.equal(delData.success, true);
        assert.equal(delData.vaultRecordDeleted, true);
      } finally {
        await enrollServer.stop();
      }
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('should serve GET /api/switching/status with default idle status', async () => {
    const res = await request('/api/switching/status');
    assert.equal(res.status, 200);
    const json = JSON.parse(res.body);
    assert.equal(json.status.currentState, 'IDLE');
    assert.equal(json.status.activeTransactionId, null);
  });

  it('should reject POST /api/accounts/:id/switch with 403 Forbidden', async () => {
    const res = await request('/api/accounts/acc_test/switch', { method: 'POST' });
    assert.equal(res.status, 403);
    const json = JSON.parse(res.body);
    assert.match(json.error, /Live account switching execution is not authorized/);
  });

  it('should evaluate switch plan via POST /api/accounts/:id/switch-plan when planner is configured', async () => {
    // 1. Unconfigured planner returns 501
    const unconfRes = await request('/api/accounts/acc_test/switch-plan', { method: 'POST' });
    assert.equal(unconfRes.status, 501);

    // 2. Configured planner
    const plannerServer = new AppServer(
      { ...testConfig, port: testPort + 3 },
      accountStore,
      adapter,
      router,
      undefined,
      undefined,
      {
        planSwitch: async (id: string) => ({
          ready: false,
          targetAccountId: id,
          targetEmail: 'test@example.com',
          currentAccountId: null,
          currentEmail: null,
          checks: [
            { code: 'TARGET_ACCOUNT_EXISTS' as const, passed: true, message: 'OK' }
          ],
          blockers: ['Antigravity 2 is offline'],
          plannedAt: new Date().toISOString()
        })
      } as any
    );

    await plannerServer.start();
    try {
      const planRes = await new Promise<{ status: number; body: string }>((resolve, reject) => {
        const req = http.request(
          {
            hostname: '127.0.0.1',
            port: testPort + 3,
            path: '/api/accounts/acc_plan_test/switch-plan',
            method: 'POST'
          },
          (r) => {
            let b = '';
            r.on('data', (c) => (b += c));
            r.on('end', () => resolve({ status: r.statusCode || 0, body: b }));
          }
        );
        req.on('error', reject);
        req.end();
      });

      assert.equal(planRes.status, 200);
      const json = JSON.parse(planRes.body);
      assert.equal(json.plan.ready, false);
      assert.equal(json.plan.targetAccountId, 'acc_plan_test');
      assert.equal(json.plan.blockers.length, 1);
    } finally {
      await plannerServer.stop();
    }
  });
});
