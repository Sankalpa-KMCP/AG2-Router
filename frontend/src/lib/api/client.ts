import type {
  AccountsListResponse,
  AccountMetadata,
  SystemStatusDto,
  SwitchStatusDto,
  SwitchResultDto,
  SwitchPlanResultDto,
  RouterConfigDto,
  RouterConfigUpdate,
  CandidateEvidenceStatusDto,
  JournalResolutionResult,
  JournalResolutionStatus,
  UsageSummaryResponse,
  UsageTimeSeriesResponse,
  UsageModelBreakdownResponse,
  UsageScopeQuery,
  UsageRange
} from './types.js';
import { USAGE_RANGES } from './types.js';

/** Raised when the connected backend does not implement usage accounting (Node reference server). */
export class UsageUnavailableError extends Error {
  constructor(public readonly httpStatus: number) {
    super('Usage accounting is not available on this backend.');
    this.name = 'UsageUnavailableError';
  }
}

export class SwitchRequestError extends Error {
  constructor(public readonly result: SwitchResultDto, public readonly httpStatus: number) {
    super(result.message);
    this.name = 'SwitchRequestError';
  }

  get code(): string { return this.result.code; }
  get manualRecoveryRequired(): boolean | undefined { return this.result.manualRecoveryRequired; }
}

function parseSwitchResult(data: unknown): SwitchResultDto | null {
  if (!data || typeof data !== 'object') return null;
  const raw = data as Record<string, unknown>;
  if (typeof raw.success !== 'boolean' || typeof raw.code !== 'string' || !raw.code.trim() ||
      typeof raw.message !== 'string' ||
      (raw.manualRecoveryRequired !== undefined && typeof raw.manualRecoveryRequired !== 'boolean')) return null;
  return {
    success: raw.success,
    code: raw.code,
    message: raw.message,
    ...(typeof raw.manualRecoveryRequired === 'boolean' ? { manualRecoveryRequired: raw.manualRecoveryRequired } : {})
  };
}

export const VALID_RESOLUTION_STATUSES = new Set<JournalResolutionStatus>([
  'CleanCleanupCompleted',
  'ResolvedRestartRequired',
  'NoJournal',
  'NotResolvable',
  'ProofFailed',
  'PersistenceFailure'
]);

export const VALID_RESOLUTION_REASON_CODES = new Set<string>([
  'CORRUPT_JOURNAL',
  'UNSUPPORTED_VERSION',
  'IO_ERROR',
  'LOCK_TIMEOUT',
  'LEASE_ACQUISITION_FAILED',
  'NO_ACTIVE_ACCOUNT',
  'ACTIVE_ACCOUNT_NOT_FOUND',
  'ACCOUNT_NOT_ENROLLED',
  'LIVE_IDENTITY_UNAVAILABLE',
  'LIVE_IDENTITY_MISMATCH',
  'CREDENTIAL_MISSING',
  'VAULT_SESSION_MISSING',
  'CREDENTIAL_MISMATCH',
  'CONCURRENT_MUTATION',
  'CLEAN_RECORDED_REMOVED',
  'CLEAN_COMMITTED_PRECOMMIT_REMOVED',
  'UNSUPPORTED_PLATFORM',
  'DELETE_FAILED',
  'NO_JOURNAL'
]);

export const FAIL_CLOSED_RESOLUTION_RESULT: JournalResolutionResult = {
  status: 'PersistenceFailure',
  message: 'A persistence failure occurred during journal resolution.',
  restartRequired: false,
  reasonCode: 'UNKNOWN'
};

function getFixedResolutionMessage(status: JournalResolutionStatus, _reasonCode?: string | null): string {
  switch (status) {
    case 'CleanCleanupCompleted':
      return 'Switch journal cleaned up successfully.';
    case 'ResolvedRestartRequired':
      return 'Switch journal successfully resolved and removed. Application restart is required before normal routing resumes.';
    case 'NoJournal':
      return 'No switch journal present.';
    case 'NotResolvable':
      return 'The switch journal cannot be resolved automatically.';
    case 'ProofFailed':
      return 'Account state coherence could not be verified.';
    case 'PersistenceFailure':
      return 'A persistence failure occurred during journal resolution.';
    default:
      return 'An unexpected resolution state occurred.';
  }
}

/**
 * Typed client for AG2 Router loopback HTTP server.
 * Communicates strictly with loopback endpoints, captures switch intent tokens (`X-AG2-Switch-Token`),
 * and validates domain responses before passing them to Svelte state runes.
 */
export class ApiClient {
  private switchIntentToken: string | null = null;

