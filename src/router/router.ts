/**
 * AG2 Router - Router Domain Coordinator
 *
 * Coordinates quota monitoring, candidate selection, and idle safety gating.
 *
 * BOUNDARY RULE:
 * This foundation implementation strictly halts before credential mutation or process restarts.
 */

import { IAccountStore } from '../accounts/types.js';
import { IAG2Adapter } from '../ag2/adapter.js';
import { SafetyGate } from './safety-gate.js';
import { selectBestCandidate } from './selector.js';
import {
  DEFAULT_ROUTER_CONFIG,
  RouterConfig,
  RouterStatusSnapshot,
  SelectionResult
} from './types.js';

export const MIN_POLLING_INTERVAL_MS = 1;
export const MAX_POLLING_INTERVAL_MS = 2147483647;

export function validateRouterConfig(config: RouterConfig): void {
  if (!config) {
    throw new Error('Router configuration must not be null or undefined.');
  }
  if (
    typeof config.pollingIntervalMs !== 'number' ||
    !Number.isInteger(config.pollingIntervalMs) ||
    config.pollingIntervalMs < MIN_POLLING_INTERVAL_MS ||
    config.pollingIntervalMs > MAX_POLLING_INTERVAL_MS
  ) {
    throw new RangeError('Polling interval must be an integer between 1 and 2147483647 ms.');
  }
  if (
    typeof config.lowQuotaThresholdPercent !== 'number' ||
    !Number.isFinite(config.lowQuotaThresholdPercent) ||
    config.lowQuotaThresholdPercent < 5 ||
    config.lowQuotaThresholdPercent > 50
  ) {
    throw new RangeError('Low quota threshold must be between 5% and 50%.');
  }
  if (
    typeof config.minimumCandidateQuotaPercent !== 'number' ||
    !Number.isFinite(config.minimumCandidateQuotaPercent) ||
    config.minimumCandidateQuotaPercent < 10 ||
    config.minimumCandidateQuotaPercent > 90
  ) {
    throw new RangeError('Minimum candidate quota must be between 10% and 90%.');
  }
}

export class QuotaRouter {
  private config: RouterConfig;
  private safetyGate: SafetyGate;
  private accountStore: IAccountStore;
  private adapter: IAG2Adapter;

  private isRunning = false;
  private timer: NodeJS.Timeout | null = null;
  private lastEvaluatedAt: string | null = null;
  private lastDecisionReason = 'Router initialized';

  constructor(
    accountStore: IAccountStore,
    adapter: IAG2Adapter,
    config: Partial<RouterConfig> = {}
  ) {
    this.accountStore = accountStore;
    this.adapter = adapter;
    const initialConfig: RouterConfig = { ...DEFAULT_ROUTER_CONFIG, ...config };
    validateRouterConfig(initialConfig);
    this.config = initialConfig;
    this.safetyGate = new SafetyGate();
  }

  public getConfig(): RouterConfig {
    return { ...this.config };
  }

  public updateConfig(updates: Partial<RouterConfig>): RouterConfig {
    const candidate: RouterConfig = {
      ...this.config,
      ...updates
    };
    validateRouterConfig(candidate);
    this.config = candidate;
    return this.getConfig();
  }

  public getSafetyGate(): SafetyGate {
    return this.safetyGate;
  }

  /**
   * Return high-level router status snapshot for UI and API clients.
   */
  public async getStatusSnapshot(): Promise<RouterStatusSnapshot> {
    const activeAccountId = await this.accountStore.getActiveAccountId();
    let activeEmail: string | null = null;

    if (activeAccountId) {
      const acc = await this.accountStore.getAccount(activeAccountId);
      activeEmail = acc?.email ?? null;
    }

    return {
      state: this.safetyGate.getState(),
      autoSwitchEnabled: this.config.autoSwitchEnabled,
      activeAccountId,
      activeAccountEmail: activeEmail,
      pendingTargetAccountId: this.safetyGate.getTargetAccountId(),
      lastEvaluatedAt: this.lastEvaluatedAt,
      lastDecisionReason: this.lastDecisionReason,
      config: this.getConfig()
    };
  }

  /**
   * Run a single evaluation cycle:
   * 1. Inspect active account and quota
   * 2. Select best candidate if low quota detected
   * 3. Evaluate safety gate
   */
  public async evaluateCycle(): Promise<SelectionResult> {
    this.lastEvaluatedAt = new Date().toISOString();

    const activeAccountId = await this.accountStore.getActiveAccountId();
    const accounts = await this.accountStore.listAccounts();

    // Query AG2 adapter for current quota
    const quotaSnapshot = await this.adapter.getQuota();

    // Calculate current account usable quota fraction
    let currentQuotaFraction: number | null = null;
    if (quotaSnapshot && quotaSnapshot.models.length > 0) {
      if (quotaSnapshot.models.some(m => m.isExhausted || m.remainingFraction === 0)) {
        currentQuotaFraction = 0;
      } else if (quotaSnapshot.models.every(m => m.remainingFraction !== null && Number.isFinite(m.remainingFraction))) {
        currentQuotaFraction = Math.min(...quotaSnapshot.models.map(m => m.remainingFraction!));
      }
    }

    // Build account quota map (in foundation, non-active accounts have no live telemetry yet)
    const accountQuotas = new Map<string, number>();
    if (activeAccountId && currentQuotaFraction !== null) {
      accountQuotas.set(activeAccountId, currentQuotaFraction);
    }

    const selection = selectBestCandidate({
      currentAccountId: activeAccountId,
      currentQuotaFraction,
      accounts,
      accountQuotas,
      config: this.config
    });

    this.lastDecisionReason = selection.reason;

    // Safety Gate Assessment
    if (selection.shouldSwitch && selection.bestCandidate) {
      if (this.safetyGate.getState() === 'IDLE') {
        this.safetyGate.transition(
          'LOW_QUOTA_DETECTED',
          selection.reason,
          selection.bestCandidate.account.id
        );
      }

      if (this.config.autoSwitchEnabled) {
        if (this.safetyGate.getState() === 'LOW_QUOTA_DETECTED') {
          this.safetyGate.transition(
            'SWITCH_PENDING',
            `Auto-switch queued for candidate ${selection.bestCandidate.account.email}`,
            selection.bestCandidate.account.id
          );
        }

        const activity = await this.adapter.getActivityState();
        const gateAssessment = this.safetyGate.assessActivity(activity);

        if (!gateAssessment.canProceed) {
          if (this.safetyGate.getState() === 'SWITCH_PENDING') {
            this.safetyGate.transition(
              'WAITING_FOR_IDLE',
              gateAssessment.reason,
              selection.bestCandidate.account.id
            );
          }
        } else {
          // Foundation stop boundary:
          // We DO NOT execute switchAccount() in this foundation setup.
          this.lastDecisionReason = `${selection.reason} (Switch pending; live execution paused at foundation boundary).`;
        }
      }
    } else if (!selection.shouldSwitch && this.safetyGate.getState() !== 'IDLE') {
      this.safetyGate.reset('Quota conditions normalized; returning to IDLE.');
    }

    return selection;
  }

  public start(): void {
    if (this.isRunning) return;
    this.isRunning = true;

    this.evaluateCycle().catch(() => {});
    this.timer = setInterval(() => {
      this.evaluateCycle().catch(() => {});
    }, this.config.pollingIntervalMs);
  }

  public stop(): void {
    this.isRunning = false;
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}
