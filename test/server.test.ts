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

    // 4. Delete account
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
});