  private async request<T>(url: string, init?: RequestInit): Promise<T> {
    const headers: Record<string, string> = {
      ...(init?.headers as Record<string, string> || {})
    };

    if (init?.body && typeof init.body === 'string' && !headers['Content-Type']) {
      headers['Content-Type'] = 'application/json';
    }

    const res = await fetch(url, {
      ...init,
      headers
    });

    // Capture switch token if present
    const switchHeader = res.headers.get('x-ag2-switch-token');
    if (switchHeader) {
      this.switchIntentToken = switchHeader;
    }

    if (!res.ok) {
      let errorMsg = `HTTP ${res.status}`;
      try {
        const errorJson = (await res.json()) as Record<string, unknown> | null;
        if (errorJson && typeof errorJson.error === 'string') {
          errorMsg = errorJson.error;
        }
      } catch {
        // Fall back to status text
        errorMsg = res.statusText || errorMsg;
      }
      throw new Error(errorMsg);
    }

    return await res.json() as T;
  }

  public async getStatus(): Promise<SystemStatusDto> {
    return this.request<SystemStatusDto>('/api/status');
  }

  public async getAccounts(): Promise<AccountsListResponse> {
    return this.request<AccountsListResponse>('/api/accounts');
  }

  public async createAccount(data: {
    email: string;
    name?: string;
    priority?: number;
    isReserve?: boolean;
    alias?: string;
  }): Promise<{ account: AccountMetadata }> {
    return this.request<{ account: AccountMetadata }>('/api/accounts', {
      method: 'POST',
      body: JSON.stringify(data)
    });
  }

  public async updateAccountAlias(id: string, alias: string): Promise<{ success: boolean; account: AccountMetadata }> {
    return this.request<{ success: boolean; account: AccountMetadata }>(`/api/accounts/${encodeURIComponent(id)}`, {
      method: 'PATCH',
      body: JSON.stringify({ alias })
    });
  }

  public async enrollCurrentAccount(): Promise<{ success: boolean; account: AccountMetadata; message?: string }> {
    return this.request<{ success: boolean; account: AccountMetadata; message?: string }>('/api/accounts/enroll-current', {
      method: 'POST',
      body: JSON.stringify({})
    });
  }

  public async deleteAccount(id: string): Promise<{ success: boolean; removedId: string; vaultRecordDeleted?: boolean }> {
    return this.request<{ success: boolean; removedId: string; vaultRecordDeleted?: boolean }>(`/api/accounts/${encodeURIComponent(id)}`, {
      method: 'DELETE'
    });
  }

  public async getSwitchStatus(): Promise<{ status: SwitchStatusDto }> {
    return this.request<{ status: SwitchStatusDto }>('/api/switching/status');
  }

  public async getSwitchIntent(): Promise<void> {
    await this.request<{ ready: boolean }>('/api/switching/intent', {
      method: 'POST',
      headers: { 'X-AG2-Intent-Request': '1' }
    });
  }

  public async getSwitchPlan(id: string): Promise<SwitchPlanResultDto> {
    return this.request<SwitchPlanResultDto>(`/api/accounts/${encodeURIComponent(id)}/switch-plan`, {
      method: 'POST'
    });
  }

  public async executeSwitch(id: string): Promise<SwitchResultDto> {
    // If we don't have a token, attempt to acquire one first
    if (!this.switchIntentToken) {
      await this.getSwitchIntent();
    }

    const headers: Record<string, string> = { 'Content-Type': 'application/json' };
    if (this.switchIntentToken) {
      headers['X-AG2-Switch-Token'] = this.switchIntentToken;
    }

    const res = await fetch(`/api/accounts/${encodeURIComponent(id)}/switch`, {
      method: 'POST',
      headers,
      body: JSON.stringify({ confirm: true })
    });
    const switchHeader = res.headers.get('x-ag2-switch-token');
    if (switchHeader) this.switchIntentToken = switchHeader;

    let data: unknown = null;
    try { data = await res.json(); } catch { /* Use the HTTP fallback below. */ }
    const result = parseSwitchResult(data);
    if (result && !result.success) throw new SwitchRequestError(result, res.status);
    if (res.ok && result && !result.manualRecoveryRequired) return result;

    const raw = data && typeof data === 'object' ? data as Record<string, unknown> : null;
    const message = typeof raw?.error === 'string' && raw.error.trim()
      ? raw.error
      : res.ok ? 'Invalid switch response; inspect switching status before retrying.' : `HTTP ${res.status}`;
    throw new Error(message);
  }

