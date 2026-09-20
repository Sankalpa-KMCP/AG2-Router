/**
 * AG2 Router - Antigravity 2 Live Integration Adapter
 *
 * Implements the IAG2Adapter contract with live Windows-native process discovery,
 * Connect-RPC telemetry retrieval, and pure data normalization.
 *
 * STRICT SAFETY BOUNDARY:
 * - Read-only: Never mutates credentials, tokens, WinCred, or processes.
 * - switchAccount and verifyAccount throw explicit NotImplementedError.
 * - Zero third-party frameworks.
 */

import { AG2ProcessDetector } from './discovery.js';
import {
  normalizeAccountIdentity,
  normalizeActivitySnapshot,
  normalizeQuotaSnapshot
} from './normalizer.js';
import { AG2RpcClient } from './rpc-client.js';
import {
  ActivitySnapshot,
  AG2AccountIdentity,
  AG2DiscoveryResult,
  IAG2Adapter,
  NotImplementedError,
  QuotaSnapshot,
  SwitchRequest,
  SwitchResult
} from './types.js';

export interface LiveAdapterOptions {
  readonly detector?: AG2ProcessDetector;
  readonly rpcClient?: AG2RpcClient;
}

export class AG2LiveAdapter implements IAG2Adapter {
  private readonly detector: AG2ProcessDetector;
  private readonly rpcClient: AG2RpcClient;
  private lastTelemetryTimestamp: string | null = null;

  constructor(options: LiveAdapterOptions = {}) {
    this.detector = options.detector || new AG2ProcessDetector();
    this.rpcClient = options.rpcClient || new AG2RpcClient();
  }

  public getDetector(): AG2ProcessDetector {
    return this.detector;
  }

  public getLastTelemetryTimestamp(): string | null {
    return this.lastTelemetryTimestamp;
  }

  public async discover(): Promise<AG2DiscoveryResult> {
    return this.detector.discover();
  }

  private async ensureSession(): Promise<{
    pid: number;
    port: number;
    protocol: 'http' | 'https';
    csrfToken: string;
  } | null> {
    let session = this.detector.getCachedSession();
    if (!session) {
      const discovery = await this.detector.discover();
      if (!discovery.isRunning || !discovery.processInfo) {
        return null;
      }
      session = this.detector.getCachedSession();
    }
    return session;
  }

  public async getCurrentAccount(): Promise<AG2AccountIdentity | null> {
    const session = await this.ensureSession();
    if (!session) {
      return null;
    }

    try {
      const raw = await this.rpcClient.getUserStatus(
        session.port,
        session.protocol,
        session.csrfToken
      );
      this.detector.recordRpcSuccess();
      this.lastTelemetryTimestamp = new Date().toISOString();
      return normalizeAccountIdentity(raw);
    } catch (err) {
      this.detector.recordRpcFailure(err);
      return null;
    }
  }

  public async getQuota(): Promise<QuotaSnapshot | null> {
    const session = await this.ensureSession();
    if (!session) {
      return null;
    }

    try {
      const raw = await this.rpcClient.getUserStatus(
        session.port,
        session.protocol,
        session.csrfToken
      );
      this.detector.recordRpcSuccess();
      this.lastTelemetryTimestamp = new Date().toISOString();
      return normalizeQuotaSnapshot(raw);
    } catch (err) {
      this.detector.recordRpcFailure(err);
      return null;
    }
  }

  public async getActivityState(): Promise<ActivitySnapshot> {
    const session = await this.ensureSession();
    if (!session) {
      return {
        state: 'OFFLINE',
        totalTrajectories: 0,
        runningTrajectories: 0,
        timestamp: new Date().toISOString()
      };
    }

    try {
      const raw = await this.rpcClient.getAllCascadeTrajectories(
        session.port,
        session.protocol,
        session.csrfToken
      );
      this.detector.recordRpcSuccess();
      this.lastTelemetryTimestamp = new Date().toISOString();
      return normalizeActivitySnapshot(raw);
    } catch (err) {
      this.detector.recordRpcFailure(err);
      return {
        state: 'ERROR',
        totalTrajectories: 0,
        runningTrajectories: 0,
        timestamp: new Date().toISOString()
      };
    }
  }

  public async switchAccount(_request: SwitchRequest): Promise<SwitchResult> {
    throw new NotImplementedError('switchAccount');
  }

  public async verifyAccount(_expectedEmail: string): Promise<boolean> {
    throw new NotImplementedError('verifyAccount');
  }
}
