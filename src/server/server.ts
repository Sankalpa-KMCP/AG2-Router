import { validateAccountText } from '../accounts/text-validation.js';
/**
 * AG2 Router - Lightweight Loopback HTTP Server
 *
 * Exposes a minimal REST API and serves the dashboard static assets.
 *
 * SECURITY:
 * - Strictly binds to loopback (127.0.0.1).
 * - Enforces remote address validation rejecting non-loopback clients.
 * - Enforces strict path traversal prevention on static file requests.
 * - Sets nosniff, DENY frame options, and no-store cache headers.
 * - Zero third-party web frameworks (native node:http).
 */

import * as http from 'node:http';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { IAccountStore } from '../accounts/types.js';
import { AccountEnrollmentService } from '../accounts/enrollment.js';
import { IAG2Adapter } from '../ag2/adapter.js';
import { QuotaSnapshot } from '../ag2/types.js';
import { AppConfig } from '../config/config.js';
import { QuotaRouter } from '../router/router.js';
import { SessionVault } from '../vault/session-vault.js';
import { SwitchPlanner } from '../switching/planner.js';
import { SwitchTransactionCoordinator } from '../switching/transaction.js';
import type { SwitchStatusResponse } from '../switching/types.js';

const MIME_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon'
};

export class AppServer {
  private server: http.Server | null = null;
  private config: AppConfig;
  private accountStore: IAccountStore;
  private adapter: IAG2Adapter;
  private router: QuotaRouter;
  private enrollmentService?: AccountEnrollmentService;
  private sessionVault?: SessionVault;
  private switchPlanner?: SwitchPlanner;
  private switchCoordinator?: SwitchTransactionCoordinator;

  constructor(
    config: AppConfig,
    accountStore: IAccountStore,
    adapter: IAG2Adapter,
    router: QuotaRouter,
    enrollmentService?: AccountEnrollmentService,
    sessionVault?: SessionVault,
    switchPlanner?: SwitchPlanner,
    switchCoordinator?: SwitchTransactionCoordinator
  ) {
    this.config = config;
    this.accountStore = accountStore;
    this.adapter = adapter;
    this.router = router;
    this.enrollmentService = enrollmentService;
    this.sessionVault = sessionVault;
    this.switchPlanner = switchPlanner;
    this.switchCoordinator = switchCoordinator;
  }

  public async start(): Promise<{ host: string; port: number }> {
    return new Promise((resolve, reject) => {
      this.server = http.createServer(async (req, res) => {
        try {
          await this.handleRequest(req, res);
        } catch (err) {
          const message = err instanceof Error ? err.message : 'Internal Server Error';
          this.sendJson(res, 500, { error: message });
        }
      });

      this.server.on('error', (err) => {
        reject(err);
      });

      this.server.listen(this.config.port, this.config.host, () => {
        const address = this.server?.address();
        const port = typeof address === 'object' && address !== null ? address.port : this.config.port;
        resolve({ host: this.config.host, port });
      });
    });
  }

  public async stop(): Promise<void> {
    return new Promise((resolve) => {
      if (this.server) {
        this.server.close(() => {
          this.server = null;
          resolve();
        });
      } else {
        resolve();
      }
    });
  }

  private isLoopback(remoteAddress?: string): boolean {
    if (!remoteAddress) return false;
    return (
      remoteAddress === '127.0.0.1' ||
      remoteAddress === '::1' ||
      remoteAddress === '::ffff:127.0.0.1'
    );
  }

  private isAllowedLoopbackHost(host?: string): boolean {
    if (!host) return false;
    const cleanHost = host.split(':')[0].toLowerCase();
    return cleanHost === '127.0.0.1' || cleanHost === 'localhost';
  }

