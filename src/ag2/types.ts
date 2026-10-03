/**
 * AG2 Router - Antigravity 2 Integration Types
 *
 * Defines the contract boundary between AG2 Router and Antigravity 2.
 * All version-sensitive, process-specific, or Connect-RPC details are
 * isolated within these types and the AG2 adapter.
 */

/**
 * Health and lifecycle status of discovered AG2 language server instances.
 */
export type AG2InstanceStatus =
  | 'OFFLINE'
  | 'DISCOVERED'
  | 'HEALTHY'
  | 'DEGRADED'
  | 'DISCONNECTED';

/**
 * Discovered local Antigravity 2 process parameters.
 */
export interface AG2ProcessInfo {
  readonly pid: number;
  readonly port: number;
  readonly protocol: 'http' | 'https';
  readonly csrfToken: string;
  readonly commandLine?: string;
  readonly binaryPath?: string;
  readonly discoveredAt: string;
}

/**
 * Result of attempting to discover the running Antigravity 2 daemon.
 */
export interface AG2DiscoveryResult {
  readonly isRunning: boolean;
  readonly status: AG2InstanceStatus;
  readonly processInfo: AG2ProcessInfo | null;
  readonly message?: string;
}

/**
 * Account identity returned by AG2 UserStatus RPC.
 */
export interface AG2AccountIdentity {
  readonly email: string;
  readonly name?: string;
  readonly tierId?: string;
  readonly tierName?: string;
  readonly rawStatusTimestamp?: string;
}

/**
 * Per-model quota allocation and exhaustion telemetry.
 */
export interface ModelQuotaInfo {
  readonly label: string;
  readonly modelOrTier?: string;
  /**
   * Remaining quota fraction between 0.0 (0%) and 1.0 (100%).
   */
  readonly remainingFraction: number | null;
  /**
   * ISO 8601 UTC timestamp string for when quota resets.
   */
  readonly resetTime?: string;
  readonly isExhausted: boolean;
}

/**
 * Deduplicated canonical model quota bucket grouping related execution/reasoning variants.
 */
export interface CanonicalModelQuotaInfo {
  readonly key: string;
  readonly label: string;
  readonly canonicalKey: string;
  readonly displayLabel: string;
  readonly modelOrTier?: string | null;
  readonly remainingFraction: number | null;
  readonly resetTime?: string | null;
  readonly isExhausted: boolean;
  readonly modes: readonly string[];
}

/**
 * Segregated prompt and flow credit telemetry.
 * Prompt and flow pools MUST NOT be combined or summed.
 */
export interface CreditPoolInfo {
  readonly availableCredits: number | null;
  readonly monthlyCredits: number | null;
  readonly usedCredits: number | null;
}

/**
 * Normalized quota snapshot from Antigravity 2.
 */
export interface QuotaSnapshot {
  readonly timestamp: string;
  readonly models: readonly ModelQuotaInfo[];
  readonly promptCredits?: CreditPoolInfo;
  readonly flowCredits?: CreditPoolInfo;
  readonly canonicalModels?: readonly CanonicalModelQuotaInfo[];
}

/**
 * Antigravity 2 cascade run / agent trajectory execution state.
 */
export type TrajectoryRunState = 'IDLE' | 'BUSY' | 'OFFLINE' | 'UNKNOWN' | 'ERROR';

/**
 * Live activity snapshot used by the idle safety gate.
 */
export interface ActivitySnapshot {
  readonly state: TrajectoryRunState;
  readonly totalTrajectories: number;
  readonly runningTrajectories: number;
  readonly timestamp: string;
}

/**
 * Account switch request payload.
 */
export interface SwitchRequest {
  readonly targetAccountId: string;
  readonly targetEmail: string;
  readonly dryRun?: boolean;
}

/**
 * Result of an account switch operation.
 */
export interface SwitchResult {
  readonly success: boolean;
  readonly previousAccountId: string | null;
  readonly currentAccountId: string | null;
  readonly error?: string;
  readonly timestamp: string;
}

/**
 * Antigravity 2 Adapter Contract Interface
 */
export interface IAG2Adapter {
  discover(): Promise<AG2DiscoveryResult>;
  getCurrentAccount(): Promise<AG2AccountIdentity | null>;
  getQuota(): Promise<QuotaSnapshot | null>;
  getActivityState(): Promise<ActivitySnapshot>;
  switchAccount(request: SwitchRequest): Promise<SwitchResult>;
  verifyAccount(expectedEmail: string): Promise<boolean>;
}

/**
 * Explicit error thrown when an AG2 operation is not yet implemented.
 */
export class NotImplementedError extends Error {
  public readonly code = 'NOT_IMPLEMENTED';

  constructor(operation: string) {
    super(`AG2 operation '${operation}' is not implemented in the current foundation stage.`);
    this.name = 'NotImplementedError';
  }
}
