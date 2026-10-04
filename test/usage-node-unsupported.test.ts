import { describe, it, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { AG2AdapterFoundation } from '../src/ag2/adapter.js';
import { InMemoryAccountStore } from '../src/accounts/account-store.js';
import { QuotaRouter } from '../src/router/router.js';
import { AppServer } from '../src/server/server.js';
import { DEFAULT_ROUTER_CONFIG } from '../src/router/types.js';
import type { AppConfig } from '../src/config/config.js';

describe('Usage capability difference on the Node reference server', () => {
  let server: AppServer;
  let baseUrl: string;

  before(async () => {
    const config: AppConfig = {
      host: '127.0.0.1',
      port: 0,
      storageDir: 'unused',
      uiDir: 'unused',
      router: { ...DEFAULT_ROUTER_CONFIG },
      isDev: true,
    };
    server = new AppServer(
      config,
      new InMemoryAccountStore(),
      new AG2AdapterFoundation(),
      new QuotaRouter(new InMemoryAccountStore(), new AG2AdapterFoundation(), config.router),
    );
    const bound = await server.start();
    baseUrl = `http://${bound.host}:${bound.port}`;
  });

  after(async () => {
    await server.stop();
  });

  it('responds 501 for every usage route instead of synthesizing usage', async () => {
    for (const route of ['/api/usage/summary?scope=all', '/api/usage/timeseries?range=7d', '/api/usage/models']) {
      const response = await fetch(`${baseUrl}${route}`);
      assert.equal(response.status, 501);
      const body = (await response.json()) as { error: string };
      assert.match(body.error, /not supported/i);
    }
  });
});