  /**
   * Validates request Origin for state-mutating HTTP methods (R04).
   *
   * SECURITY INVARIANTS:
   * - Missing Origin (undefined): Allowed to support native clients (curl, WPF WebView2 host IPC).
   * - Non-loopback Origin: Rejected with 403 to prevent Cross-Site Request Forgery (CSRF).
   *   Simple cross-origin POST/PUT/PATCH/DELETE requests from foreign browser contexts cannot bypass this.
   * - origin === 'null': Explicitly rejected (opaque origin).
   * - Port & scheme: Must strictly match loopback http://127.0.0.1:<port> or http://localhost:<port>.
   */
  private isAllowedMutationOrigin(req: http.IncomingMessage): boolean {
    const origin = req.headers.origin;
    if (origin === undefined) return true;
    if (typeof origin !== 'string' || origin.trim() === '') return false;
    try {
      const url = new URL(origin);
      if (url.protocol !== 'http:') return false;
      if (!this.isAllowedLoopbackHost(url.hostname)) return false;
      const port = url.port ? parseInt(url.port, 10) : 80;
      return port === this.config.port;
    } catch {
      return false;
    }
  }

  private applySecurityHeaders(res: http.ServerResponse): void {
    res.setHeader('X-Content-Type-Options', 'nosniff');
    res.setHeader('X-Frame-Options', 'DENY');
    res.setHeader('Cache-Control', 'no-store, max-age=0');
    res.setHeader(
      'Content-Security-Policy',
      "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'"
    );
  }

  private sendJson(res: http.ServerResponse, statusCode: number, data: unknown): void {
    this.applySecurityHeaders(res);
    res.setHeader('Content-Type', 'application/json; charset=utf-8');
    res.writeHead(statusCode);
    res.end(JSON.stringify(data));
  }

  private async readBodyJson(req: http.IncomingMessage): Promise<unknown> {
    return new Promise((resolve, reject) => {
      let body = '';
      req.on('data', (chunk) => {
        body += chunk;
        if (body.length > 1024 * 64) {
          // 64 KB limit
          reject(new Error('Payload too large'));
        }
      });
      req.on('end', () => {
        if (!body.trim()) {
          resolve({});
          return;
        }
        try {
          resolve(JSON.parse(body));
        } catch {
          reject(new Error('Invalid JSON payload'));
        }
      });
      req.on('error', (err) => reject(err));
    });
  }

  /**
   * Projects quota snapshots into the wire contract format (R09).
   *
   * DUAL-PROPERTY WIRE ALIGNMENT:
   * Emits both `canonicalKey`/`displayLabel` AND `key`/`label` in canonicalModels.
   * This bridges frontend consumers (summarizeModelFamilies) and legacy consumers without undefined headings,
   * keeping the Node oracle and .NET NativeLoopbackServer DTOs completely aligned.
   */
  private projectQuotaSnapshot(quota: QuotaSnapshot | null): QuotaSnapshot | null {
    if (!quota) return null;
    return {
      timestamp: quota.timestamp,
      models: quota.models,
      promptCredits: quota.promptCredits,
      flowCredits: quota.flowCredits,
      canonicalModels: quota.canonicalModels
        ? quota.canonicalModels.map((m) => {
            const key = m.key || (m as { canonicalKey?: string }).canonicalKey || 'unknown';
            const label = m.label || (m as { displayLabel?: string }).displayLabel || 'Unknown Model';
            const canonicalKey = (m as { canonicalKey?: string }).canonicalKey || key;
            const displayLabel = (m as { displayLabel?: string }).displayLabel || label;
            return {
              key,
              label,
              canonicalKey,
              displayLabel,
              modelOrTier: m.modelOrTier ?? null,
              remainingFraction: m.remainingFraction,
              resetTime: m.resetTime ?? null,
              isExhausted: m.isExhausted,
              modes: m.modes || []
            };
          })
        : undefined
    };
  }

