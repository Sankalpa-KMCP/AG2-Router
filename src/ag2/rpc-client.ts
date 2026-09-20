/**
 * AG2 Router - Connect-RPC Client for Antigravity 2
 *
 * Implements lightweight, native Node HTTP/HTTPS requests to the local
 * language server Connect-RPC service without external dependencies.
 *
 * SECURITY:
 * - Loopback only (127.0.0.1).
 * - TLS certificate relaxation (rejectUnauthorized: false) is strictly scoped
 *   to individual loopback HTTPS requests; never globally set in process.env.
 * - All error messages and logs pass through secret-sanitizing redactors.
 */

import * as http from 'node:http';
import * as https from 'node:https';
import { RawTrajectoriesResponse, RawUserStatusResponse } from './normalizer.js';
import { redactSensitiveText, sanitizeError } from './security.js';

export interface RpcCallOptions {
  readonly port: number;
  readonly protocol: 'http' | 'https';
  readonly csrfToken: string;
  readonly endpoint: string;
  readonly body?: Record<string, unknown>;
  readonly timeoutMs?: number;
}

export class AG2RpcClient {
  private readonly defaultTimeoutMs: number;

  constructor(defaultTimeoutMs = 5000) {
    this.defaultTimeoutMs = defaultTimeoutMs;
  }

  /**
   * Execute a Connect-RPC POST request against the local Antigravity 2 daemon.
   */
  public async callRpc<T>(options: RpcCallOptions): Promise<T> {
    const {
      port,
      protocol,
      csrfToken,
      endpoint,
      body = { metadata: { ideName: 'antigravity', extensionName: 'antigravity' } },
      timeoutMs = this.defaultTimeoutMs
    } = options;

    return new Promise<T>((resolve, reject) => {
      const postData = JSON.stringify(body);
      const transport = protocol === 'https' ? https : http;

      const requestOptions: https.RequestOptions = {
        hostname: '127.0.0.1',
        port,
        path: `/exa.language_server_pb.LanguageServerService/${endpoint}`,
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Connect-Protocol-Version': '1',
          'x-codeium-csrf-token': csrfToken,
          'Content-Length': Buffer.byteLength(postData)
        },
        timeout: timeoutMs,
        // Narrowly scoped to this loopback request for local self-signed TLS:
        rejectUnauthorized: false
      };

      const req = transport.request(requestOptions, (res) => {
        let responseBody = '';

        res.setEncoding('utf8');
        res.on('data', (chunk) => {
          responseBody += chunk;
        });

        res.on('end', () => {
          const statusCode = res.statusCode ?? 0;
          if (statusCode < 200 || statusCode >= 300) {
            const snippet = redactSensitiveText(responseBody.slice(0, 200));
            reject(
              new Error(
                `Connect-RPC endpoint '${endpoint}' failed with HTTP ${statusCode}: ${snippet}`
              )
            );
            return;
          }

          try {
            const parsed = JSON.parse(responseBody) as T;
            resolve(parsed);
          } catch (jsonErr) {
            reject(
              new Error(
                `Failed to parse JSON response from '${endpoint}': ${(jsonErr as Error).message}`
              )
            );
          }
        });
      });

      req.on('timeout', () => {
        req.destroy();
        reject(new Error(`Connect-RPC timeout (${timeoutMs}ms) calling '${endpoint}'`));
      });

      req.on('error', (err) => {
        reject(sanitizeError(err));
      });

      req.write(postData);
      req.end();
    });
  }

  /**
   * Fetch current user identity, plan status, and model quotas.
   */
  public async getUserStatus(
    port: number,
    protocol: 'http' | 'https',
    csrfToken: string,
    timeoutMs?: number
  ): Promise<RawUserStatusResponse> {
    return this.callRpc<RawUserStatusResponse>({
      port,
      protocol,
      csrfToken,
      endpoint: 'GetUserStatus',
      timeoutMs
    });
  }

  /**
   * Fetch cascade run / agent trajectories to evaluate running tasks.
   */
  public async getAllCascadeTrajectories(
    port: number,
    protocol: 'http' | 'https',
    csrfToken: string,
    timeoutMs?: number
  ): Promise<RawTrajectoriesResponse> {
    return this.callRpc<RawTrajectoriesResponse>({
      port,
      protocol,
      csrfToken,
      endpoint: 'GetAllCascadeTrajectories',
      timeoutMs
    });
  }

  /**
   * Probe a port candidate to determine if it responds to Connect-RPC requests.
   * Any HTTP status < 500 (including 200 OK or 4xx client errors) validates an active server.
   */
  public async probePort(
    port: number,
    protocol: 'http' | 'https',
    csrfToken: string,
    timeoutMs = 3000
  ): Promise<boolean> {
    try {
      await this.callRpc({
        port,
        protocol,
        csrfToken,
        endpoint: 'GetUserStatus',
        timeoutMs
      });
      return true;
    } catch (err) {
      const msg = err instanceof Error ? err.message : '';
      // Status code < 500 (e.g. 400 or 404 or 401) proves an active HTTP server on this port
      if (msg.includes('HTTP 4') || msg.includes('HTTP 2')) {
        return true;
      }
      return false;
    }
  }
}
