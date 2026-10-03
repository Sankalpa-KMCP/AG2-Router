/**
 * Test: Loopback HTTP Server & API Surface
 */

import { describe, it, before, after } from 'node:test';
import * as assert from 'node:assert/strict';
import * as http from 'node:http';
import * as path from 'node:path';
import * as fs from 'node:fs';
import * as os from 'node:os';
import { InMemoryAccountStore } from '../src/accounts/account-store.js';
import { AccountEnrollmentService } from '../src/accounts/enrollment.js';
import { AG2AdapterFoundation } from '../src/ag2/adapter.js';
import { normalizeQuotaSnapshot, type RawUserStatusResponse } from '../src/ag2/normalizer.js';
import { AppConfig } from '../src/config/config.js';
import { QuotaRouter } from '../src/router/router.js';
import { SessionVault } from '../src/vault/session-vault.js';
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

  it('should reject invalid configuration bounds via POST /api/config and preserve config', async () => {
    // Current config snapshot
    const baselineRes = await request('/api/config');
    assert.equal(baselineRes.status, 200);
    const baselineCfg = JSON.parse(baselineRes.body).config;

    const invalidPayloads = [
      { pollingIntervalMs: 0 },
      { pollingIntervalMs: -100 },
      { pollingIntervalMs: 0.5 },
      { pollingIntervalMs: 1.5 },
      { pollingIntervalMs: 2147483648 },
      { pollingIntervalMs: 99999999999 },
      { lowQuotaThresholdPercent: 4 },
      { lowQuotaThresholdPercent: 55 },
      { minimumCandidateQuotaPercent: 9 },
      { minimumCandidateQuotaPercent: 95 },
      { workloadModelKey: '   ' },
      { workloadModelKey: 'a'.repeat(129) },
      { workloadModelKey: 'gemini\npro' },
      { workloadModelKey: 'gemini pro' }
    ];

    for (const payload of invalidPayloads) {
      const res = await request('/api/config', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      } as http.RequestOptions & { body: string });

      assert.equal(res.status, 400, `Expected 400 Bad Request for ${JSON.stringify(payload)}`);
      const body = JSON.parse(res.body);
      assert.ok(body.error, 'Error message must be present in response');

      // Verify router config remained unchanged
      const checkRes = await request('/api/config');
      assert.deepEqual(JSON.parse(checkRes.body).config, baselineCfg);
    }
  });

  it('should accept valid workloadModelKey and null via POST /api/config', async () => {
    const postRes1 = await request('/api/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ workloadModelKey: 'gemini-2.5-pro' })
    } as http.RequestOptions & { body: string });

    assert.equal(postRes1.status, 200);
    const updated1 = JSON.parse(postRes1.body).config;
    assert.equal(updated1.workloadModelKey, 'gemini-2.5-pro');

    const checkRes1 = await request('/api/config');
    assert.equal(checkRes1.status, 200);
    assert.equal(JSON.parse(checkRes1.body).config.workloadModelKey, 'gemini-2.5-pro');

    // Setting back to null
    const postRes2 = await request('/api/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ workloadModelKey: null })
    } as http.RequestOptions & { body: string });

    assert.equal(postRes2.status, 200);
    const updated2 = JSON.parse(postRes2.body).config;
    assert.equal(updated2.workloadModelKey, null);

    const checkRes2 = await request('/api/config');
    assert.equal(checkRes2.status, 200);
    assert.equal(JSON.parse(checkRes2.body).config.workloadModelKey, null);
  });

  it('should accept 1 ms polling interval via POST /api/config proving 1 ms is valid and 2000 ms is not backend minimum', async () => {
    const postRes = await request('/api/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pollingIntervalMs: 1 })
    } as http.RequestOptions & { body: string });

    assert.equal(postRes.status, 200);
    const updated = JSON.parse(postRes.body).config;
    assert.equal(updated.pollingIntervalMs, 1);

    const checkRes = await request('/api/config');
    assert.equal(checkRes.status, 200);
    assert.equal(JSON.parse(checkRes.body).config.pollingIntervalMs, 1);
  });

  it('should accept 2147483647 ms polling interval via POST /api/config proving Int32.MaxValue is valid', async () => {
    const postRes = await request('/api/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pollingIntervalMs: 2147483647 })
    } as http.RequestOptions & { body: string });

    assert.equal(postRes.status, 200);
    const updated = JSON.parse(postRes.body).config;
    assert.equal(updated.pollingIntervalMs, 2147483647);

    const checkRes = await request('/api/config');
    assert.equal(checkRes.status, 200);
    assert.equal(JSON.parse(checkRes.body).config.pollingIntervalMs, 2147483647);
  });

  it('should reject non-object or malformed body via POST /api/config', async () => {
    const res = await request('/api/config', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify('not-an-object')
    } as http.RequestOptions & { body: string });

    assert.equal(res.status, 400);
    const body = JSON.parse(res.body);
    assert.ok(body.error);
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

  it('should expose totalCount, activeAccountId, and alias in GET /api/accounts', async () => {
    const acc1 = await accountStore.addAccount({ email: 'first@example.com', name: 'First User' });
    const acc2 = await accountStore.addAccount({ email: 'second@example.com', name: 'Second User', alias: 'Backup' });
    await accountStore.setActiveAccountId(acc2.id);

    try {
      const res = await request('/api/accounts');
      assert.equal(res.status, 200);
      const data = JSON.parse(res.body);

      assert.equal(data.totalCount, 2);
      assert.equal(data.activeAccountId, acc2.id);
      assert.equal(data.accounts.length, 2);

      const a1 = data.accounts.find((a: any) => a.id === acc1.id);
      assert.equal(a1.isActive, false);
      assert.equal(a1.alias, undefined);

      const a2 = data.accounts.find((a: any) => a.id === acc2.id);
      assert.equal(a2.isActive, true);
      assert.equal(a2.alias, 'Backup');
    } finally {
      await accountStore.removeAccount(acc1.id);
      await accountStore.removeAccount(acc2.id);
      await accountStore.setActiveAccountId(null);
    }
  });

  it('should handle PATCH /api/accounts/:id alias mutations (update, trim, clear, and security)', async () => {
    const acc = await accountStore.addAccount({ email: 'patch_test@example.com', name: 'Patch Target' });

    try {
      // 1. Update with trimming
      const updateRes = await request(`/api/accounts/${acc.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ alias: '   Work Production   ' })
      });
      assert.equal(updateRes.status, 200);
      const updateData = JSON.parse(updateRes.body);
      assert.equal(updateData.success, true);
      assert.equal(updateData.account.alias, 'Work Production');
      assert.equal(updateData.account.email, 'patch_test@example.com');
      assert.equal(updateData.account.name, 'Patch Target');

      // Verify store persisted
      const loaded1 = await accountStore.getAccount(acc.id);
      assert.equal(loaded1?.alias, 'Work Production');

      // 2. Whitespace clears alias
      const clearWsRes = await request(`/api/accounts/${acc.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ alias: '   ' })
      });
      assert.equal(clearWsRes.status, 200);
      const clearWsData = JSON.parse(clearWsRes.body);
      assert.equal(clearWsData.account.alias, undefined);

      // 3. Null clears alias
      await accountStore.updateAccount(acc.id, { alias: 'Temporary' });
      const clearNullRes = await request(`/api/accounts/${acc.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ alias: null })
      });
      assert.equal(clearNullRes.status, 200);
      const clearNullData = JSON.parse(clearNullRes.body);
      assert.equal(clearNullData.account.alias, undefined);

      // 4. Unknown account returns 404
      const notFoundRes = await request('/api/accounts/non_existent_id', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ alias: 'New' })
      });
      assert.equal(notFoundRes.status, 404);

      // 5. Malformed payload returns 400
      const malformedRes = await request(`/api/accounts/${acc.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: '{ not valid json'
      });
      assert.equal(malformedRes.status, 400);

      // 6. Invalid alias type returns 400
      const invalidTypeRes = await request(`/api/accounts/${acc.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ alias: 12345 })
      });
      assert.equal(invalidTypeRes.status, 400);

      // 7. Unauthorized origin rejected with 403
      const foreignRes = await request(`/api/accounts/${acc.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json', Origin: 'http://evil.com' },
        body: JSON.stringify({ alias: 'Hacked' })
      });
      assert.equal(foreignRes.status, 403);

      // 8. Sec-Fetch-Site cross-site rejected with 403
      const crossSiteRes = await request(`/api/accounts/${acc.id}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json', 'Sec-Fetch-Site': 'cross-site' },
        body: JSON.stringify({ alias: 'CrossSite' })
      });
      assert.equal(crossSiteRes.status, 403);
    } finally {
      await accountStore.removeAccount(acc.id);
    }
  });

  it('should expose canonical quota models in GET /api/status when adapter supplies them', async () => {
    const customAdapter: any = {
      discover: async () => ({ isRunning: true, status: 'CONNECTED' }),
      getCurrentAccount: async () => ({ email: 'dev@example.com', name: 'Dev' }),
      getQuota: async () => ({
        timestamp: '2026-09-21T18:00:00Z',
        models: [
          { label: 'Gemini 2.5 Pro', modelOrTier: 'gemini-2.5-pro', remainingFraction: 0.85, isExhausted: false },
          { label: 'Gemini 2.5 Pro (Thinking)', modelOrTier: 'gemini-2.5-pro', remainingFraction: 0.85, isExhausted: false }
        ],
        promptCredits: { availableCredits: 1000, monthlyCredits: 2000, usedCredits: 1000 },
        flowCredits: { availableCredits: 200, monthlyCredits: 500, usedCredits: 300 },
        canonicalModels: [
          {
            key: 'tier:gemini-2.5-pro',
            label: 'Gemini 2.5 Pro',
            modelOrTier: 'gemini-2.5-pro',
            remainingFraction: 0.85,
            isExhausted: false,
            modes: ['Standard', 'Thinking']
          }
        ]
      }),
      getActivityState: async () => ({ state: 'IDLE', totalTrajectories: 0, runningTrajectories: 0, timestamp: '' })
    };

    const quotaServer = new AppServer(
      { ...testConfig, port: testPort + 4 },
      accountStore,
      customAdapter,
      router
    );

    await quotaServer.start();
    try {
      const res = await new Promise<{ status: number; body: string }>((resolve, reject) => {
        const req = http.request(
          {
            hostname: '127.0.0.1',
            port: testPort + 4,
            path: '/api/status',
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

      assert.equal(res.status, 200);
      const json = JSON.parse(res.body);
      const quota = json.telemetry.quota;

      assert.equal(quota.models.length, 2);
      assert.equal(quota.canonicalModels.length, 1);
      assert.equal(quota.canonicalModels[0].key, 'tier:gemini-2.5-pro');
      assert.equal(quota.canonicalModels[0].label, 'Gemini 2.5 Pro');
      assert.equal(quota.canonicalModels[0].canonicalKey, 'tier:gemini-2.5-pro');
      assert.equal(quota.canonicalModels[0].displayLabel, 'Gemini 2.5 Pro');
      assert.deepEqual(quota.canonicalModels[0].modes, ['Standard', 'Thinking']);
      assert.equal(quota.promptCredits.availableCredits, 1000);
      assert.equal(quota.flowCredits.availableCredits, 200);
    } finally {
      await quotaServer.stop();
    }
  });

  it('preserves missing, partial and observed-zero credits through GET /api/status', async () => {
    let raw: RawUserStatusResponse = { userStatus: { planStatus: { planInfo: {} } } };
    const normalizedAdapter: any = {
      discover: async () => ({ isRunning: true, status: 'CONNECTED' }),
      getCurrentAccount: async () => null,
      getQuota: async () => normalizeQuotaSnapshot(raw),
      getActivityState: async () => ({ state: 'IDLE', totalTrajectories: 0, runningTrajectories: 0, timestamp: '' })
    };
    const quotaServer = new AppServer({ ...testConfig, port: testPort + 5 }, accountStore, normalizedAdapter, router);
    await quotaServer.start();
    try {
      async function readCredits() {
        const body = await new Promise<string>((resolve, reject) => {
          const req = http.get({ hostname: '127.0.0.1', port: testPort + 5, path: '/api/status' }, res => {
            let data = '';
            res.on('data', chunk => data += chunk);
            res.on('end', () => resolve(data));
          });
          req.on('error', reject);
        });
        return JSON.parse(body).telemetry.quota;
      }

      let quota = await readCredits();
      assert.deepEqual(quota.promptCredits, { availableCredits: null, monthlyCredits: null, usedCredits: null });
      assert.deepEqual(quota.flowCredits, { availableCredits: null, monthlyCredits: null, usedCredits: null });

      raw = { userStatus: { planStatus: { availablePromptCredits: 7, planInfo: { monthlyFlowCredits: 20 } } } };
      quota = await readCredits();
      assert.deepEqual(quota.promptCredits, { availableCredits: 7, monthlyCredits: null, usedCredits: null });
      assert.deepEqual(quota.flowCredits, { availableCredits: null, monthlyCredits: 20, usedCredits: null });

      raw = { userStatus: { planStatus: { availablePromptCredits: 0, availableFlowCredits: 0,
        planInfo: { monthlyPromptCredits: 0, monthlyFlowCredits: 10 } } } };
      quota = await readCredits();
      assert.deepEqual(quota.promptCredits, { availableCredits: 0, monthlyCredits: 0, usedCredits: 0 });
      assert.deepEqual(quota.flowCredits, { availableCredits: 0, monthlyCredits: 10, usedCredits: 10 });
    } finally {
      await quotaServer.stop();
    }
  });
});

describe('R04: Central Loopback HTTP Mutation Origin & Fetch-Context Security Boundary', () => {
  const r04Port = 39330;
  const enrollPort = 39331;

  function makeRequest(
    port: number,
    urlPath: string,
    options: {
      method?: string;
      headers?: http.OutgoingHttpHeaders;
      body?: string;
    } = {}
  ): Promise<{ status: number; headers: http.IncomingHttpHeaders; body: string }> {
    return new Promise((resolve, reject) => {
      const headers: http.OutgoingHttpHeaders = { ...options.headers };
      if (!headers['connection'] && !headers['Connection']) {
        headers['Connection'] = 'close';
      }
      if (options.body !== undefined && !headers['content-length'] && !headers['Content-Length']) {
        headers['Content-Length'] = Buffer.byteLength(options.body);
      }
      const req = http.request(
        {
          hostname: '127.0.0.1',
          port,
          path: urlPath,
          method: options.method || 'GET',
          headers
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
      if (options.body !== undefined) {
        req.write(options.body);
      }
      req.end();
    });
  }

  describe('Enrollment Mutation Endpoint Security', () => {
    let enrollServer: AppServer;
    let tempVaultDir: string;
    let testStore: InMemoryAccountStore;
    let testVault: SessionVault;
    let enrollService: AccountEnrollmentService;

    const mockAdapter = {
      discover: async () => ({ isRunning: true, status: 'HEALTHY' as const, processInfo: null }),
      getCurrentAccount: async () => ({ email: 'enroll-sec@example.com', name: 'Enroll Sec User' }),
      getQuota: async () => null,
      getActivityState: async () => ({ state: 'IDLE' as const, totalTrajectories: 0, runningTrajectories: 0, timestamp: '' }),
      verifyAccount: async () => true,
      switchAccount: async () => { throw new Error('Not implemented'); }
    };

    const mockWincred = {
      readCredential: async () => ({
        target: 'gemini:antigravity',
        type: 1,
        userName: 'antigravity',
        persistence: 2,
        blob: Buffer.from(JSON.stringify({ token: 'sec-enroll-token' }), 'utf8')
      })
    };

    const mockDpapi = {
      encrypt: async (b: Buffer) => Buffer.concat([Buffer.from('ENC:'), b]),
      decrypt: async (b: Buffer) => Buffer.from(b.subarray(4))
    };

    before(async () => {
      tempVaultDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ag2-r04-vault-'));
      testVault = new SessionVault({ vaultDir: tempVaultDir, dpapiProvider: mockDpapi });
      testStore = new InMemoryAccountStore();
      enrollService = new AccountEnrollmentService({
        adapter: mockAdapter,
        wincredReader: mockWincred,
        sessionVault: testVault,
        accountStore: testStore
      });
      const router = new QuotaRouter(testStore, mockAdapter, {
        autoSwitchEnabled: false,
        lowQuotaThresholdPercent: 15,
        minimumCandidateQuotaPercent: 30,
        pollingIntervalMs: 10000
      });
      enrollServer = new AppServer(
        {
          host: '127.0.0.1',
          port: enrollPort,
          storageDir: tempVaultDir,
          uiDir: path.resolve('src', 'ui'),
          router: {
            autoSwitchEnabled: false,
            lowQuotaThresholdPercent: 15,
            minimumCandidateQuotaPercent: 30,
            pollingIntervalMs: 10000
          },
          isDev: true
        },
        testStore,
        mockAdapter,
        router,
        enrollService,
        testVault
      );
      await enrollServer.start();
    });

    after(async () => {
      await enrollServer.stop();
      fs.rmSync(tempVaultDir, { recursive: true, force: true });
    });

    it('1. allowed local/same-origin browser enrollment request succeeds normally', async () => {
      const res = await makeRequest(enrollPort, '/api/accounts/enroll-current', {
        method: 'POST',
        headers: {
          Origin: `http://127.0.0.1:${enrollPort}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ priority: 1, notes: 'same-origin browser' })
      });
      assert.equal(res.status, 200);
      const data = JSON.parse(res.body);
      assert.equal(data.success, true);
      assert.equal(data.account.email, 'enroll-sec@example.com');
      assert.equal(data.account.hasVaultedSession, true);
    });

    it('2. legitimate native enrollment request with no Origin succeeds according to contract', async () => {
      const res = await makeRequest(enrollPort, '/api/accounts/enroll-current', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ priority: 2, notes: 'native client' })
      });
      assert.equal(res.status, 200);
      const data = JSON.parse(res.body);
      assert.equal(data.success, true);
      assert.equal(data.account.email, 'enroll-sec@example.com');
    });

    it('3. foreign Origin POST enrollment is rejected with 403 Unauthorized origin', async () => {
      const res = await makeRequest(enrollPort, '/api/accounts/enroll-current', {
        method: 'POST',
        headers: {
          Origin: 'http://malicious-website.com',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ priority: 5, notes: 'attacker payload' })
      });
      assert.equal(res.status, 403);
      const data = JSON.parse(res.body);
      assert.equal(data.error, 'Unauthorized origin.');
    });

    it('4. cross-site fetch-context POST enrollment is rejected with 403 Cross-site requests forbidden', async () => {
      const res = await makeRequest(enrollPort, '/api/accounts/enroll-current', {
        method: 'POST',
        headers: {
          'Sec-Fetch-Site': 'cross-site',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ priority: 5, notes: 'cross site payload' })
      });
      assert.equal(res.status, 403);
      const data = JSON.parse(res.body);
      assert.equal(data.error, 'Cross-site requests forbidden.');
    });

    it('5. foreign request produces zero enrollment side effects', async () => {
      const accountsBefore = await testStore.listAccounts();
      const countBefore = accountsBefore.length;

      const res = await makeRequest(enrollPort, '/api/accounts/enroll-current', {
        method: 'POST',
        headers: {
          Origin: 'http://evil.com',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ priority: 99, notes: 'side effect probe' })
      });
      assert.equal(res.status, 403);

      const accountsAfter = await testStore.listAccounts();
      assert.equal(accountsAfter.length, countBefore, 'Account store count must not change on rejected foreign request');
      assert.deepEqual(accountsAfter.map(a => a.id), accountsBefore.map(a => a.id));
    });

    it('6. empty-body enrollment cannot bypass origin or fetch-context boundary', async () => {
      // Empty body with foreign origin
      const resOrigin = await makeRequest(enrollPort, '/api/accounts/enroll-current', {
        method: 'POST',
        headers: {
          Origin: 'http://attacker.example.com'
        },
        body: ''
      });
      assert.equal(resOrigin.status, 403);
      assert.equal(JSON.parse(resOrigin.body).error, 'Unauthorized origin.');

      // Empty body with cross-site fetch context
      const resFetchSite = await makeRequest(enrollPort, '/api/accounts/enroll-current', {
        method: 'POST',
        headers: {
          'Sec-Fetch-Site': 'cross-site'
        },
        body: ''
      });
      assert.equal(resFetchSite.status, 403);
      assert.equal(JSON.parse(resFetchSite.body).error, 'Cross-site requests forbidden.');
    });
  });

  describe('Central Mutation Security for Configuration, Accounts, and Edge Cases', () => {
    let mainServer: AppServer;
    let store: InMemoryAccountStore;
    let adapter: AG2AdapterFoundation;
    let router: QuotaRouter;

    before(async () => {
      store = new InMemoryAccountStore();
      adapter = new AG2AdapterFoundation();
      router = new QuotaRouter(store, adapter, {
        autoSwitchEnabled: false,
        lowQuotaThresholdPercent: 15,
        minimumCandidateQuotaPercent: 30,
        pollingIntervalMs: 10000
      });
      mainServer = new AppServer(
        {
          host: '127.0.0.1',
          port: r04Port,
          storageDir: path.resolve('data'),
          uiDir: path.resolve('src', 'ui'),
          router: {
            autoSwitchEnabled: false,
            lowQuotaThresholdPercent: 15,
            minimumCandidateQuotaPercent: 30,
            pollingIntervalMs: 10000
          },
          isDev: true
        },
        store,
        adapter,
        router
      );
      await mainServer.start();
    });

    after(async () => {
      await mainServer.stop();
    });

    // --- Configuration Tests ---
    it('configuration: allowed mutation works with same-origin header', async () => {
      const res = await makeRequest(r04Port, '/api/config', {
        method: 'POST',
        headers: {
          Origin: `http://127.0.0.1:${r04Port}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ autoSwitchEnabled: true, lowQuotaThresholdPercent: 25 })
      });
      assert.equal(res.status, 200);
      const data = JSON.parse(res.body);
      assert.equal(data.config.autoSwitchEnabled, true);
      assert.equal(data.config.lowQuotaThresholdPercent, 25);
    });

    it('configuration: foreign Origin JSON request rejected with 403', async () => {
      const res = await makeRequest(r04Port, '/api/config', {
        method: 'POST',
        headers: {
          Origin: 'http://malicious.org',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ autoSwitchEnabled: false, lowQuotaThresholdPercent: 10 })
      });
      assert.equal(res.status, 403);
      assert.equal(JSON.parse(res.body).error, 'Unauthorized origin.');
    });

    it('configuration: foreign Origin text/plain simple POST rejected before mutation (403)', async () => {
      const res = await makeRequest(r04Port, '/api/config', {
        method: 'POST',
        headers: {
          Origin: 'http://malicious.org',
          'Content-Type': 'text/plain'
        },
        body: JSON.stringify({ autoSwitchEnabled: false, lowQuotaThresholdPercent: 10 })
      });
      assert.equal(res.status, 403);
      assert.equal(JSON.parse(res.body).error, 'Unauthorized origin.');
    });

    it('configuration: cross-site fetch context rejected with 403', async () => {
      const res = await makeRequest(r04Port, '/api/config', {
        method: 'POST',
        headers: {
          'Sec-Fetch-Site': 'cross-site',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ autoSwitchEnabled: false, lowQuotaThresholdPercent: 10 })
      });
      assert.equal(res.status, 403);
      assert.equal(JSON.parse(res.body).error, 'Cross-site requests forbidden.');
    });

    it('configuration: durable configuration remains unchanged after rejection', async () => {
      const res = await makeRequest(r04Port, '/api/config');
      assert.equal(res.status, 200);
      const cfg = JSON.parse(res.body).config;
      assert.equal(cfg.autoSwitchEnabled, true, 'autoSwitchEnabled must remain true');
      assert.equal(cfg.lowQuotaThresholdPercent, 25, 'lowQuotaThresholdPercent must remain 25');
    });

    // --- Existing Account Mutation Endpoints ---
    it('accounts CRUD: POST, PATCH, and DELETE still enforce foreign Origin and Sec-Fetch-Site', async () => {
      // 1. POST /api/accounts rejects foreign Origin
      const postForeign = await makeRequest(r04Port, '/api/accounts', {
        method: 'POST',
        headers: {
          Origin: 'http://evil.com',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ email: 'attacker@evil.com' })
      });
      assert.equal(postForeign.status, 403);
      assert.equal(JSON.parse(postForeign.body).error, 'Unauthorized origin.');

      // 2. POST /api/accounts rejects cross-site
      const postCross = await makeRequest(r04Port, '/api/accounts', {
        method: 'POST',
        headers: {
          'Sec-Fetch-Site': 'cross-site',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ email: 'cross@evil.com' })
      });
      assert.equal(postCross.status, 403);
      assert.equal(JSON.parse(postCross.body).error, 'Cross-site requests forbidden.');

      // Create an account legitimately via native request
      const created = await store.addAccount({ email: 'sec-target@example.com', name: 'Sec Target' });

      // 3. PATCH /api/accounts/:id rejects foreign Origin
      const patchForeign = await makeRequest(r04Port, `/api/accounts/${created.id}`, {
        method: 'PATCH',
        headers: {
          Origin: 'http://evil.com',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ alias: 'Hacked Alias' })
      });
      assert.equal(patchForeign.status, 403);
      assert.equal(JSON.parse(patchForeign.body).error, 'Unauthorized origin.');

      // 4. PATCH /api/accounts/:id rejects cross-site
      const patchCross = await makeRequest(r04Port, `/api/accounts/${created.id}`, {
        method: 'PATCH',
        headers: {
          'Sec-Fetch-Site': 'cross-site',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ alias: 'Cross Alias' })
      });
      assert.equal(patchCross.status, 403);
      assert.equal(JSON.parse(patchCross.body).error, 'Cross-site requests forbidden.');

      // Verify alias untouched
      const accountCheck = await store.getAccount(created.id);
      assert.equal(accountCheck?.alias, undefined);

      // 5. DELETE /api/accounts/:id rejects foreign Origin
      const deleteForeign = await makeRequest(r04Port, `/api/accounts/${created.id}`, {
        method: 'DELETE',
        headers: {
          Origin: 'http://evil.com'
        }
      });
      assert.equal(deleteForeign.status, 403);
      assert.equal(JSON.parse(deleteForeign.body).error, 'Unauthorized origin.');

      // 6. DELETE /api/accounts/:id rejects cross-site
      const deleteCross = await makeRequest(r04Port, `/api/accounts/${created.id}`, {
        method: 'DELETE',
        headers: {
          'Sec-Fetch-Site': 'cross-site'
        }
      });
      assert.equal(deleteCross.status, 403);
      assert.equal(JSON.parse(deleteCross.body).error, 'Cross-site requests forbidden.');

      // Clean up account legitimately
      const deleteValid = await makeRequest(r04Port, `/api/accounts/${created.id}`, {
        method: 'DELETE'
      });
      assert.equal(deleteValid.status, 200);
    });

    // --- Bypass / Edge Case Audits ---
    it('edge cases: missing Origin is accepted for native clients', async () => {
      const res = await makeRequest(r04Port, '/api/config', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ lowQuotaThresholdPercent: 18 })
      });
      assert.equal(res.status, 200);
      assert.equal(JSON.parse(res.body).config.lowQuotaThresholdPercent, 18);
    });

    it('edge cases: Origin: null is rejected as foreign across mutation routes', async () => {
      const endpoints = [
        { method: 'POST', path: '/api/config', body: JSON.stringify({ lowQuotaThresholdPercent: 20 }) },
        { method: 'POST', path: '/api/accounts', body: JSON.stringify({ email: 'null-origin@example.com' }) },
        { method: 'POST', path: '/api/accounts/enroll-current', body: '{}' },
        { method: 'PATCH', path: '/api/accounts/dummy_id', body: JSON.stringify({ alias: 'Test' }) },
        { method: 'DELETE', path: '/api/accounts/dummy_id' }
      ];

      for (const ep of endpoints) {
        const res = await makeRequest(r04Port, ep.path, {
          method: ep.method,
          headers: {
            Origin: 'null',
            'Content-Type': 'application/json'
          },
          body: ep.body
        });
        assert.equal(res.status, 403, `Expected 403 for ${ep.method} ${ep.path} with Origin: null`);
        assert.equal(JSON.parse(res.body).error, 'Unauthorized origin.');
      }
    });

    it('edge cases: method casing (post, Post, patch, delete) enforces security boundary', async () => {
      // Lowercase 'post'
      const resPostLower = await makeRequest(r04Port, '/api/config', {
        method: 'post',
        headers: {
          Origin: 'http://attacker.org',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ lowQuotaThresholdPercent: 30 })
      });
      assert.equal(resPostLower.status, 403);
      assert.equal(JSON.parse(resPostLower.body).error, 'Unauthorized origin.');

      // Mixed case 'Post'
      const resPostMixed = await makeRequest(r04Port, '/api/config', {
        method: 'Post',
        headers: {
          Origin: 'http://attacker.org',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ lowQuotaThresholdPercent: 30 })
      });
      assert.equal(resPostMixed.status, 403);
      assert.equal(JSON.parse(resPostMixed.body).error, 'Unauthorized origin.');

      // Lowercase 'patch'
      const resPatchLower = await makeRequest(r04Port, '/api/accounts/test', {
        method: 'patch',
        headers: {
          Origin: 'http://attacker.org',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ alias: 'hack' })
      });
      assert.equal(resPatchLower.status, 403);
      assert.equal(JSON.parse(resPatchLower.body).error, 'Unauthorized origin.');

      // Lowercase 'delete'
      const resDeleteLower = await makeRequest(r04Port, '/api/accounts/test', {
        method: 'delete',
        headers: {
          Origin: 'http://attacker.org'
        }
      });
      assert.equal(resDeleteLower.status, 403);
      assert.equal(JSON.parse(resDeleteLower.body).error, 'Unauthorized origin.');
    });

    it('edge cases: query strings on mutation endpoints cannot bypass security boundary', async () => {
      const resForeign = await makeRequest(r04Port, '/api/config?test=1&debug=true', {
        method: 'POST',
        headers: {
          Origin: 'http://attacker.org',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ lowQuotaThresholdPercent: 40 })
      });
      assert.equal(resForeign.status, 403);
      assert.equal(JSON.parse(resForeign.body).error, 'Unauthorized origin.');

      const resCross = await makeRequest(r04Port, '/api/config?test=1&debug=true', {
        method: 'POST',
        headers: {
          'Sec-Fetch-Site': 'cross-site',
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ lowQuotaThresholdPercent: 40 })
      });
      assert.equal(resCross.status, 403);
      assert.equal(JSON.parse(resCross.body).error, 'Cross-site requests forbidden.');

      // Allowed mutation with query string works
      const resAllowed = await makeRequest(r04Port, '/api/config?param=ok', {
        method: 'POST',
        headers: {
          Origin: `http://127.0.0.1:${r04Port}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ lowQuotaThresholdPercent: 19 })
      });
      assert.equal(resAllowed.status, 200);
      assert.equal(JSON.parse(resAllowed.body).config.lowQuotaThresholdPercent, 19);
    });

    it('edge cases: trailing slashes on mutation endpoints reject foreign Origin and cross-site requests', async () => {
      const slashEndpoints = [
        { method: 'POST', path: '/api/config/' },
        { method: 'POST', path: '/api/accounts/' },
        { method: 'POST', path: '/api/accounts/enroll-current/' },
        { method: 'PATCH', path: '/api/accounts/dummy/' },
        { method: 'DELETE', path: '/api/accounts/dummy/' }
      ];

      for (const ep of slashEndpoints) {
        const resForeign = await makeRequest(r04Port, ep.path, {
          method: ep.method,
          headers: {
            Origin: 'http://evil.com',
            'Content-Type': 'application/json'
          },
          body: '{}'
        });
        assert.equal(resForeign.status, 403, `Expected 403 for ${ep.method} ${ep.path} with foreign Origin`);
        assert.equal(JSON.parse(resForeign.body).error, 'Unauthorized origin.');

        const resCross = await makeRequest(r04Port, ep.path, {
          method: ep.method,
          headers: {
            'Sec-Fetch-Site': 'cross-site',
            'Content-Type': 'application/json'
          },
          body: '{}'
        });
        assert.equal(resCross.status, 403, `Expected 403 for ${ep.method} ${ep.path} with Sec-Fetch-Site: cross-site`);
        assert.equal(JSON.parse(resCross.body).error, 'Cross-site requests forbidden.');
      }
    });

    it('read-only routes: GET endpoints remain accessible regardless of Origin', async () => {
      const getEndpoints = ['/api/status', '/api/accounts', '/api/switching/status', '/api/config'];
      for (const ep of getEndpoints) {
        const res = await makeRequest(r04Port, ep, {
          method: 'GET',
          headers: {
            Origin: 'http://any-site.org'
          }
        });
        assert.equal(res.status, 200, `Expected 200 for GET ${ep}`);
      }
    });

    it('behavioral audit: boundary enforces BEFORE reading or parsing request body', async () => {
      const malformedPayload = '{"malformed_json: true, invalid...';

      // 1. Foreign origin + malformed body returns 403 (NOT 400 Bad Request)
      const resConfigOrigin = await makeRequest(r04Port, '/api/config', {
        method: 'POST',
        headers: {
          Origin: 'http://evil.com',
          'Content-Type': 'application/json'
        },
        body: malformedPayload
      });
      assert.equal(resConfigOrigin.status, 403, 'Must return 403 before reading malformed JSON body');
      assert.equal(JSON.parse(resConfigOrigin.body).error, 'Unauthorized origin.');

      // 2. Cross-site fetch context + malformed body returns 403 (NOT 400 Bad Request)
      const resConfigCross = await makeRequest(r04Port, '/api/config', {
        method: 'POST',
        headers: {
          'Sec-Fetch-Site': 'cross-site',
          'Content-Type': 'application/json'
        },
        body: malformedPayload
      });
      assert.equal(resConfigCross.status, 403, 'Must return 403 before reading malformed JSON body');
      assert.equal(JSON.parse(resConfigCross.body).error, 'Cross-site requests forbidden.');

      // 3. POST /api/accounts with malformed body returns 403
      const resAccountsOrigin = await makeRequest(r04Port, '/api/accounts', {
        method: 'POST',
        headers: {
          Origin: 'http://evil.com',
          'Content-Type': 'application/json'
        },
        body: malformedPayload
      });
      assert.equal(resAccountsOrigin.status, 403);
      assert.equal(JSON.parse(resAccountsOrigin.body).error, 'Unauthorized origin.');

      // 4. PATCH /api/accounts/:id with malformed body returns 403
      const resPatchOrigin = await makeRequest(r04Port, '/api/accounts/any_id', {
        method: 'PATCH',
        headers: {
          Origin: 'http://evil.com',
          'Content-Type': 'application/json'
        },
        body: malformedPayload
      });
      assert.equal(resPatchOrigin.status, 403);
      assert.equal(JSON.parse(resPatchOrigin.body).error, 'Unauthorized origin.');
    });
  });
});