  private async handleRequest(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    // Loopback isolation verification
    if (!this.isLoopback(req.socket.remoteAddress)) {
      res.writeHead(403, { 'Content-Type': 'text/plain' });
      res.end('Forbidden: loopback access only.');
      return;
    }

    if (!this.isAllowedLoopbackHost(req.headers.host)) {
      res.writeHead(403, { 'Content-Type': 'text/plain' });
      res.end('Forbidden: Invalid loopback host.');
      return;
    }

    // Check for directory traversal in raw request URL
    const rawUrl = req.url || '/';
    let decodedRaw: string;
    try {
      decodedRaw = decodeURIComponent(rawUrl);
    } catch {
      decodedRaw = rawUrl;
    }

    if (
      rawUrl.includes('..') ||
      rawUrl.toLowerCase().includes('%2e%2e') ||
      decodedRaw.includes('..') ||
      rawUrl.includes('\\')
    ) {
      res.writeHead(403, { 'Content-Type': 'text/plain' });
      res.end('Forbidden: path traversal detected.');
      return;
    }

    const parsedUrl = new URL(rawUrl, `http://${this.config.host}:${this.config.port}`);
    const pathname = parsedUrl.pathname;
    const method = req.method?.toUpperCase() || 'GET';

    // API Routing
    if (pathname === '/api' || pathname.startsWith('/api/')) {
      await this.handleApiRequest(pathname, method, req, res);
      return;
    }

    // Static Dashboard Asset Serving
    if (method === 'GET' || method === 'HEAD') {
      await this.handleStaticAsset(pathname, res);
      return;
    }

    this.sendJson(res, 405, { error: 'Method Not Allowed' });
  }