  public async resolveQuarantine(): Promise<JournalResolutionResult> {
    if (!this.switchIntentToken) {
      try {
        await this.getSwitchIntent();
      } catch {
        return FAIL_CLOSED_RESOLUTION_RESULT;
      }
    }

    const headers: Record<string, string> = {
      'Content-Type': 'application/json'
    };
    if (this.switchIntentToken) {
      headers['X-AG2-Switch-Token'] = this.switchIntentToken;
    }

    let res: Response;
    try {
      res = await fetch('/api/switching/resolve-quarantine', {
        method: 'POST',
        headers,
        body: JSON.stringify({ confirm: true })
      });
    } catch {
      return FAIL_CLOSED_RESOLUTION_RESULT;
    }

    const switchHeader = res.headers.get('x-ag2-switch-token');
    if (switchHeader) {
      this.switchIntentToken = switchHeader;
    }

    let data: unknown = null;
    try {
      data = await res.json();
    } catch {
      return FAIL_CLOSED_RESOLUTION_RESULT;
    }

    if (!data || typeof data !== 'object') {
      return FAIL_CLOSED_RESOLUTION_RESULT;
    }

    const raw = data as Record<string, unknown>;
    const rawStatus = typeof raw.status === 'string' ? raw.status : '';

    if (!VALID_RESOLUTION_STATUSES.has(rawStatus as JournalResolutionStatus)) {
      return FAIL_CLOSED_RESOLUTION_RESULT;
    }

    const status = rawStatus as JournalResolutionStatus;

    // F-286-1 Success Acceptance and Result Consistency Rules:
    // 1. ResolvedRestartRequired requires res.ok AND raw.restartRequired === true.
    // 2. CleanCleanupCompleted and NoJournal may require restart when quarantine remains.
    // 3. Failure states (NotResolvable, ProofFailed, PersistenceFailure) must never assert restartRequired: true.
    // Any violation or contradiction maps fail-closed to FAIL_CLOSED_RESOLUTION_RESULT.
    if (status === 'ResolvedRestartRequired') {
      if (!res.ok || raw.restartRequired !== true) {
        return FAIL_CLOSED_RESOLUTION_RESULT;
      }
    } else if (status === 'CleanCleanupCompleted' || status === 'NoJournal') {
      if (!res.ok || (raw.restartRequired !== undefined && typeof raw.restartRequired !== 'boolean')) {
        return FAIL_CLOSED_RESOLUTION_RESULT;
      }
    } else {
      if (raw.restartRequired === true) {
        return FAIL_CLOSED_RESOLUTION_RESULT;
      }
    }

    let reasonCode: string | null = null;
    if (typeof raw.reasonCode === 'string') {
      reasonCode = VALID_RESOLUTION_REASON_CODES.has(raw.reasonCode) ? raw.reasonCode : 'UNKNOWN';
    }

    const coherentAccountId = typeof raw.coherentAccountId === 'string' ? raw.coherentAccountId : null;
    const restartRequired = status === 'ResolvedRestartRequired' ||
      ((status === 'CleanCleanupCompleted' || status === 'NoJournal') && raw.restartRequired === true);

    return {
      status,
      message: getFixedResolutionMessage(status, reasonCode),
      coherentAccountId,
      restartRequired,
      reasonCode
    };
  }

  public async getConfig(): Promise<{ config: RouterConfigDto }> {
    return this.request<{ config: RouterConfigDto }>('/api/config');
  }

  public async getCandidateEvidence(): Promise<CandidateEvidenceStatusDto> {
    return this.request<CandidateEvidenceStatusDto>('/api/router/candidate-evidence');
  }

  public async saveConfig(config: RouterConfigUpdate): Promise<{ success: boolean; config: RouterConfigDto }> {
    return this.request<{ success: boolean; config: RouterConfigDto }>('/api/config', {
      method: 'POST',
      body: JSON.stringify(config)
    });
  }

  private async getUsageJson<T>(url: string): Promise<T> {
    const res = await fetch(url);
    if (res.status === 501) throw new UsageUnavailableError(501);
    if (!res.ok) {
      let errorMsg = `HTTP ${res.status}`;
      try {
        const errorJson = (await res.json()) as Record<string, unknown> | null;
        if (errorJson && typeof errorJson.error === 'string') errorMsg = errorJson.error;
      } catch {
        errorMsg = res.statusText || errorMsg;
      }
      throw new Error(errorMsg);
    }
    return await res.json() as T;
  }

  public async getUsageSummary(scope: UsageScopeQuery = 'all'): Promise<UsageSummaryResponse> {
    return this.getUsageJson<UsageSummaryResponse>(`/api/usage/summary?scope=${encodeURIComponent(scope)}`);
  }

  public async getUsageTimeSeries(range: UsageRange, scope: UsageScopeQuery = 'all'): Promise<UsageTimeSeriesResponse> {
    if (!USAGE_RANGES.includes(range)) throw new Error('The usage time range is invalid.');
    return this.getUsageJson<UsageTimeSeriesResponse>(`/api/usage/timeseries?range=${encodeURIComponent(range)}&scope=${encodeURIComponent(scope)}`);
  }

  public async getUsageModels(scope: UsageScopeQuery = 'all'): Promise<UsageModelBreakdownResponse> {
    return this.getUsageJson<UsageModelBreakdownResponse>(`/api/usage/models?scope=${encodeURIComponent(scope)}`);
  }
}

export const api = new ApiClient();
