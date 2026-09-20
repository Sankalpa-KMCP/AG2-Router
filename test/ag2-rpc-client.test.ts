/**
 * Test: AG2 Connect-RPC Client
 */

import { describe, it, before, after } from 'node:test';
import * as assert from 'node:assert/strict';
import * as http from 'node:http';
import { AG2RpcClient } from '../src/ag2/rpc-client.js';

describe('AG2RpcClient', () => {
  let mockServer: http.Server;
  let serverPort = 0;
  let lastHeaders: http.IncomingHttpHeaders = {};
  let lastPath = '';
  let mockStatusCode = 200;
  let mockResponseBody = '{}';
  let responseDelayMs = 0;

  before(async () => {
    mockServer = http.createServer(async (req, res) => {
      lastHeaders = req.headers;
      lastPath = req.url || '';

      if (responseDelayMs > 0) {
        await new Promise((r) => setTimeout(r, responseDelayMs));
      }

      res.writeHead(mockStatusCode, { 'Content-Type': 'application/json' });
      res.end(mockResponseBody);
    });

    await new Promise<void>((resolve) => {
      mockServer.listen(0, '127.0.0.1', () => {
        const addr = mockServer.address();
        if (typeof addr === 'object' && addr) {
          serverPort = addr.port;
        }
        resolve();
      });
    });
  });

  after(async () => {
    await new Promise<void>((resolve) => {
      mockServer.close(() => resolve());
    });
  });

  it('should send Connect-RPC POST request with expected headers and return parsed JSON', async () => {
    mockStatusCode = 200;
    responseDelayMs = 0;
    mockResponseBody = JSON.stringify({
      userStatus: {
        email: 'test@example.com'
      }
    });

    const client = new AG2RpcClient(3000);
    const result = await client.getUserStatus(serverPort, 'http', 'test-csrf-token-12345');

    assert.equal(lastPath, '/exa.language_server_pb.LanguageServerService/GetUserStatus');
    assert.equal(lastHeaders['connect-protocol-version'], '1');
    assert.equal(lastHeaders['x-codeium-csrf-token'], 'test-csrf-token-12345');
    assert.equal(result.userStatus?.email, 'test@example.com');
  });

  it('should reject with sanitized error when server returns non-2xx status', async () => {
    mockStatusCode = 500;
    responseDelayMs = 0;
    mockResponseBody = JSON.stringify({ error: 'internal error with --csrf_token secret-token-xyz' });

    const client = new AG2RpcClient(3000);
    await assert.rejects(
      async () => {
        await client.getUserStatus(serverPort, 'http', 'secret-token-xyz');
      },
      (err: unknown) => {
        assert.ok(err instanceof Error);
        assert.match(err.message, /failed with HTTP 500/i);
        // Invariant: raw token must be redacted from error message
        assert.doesNotMatch(err.message, /secret-token-xyz/);
        assert.match(err.message, /--csrf_token \[REDACTED\]/);
        return true;
      }
    );
  });

  it('should timeout when request exceeds configured timeoutMs', async () => {
    mockStatusCode = 200;
    responseDelayMs = 300;
    mockResponseBody = JSON.stringify({});

    const client = new AG2RpcClient(100); // 100ms timeout
    await assert.rejects(
      async () => {
        await client.getUserStatus(serverPort, 'http', 'token', 100);
      },
      (err: unknown) => {
        assert.ok(err instanceof Error);
        assert.match(err.message, /timeout/i);
        return true;
      }
    );
    responseDelayMs = 0;
  });

  it('should validate probePort behavior on active vs dead ports', async () => {
    mockStatusCode = 200;
    responseDelayMs = 0;
    mockResponseBody = JSON.stringify({ userStatus: {} });

    const client = new AG2RpcClient(1000);

    // Active port responding 200
    const activeProbe = await client.probePort(serverPort, 'http', 'token');
    assert.equal(activeProbe, true);

    // Active port responding 400 (proves an active Connect-RPC HTTP server)
    mockStatusCode = 400;
    const clientErrorProbe = await client.probePort(serverPort, 'http', 'token');
    assert.equal(clientErrorProbe, true);

    // Dead port (no server listening)
    const deadProbe = await client.probePort(59999, 'http', 'token', 500);
    assert.equal(deadProbe, false);
  });
});