  private async handleApiRequest(
    pathname: string,
    method: string,
    req: http.IncomingMessage,
    res: http.ServerResponse
  ): Promise<void> {
    const normalizedMethod = (method || req.method || 'GET').toUpperCase();
    const isMutationMethod =
      normalizedMethod === 'POST' ||
      normalizedMethod === 'PATCH' ||
      normalizedMethod === 'PUT' ||
      normalizedMethod === 'DELETE';

    // Central Mutation Security Boundary (R04):
    // Enforces Origin and Sec-Fetch-Site validation strictly BEFORE reading the request body.
    // Protects against cross-origin browser POST/PATCH/DELETE mutations (CSRF) where simple requests
    // would otherwise deliver mutations despite CORS response blocking.
    if (isMutationMethod) {
      if (!this.isAllowedMutationOrigin(req)) {
        req.resume();
        this.sendJson(res, 403, { error: 'Unauthorized origin.' });
        return;
      }
      const secFetchSite = req.headers['sec-fetch-site'];
      const secFetchSiteVal = Array.isArray(secFetchSite) ? secFetchSite[0] : secFetchSite;
      if (typeof secFetchSiteVal === 'string' && secFetchSiteVal.toLowerCase() === 'cross-site') {
        req.resume();
        this.sendJson(res, 403, { error: 'Cross-site requests forbidden.' });
        return;
      }
    }

    // GET /api/status
    if (pathname === '/api/status' && normalizedMethod === 'GET') {
      const discovery = await this.adapter.discover();
      const currentAccount = await this.adapter.getCurrentAccount();
      const quota = await this.adapter.getQuota();
      const activity = await this.adapter.getActivityState();
      const routerSnapshot = await this.router.getStatusSnapshot();

      const lastSuccessfulTelemetry =
        'getLastTelemetryTimestamp' in this.adapter &&
        typeof (this.adapter as { getLastTelemetryTimestamp?: () => string | null }).getLastTelemetryTimestamp === 'function'
          ? (this.adapter as { getLastTelemetryTimestamp: () => string | null }).getLastTelemetryTimestamp()
          : (quota?.timestamp || null);

      this.sendJson(res, 200, {
        status: 'ok',
        ag2: {
          connected: discovery.isRunning,
          status: discovery.status,
          activity: {
            state: activity.state,
            totalTrajectories: activity.totalTrajectories,
            runningTrajectories: activity.runningTrajectories,
            timestamp: activity.timestamp
          },
          message: discovery.message || (discovery.isRunning ? 'Antigravity 2 connected' : 'Waiting for Antigravity 2')
        },
        router: routerSnapshot,
        telemetry: {
          currentAccount,
          quota: this.projectQuotaSnapshot(quota),
          activity,
          totalAvailableQuotaPercent: null, // Truthful: separate model quotas are preserved; no unverified aggregate is fabricated
          lastSuccessfulTelemetry
        }
      });
      return;
    }

    // POST /api/accounts/enroll-current
    if (pathname === '/api/accounts/enroll-current' && normalizedMethod === 'POST') {
      if (!this.enrollmentService) {
        this.sendJson(res, 501, { error: 'Account enrollment service is not configured' });
        return;
      }
      try {
        const body = ((await this.readBodyJson(req)) || {}) as Record<string, unknown>;
        const options = {
          name: typeof body.name === 'string' ? body.name : undefined,
          priority: typeof body.priority === 'number' ? body.priority : undefined,
          isReserve: typeof body.isReserve === 'boolean' ? body.isReserve : undefined,
          notes: typeof body.notes === 'string' ? body.notes : undefined
        };

        validateAccountText(options);
        const result = await this.enrollmentService.enrollCurrentAccount(options);
        this.sendJson(res, 200, {
          success: true,
          account: result.account,
          isNew: result.isNew,
          message: result.message
        });
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Enrollment failed';
        this.sendJson(res, 400, { error: message });
      }
      return;
    }

    // GET /api/accounts
    if (pathname === '/api/accounts' && normalizedMethod === 'GET') {
      const accounts = await this.accountStore.listAccounts();
      const activeId = await this.accountStore.getActiveAccountId();
      const enriched = await Promise.all(
        accounts.map(async (acc) => {
          let hasVaulted = acc.hasVaultedSession;
          if (hasVaulted === undefined && this.sessionVault) {
            hasVaulted = await this.sessionVault.hasSession(acc.id);
          }
          return {
            ...acc,
            hasVaultedSession: Boolean(hasVaulted),
            isActive: acc.id === activeId
          };
        })
      );
      this.sendJson(res, 200, {
        accounts: enriched,
        totalCount: enriched.length,
        activeAccountId: activeId || null
      });
      return;
    }

    // POST /api/accounts
    if (pathname === '/api/accounts' && normalizedMethod === 'POST') {
      try {
        const body = (await this.readBodyJson(req)) as Record<string, unknown>;
        if (!body || typeof body.email !== 'string' || !body.email.includes('@')) {
          this.sendJson(res, 400, { error: 'A valid email address is required.' });
          return;
        }

        validateAccountText({ name: typeof body.name === 'string' ? body.name : undefined,
          notes: typeof body.notes === 'string' ? body.notes : undefined });
        const created = await this.accountStore.addAccount({
          email: body.email,
          name: typeof body.name === 'string' ? body.name : undefined,
          priority: typeof body.priority === 'number' ? body.priority : undefined,
          isReserve: Boolean(body.isReserve),
          hasVaultedSession: Boolean(body.hasVaultedSession),
          notes: typeof body.notes === 'string' ? body.notes : undefined
        });

        this.sendJson(res, 201, { account: created });
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Failed to create account';
        this.sendJson(res, 400, { error: message });
      }
      return;
    }

    // PATCH /api/accounts/:id - Update or clear account alias
    if (pathname.startsWith('/api/accounts/') && normalizedMethod === 'PATCH') {
      const id = pathname.slice('/api/accounts/'.length).trim();
      if (!id || id.includes('/') || id.includes('\\')) {
        this.sendJson(res, 400, { error: 'Account ID required' });
        return;
      }

      const existing = await this.accountStore.getAccount(id);
      if (!existing) {
        this.sendJson(res, 404, { error: 'Account not found' });
        return;
      }

      let body: Record<string, unknown>;
      try {
        body = (await this.readBodyJson(req)) as Record<string, unknown>;
      } catch {
        this.sendJson(res, 400, { error: 'Invalid JSON payload' });
        return;
      }

      if (!body || typeof body !== 'object' || Array.isArray(body)) {
        this.sendJson(res, 400, { error: 'Invalid JSON payload' });
        return;
      }

      if (body.alias !== undefined && body.alias !== null && typeof body.alias !== 'string') {
        this.sendJson(res, 400, { error: 'Invalid alias: must be a string or null' });
        return;
      }

      try { validateAccountText({ alias: typeof body.alias === 'string' ? body.alias : undefined }); }
      catch (err) {
        this.sendJson(res, 400, { error: (err as Error).message });
        return;
      }
      const rawAlias = typeof body.alias === 'string' ? body.alias.trim() : '';
      const updated = await this.accountStore.updateAccount(id, { alias: rawAlias });
      if (!updated) {
        this.sendJson(res, 404, { error: 'Account not found' });
        return;
      }

      let hasVaulted = updated.hasVaultedSession;
      if (hasVaulted === undefined && this.sessionVault) {
        hasVaulted = await this.sessionVault.hasSession(updated.id);
      }
      const activeId = await this.accountStore.getActiveAccountId();
      const enriched = {
        ...updated,
        hasVaultedSession: Boolean(hasVaulted),
        isActive: updated.id === activeId
      };

      this.sendJson(res, 200, { success: true, account: enriched });
      return;
    }

    // GET /api/switching/status
    if (pathname === '/api/switching/status' && normalizedMethod === 'GET') {
      const status: SwitchStatusResponse = this.switchCoordinator
        ? this.switchCoordinator.getStatus()
        : { activeTransactionId: null, currentState: 'IDLE', lastResult: null,
            quarantineActive: false, journalRecoveryState: 'NONE' };
      this.sendJson(res, 200, { status });
      return;
    }

    // POST /api/accounts/:id/switch-plan (Dry-Run / Readiness Evaluation)
    if (pathname.startsWith('/api/accounts/') && pathname.endsWith('/switch-plan') && normalizedMethod === 'POST') {
      const id = pathname.slice('/api/accounts/'.length, -'/switch-plan'.length).trim();
      if (!id) {
        this.sendJson(res, 400, { error: 'Account ID is required' });
        return;
      }
      if (!this.switchPlanner) {
        this.sendJson(res, 501, { error: 'Switch planner service is not configured' });
        return;
      }
      try {
        const plan = await this.switchPlanner.planSwitch(id);
        this.sendJson(res, 200, { plan });
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Failed to generate switch plan';
        this.sendJson(res, 500, { error: message });
      }
      return;
    }

    // POST /api/accounts/:id/switch (Hard Boundary: Live mutation prohibited in this phase)
    if (pathname.startsWith('/api/accounts/') && pathname.endsWith('/switch') && normalizedMethod === 'POST') {
      this.sendJson(res, 403, {
        error: 'Live account switching execution is not authorized in this runtime mode. Use switch-plan for dry-run evaluation.'
      });
      return;
    }

    // DELETE /api/accounts/:id
    if (pathname.startsWith('/api/accounts/') && normalizedMethod === 'DELETE') {
      const id = pathname.slice('/api/accounts/'.length).trim();
      if (!id || id.includes('/') || id.includes('\\')) {
        this.sendJson(res, 400, { error: 'Account ID required' });
        return;
      }
      const removed = await this.accountStore.removeAccount(id);
      if (!removed) {
        this.sendJson(res, 404, { error: 'Account not found' });
        return;
      }
      let vaultRecordDeleted = false;
      if (this.sessionVault) {
        vaultRecordDeleted = await this.sessionVault.removeSession(id);
      }
      this.sendJson(res, 200, { success: true, removedId: id, vaultRecordDeleted });
      return;
    }

    // GET /api/usage/* - Explicit capability difference (not implemented on the Node oracle).
    // The reference server must never synthesize usage data that could look real.
    if (pathname.startsWith('/api/usage')) {
      this.sendJson(res, 501, { error: 'Usage accounting is not supported by the Node reference server.' });
      return;
    }

    // GET /api/config
    if (pathname === '/api/config' && normalizedMethod === 'GET') {
      this.sendJson(res, 200, { config: this.router.getConfig() });
      return;
    }

    // POST /api/config
    if (pathname === '/api/config' && normalizedMethod === 'POST') {
      try {
        const raw = await this.readBodyJson(req);
        if (!raw || typeof raw !== 'object' || Array.isArray(raw)) {
          throw new Error('Invalid configuration payload');
        }
        const body = raw as Record<string, unknown>;
        const updates: Record<string, unknown> = {};

        if (typeof body.autoSwitchEnabled === 'boolean') {
          updates.autoSwitchEnabled = body.autoSwitchEnabled;
        }
        if (typeof body.lowQuotaThresholdPercent === 'number') {
          updates.lowQuotaThresholdPercent = body.lowQuotaThresholdPercent;
        }
        if (typeof body.minimumCandidateQuotaPercent === 'number') {
          updates.minimumCandidateQuotaPercent = body.minimumCandidateQuotaPercent;
        }
        if (typeof body.pollingIntervalMs === 'number') {
          updates.pollingIntervalMs = body.pollingIntervalMs;
        }
        if (body.workloadModelKey !== undefined) {
          updates.workloadModelKey = body.workloadModelKey as string | null;
        }

        const updated = this.router.updateConfig(updates);
        this.sendJson(res, 200, { config: updated });
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Invalid configuration payload';
        this.sendJson(res, 400, { error: message });
      }
      return;
    }

    this.sendJson(res, 404, { error: `Endpoint '${pathname}' not found.` });
  }

