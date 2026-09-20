/**
 * AG2 Router - Antigravity 2 Adapter Contract & Foundation Implementation
 *
 * All Antigravity 2 interaction must strictly pass through this adapter.
 * The core router, account store, and server never invoke Connect-RPC,
 * inspect Windows processes, or modify WinCred credentials directly.
 */

import {
  AG2AccountIdentity,
  AG2DiscoveryResult,
  ActivitySnapshot,
  IAG2Adapter,
  NotImplementedError,
  QuotaSnapshot,
  SwitchRequest,
  SwitchResult
} from './types.js';
import { AG2LiveAdapter } from './live-adapter.js';

export { IAG2Adapter, AG2LiveAdapter };

/**
 * Foundation implementation of the AG2 adapter.
 *
 * This implementation enforces the contract boundary during early project stages.
 * Unsupported mutation and live switching operations throw explicit NotImplementedError
 * rather than faking success.
 */
export class AG2AdapterFoundation implements IAG2Adapter {
  public async discover(): Promise<AG2DiscoveryResult> {
    return {
      isRunning: false,
      status: 'OFFLINE',
      processInfo: null,
      message: 'AG2 live process discovery is reserved for the live integration stage.'
    };
  }

  public async getCurrentAccount(): Promise<AG2AccountIdentity | null> {
    return null;
  }

  public async getQuota(): Promise<QuotaSnapshot | null> {
    return null;
  }

  public async getActivityState(): Promise<ActivitySnapshot> {
    return {
      state: 'UNKNOWN',
      totalTrajectories: 0,
      runningTrajectories: 0,
      timestamp: new Date().toISOString()
    };
  }

  public async switchAccount(_request: SwitchRequest): Promise<SwitchResult> {
    throw new NotImplementedError('switchAccount');
  }

  public async verifyAccount(_expectedEmail: string): Promise<boolean> {
    throw new NotImplementedError('verifyAccount');
  }
}