  private async handleStaticAsset(pathname: string, res: http.ServerResponse): Promise<void> {
    let decodedPath: string;
    try {
      decodedPath = decodeURIComponent(pathname);
    } catch {
      res.writeHead(400, { 'Content-Type': 'text/plain' });
      res.end('Bad Request: Invalid URI encoding');
      return;
    }

    if (decodedPath.includes('..') || decodedPath.includes('\\')) {
      res.writeHead(403, { 'Content-Type': 'text/plain' });
      res.end('Forbidden: path traversal detected.');
      return;
    }

    let sanitizedPath = decodedPath;
    if (sanitizedPath === '/' || sanitizedPath === '') {
      sanitizedPath = '/index.html';
    }

    // Normalize and prevent path traversal
    const normalizedUiDir = path.normalize(this.config.uiDir);
    const targetFile = path.normalize(path.join(normalizedUiDir, sanitizedPath));

    if (!targetFile.startsWith(normalizedUiDir)) {
      res.writeHead(403, { 'Content-Type': 'text/plain' });
      res.end('Forbidden: path traversal detected.');
      return;
    }

    try {
      const stats = await fs.stat(targetFile);
      if (stats.isDirectory()) {
        res.writeHead(403, { 'Content-Type': 'text/plain' });
        res.end('Forbidden: directory listing prohibited.');
        return;
      }

      const ext = path.extname(targetFile).toLowerCase();
      const contentType = MIME_TYPES[ext] || 'application/octet-stream';
      const content = await fs.readFile(targetFile);

      this.applySecurityHeaders(res);
      res.setHeader('Content-Type', contentType);
      res.setHeader('Content-Length', content.byteLength);
      res.writeHead(200);
      res.end(content);
    } catch (err: unknown) {
      const nodeErr = err as NodeJS.ErrnoException;
      if (nodeErr.code === 'ENOENT') {
        res.writeHead(404, { 'Content-Type': 'text/plain' });
        res.end('Not Found');
        return;
      }
      res.writeHead(500, { 'Content-Type': 'text/plain' });
      res.end('Internal Error Reading Asset');
    }
  }
}
