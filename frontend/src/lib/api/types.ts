export interface AccountMetadata {
  id: string;
  email: string;
  name?: string | null;
  alias?: string | null;
  priority: number;
  isReserve: boolean;
  createdAt: string;
  updatedAt?: string | null;
  lastActiveAt?: string | null;
  hasVaultedSession: boolean;
  validationStatus?: string | null;
  notes?: string | null;
  isActive: boolean;
}

export interface AccountsListResponse {
  accounts: AccountMetadata[];
  totalCount: number;
  activeAccountId: string | null;
}

export interface RawModelQuotaDto {
  modelOrTier: string | null;
  label: string;
  remainingFraction: number | null;
  resetTime?: string | null;
  isExhausted: boolean;
}

/**
 * Canonical model quota representation aligned with wire contract (R09).
 * Properties match both Node reference server and native .NET loopback DTOs.
 */
export interface CanonicalModelDto {
  canonicalKey: string;
  displayLabel: string;
  modes: string[];
  remainingFraction: number | null;
  resetTime?: string | null;
  isExhausted: boolean;
}

export interface CreditPoolDto {
  availableCredits?: number | null;
  monthlyCredits?: number | null;
  usedCredits?: number | null;
}

export interface QuotaSnapshotDto {
  timestamp: string;
  models: RawModelQuotaDto[];
  promptCredits?: CreditPoolDto | null;
  flowCredits?: CreditPoolDto | null;
  canonicalModels?: CanonicalModelDto[] | null;
}

export interface AccountIdentityDto {
  email: string;
  name?: string | null;
  tierId?: string | null;
  tierName?: string | null;
}

export interface ActivityStatusDto {
  state: string;
  totalTrajectories: number;
  runningTrajectories: number;
  timestamp: string;
}

export interface Ag2StatusDto {
  connected: boolean;
  status: string;
  activity?: ActivityStatusDto | null;
  message?: string | null;
}

export interface RouterConfigDto {
  autoSwitchEnabled: boolean;
  lowQuotaThresholdPercent: number;
  minimumCandidateQuotaPercent: number;
  pollingIntervalMs: number;
  workloadModelKey?: string | null;
}

export type RouterConfigUpdate = RouterConfigDto & { workloadModelKey: string | null };

export interface CandidateQuotaStatusDto {
  accountId: string;
  state: string;
  remainingFraction: number | null;
  observedAtUtc: string | null;
  ageSeconds: number | null;
}

export interface CandidateEvidenceStatusDto {
  modelKey: string | null;
  minimumCandidateQuotaPercent: number;
  available: boolean;
  candidates: CandidateQuotaStatusDto[];
}

export interface RouterStatusDto {
  state: string;
  autoSwitchEnabled: boolean;
  activeAccountId?: string | null;
  activeAccountEmail?: string | null;
  pendingTargetAccountId?: string | null;
  lastEvaluatedAt?: string | null;
  lastDecisionReason?: string | null;
  config?: RouterConfigDto;
}

export interface TelemetrySnapshotDto {
  currentAccount?: AccountIdentityDto | null;
  quota?: QuotaSnapshotDto | null;
  activity?: ActivityStatusDto | null;
  lastSuccessfulTelemetry?: string | null;
}

export interface SystemStatusDto {
  status: string;
  ag2?: Ag2StatusDto | null;
  router?: RouterStatusDto | null;
  telemetry?: TelemetrySnapshotDto | null;
}

export type JournalRecoveryState = 'NONE' | 'ACTION_REQUIRED' | 'RESTART_REQUIRED' | 'NOT_RESOLVABLE' | 'UNKNOWN';

/** Dashboard-consumed fields of the native NativeSwitchResult wire contract. */
export interface SwitchResultDto {
  success: boolean;
  code: string;
  message: string;
  manualRecoveryRequired?: boolean;
}

export interface SwitchStatusDto {
  activeTransactionId?: string | null;
  currentState: string;
  lastResult?: {
    code: string;
    message: string;
  } | null;
  quarantineActive?: boolean;
  journalRecoveryState?: JournalRecoveryState;
}

export type JournalResolutionStatus =
  | 'NoJournal'
  | 'CleanCleanupCompleted'
  | 'ResolvedRestartRequired'
  | 'NotResolvable'
  | 'ProofFailed'
  | 'PersistenceFailure';

export interface JournalResolutionResult {
  status: JournalResolutionStatus;
  message: string;
  coherentAccountId?: string | null;
  restartRequired?: boolean;
  reasonCode?: string | null;
}

export interface ResolveQuarantineRequest {
  confirm: boolean;
}


// ---- Usage accounting (conversation calls) ----

export interface UsageTokenTotals {
  calls: number;
  conversationTokens: number;
  inputTokens: number;
  outputTokens: number;
  responseOutputTokens: number;
  responseOutputCalls: number;
  thinkingOutputTokens: number;
  thinkingOutputCalls: number;
  cacheReadTokens: number;
  cacheReportedCalls: number;
  cacheUnknownCalls: number;
  outputMismatchCalls: number;
}

export type UsageScopeQuery = 'all' | 'unattributed' | `account:${string}`;

export interface UsageCollectorStatus {
  baselineEstablished: boolean;
  lastCollectionAttemptAtUtc: string | null;
  lastSuccessfulCollectionAtUtc: string | null;
  lastError: string | null;
  instancesDiscovered: number;
  instancesHealthy: number;
  integrityAvailable: boolean;
}

export interface UsageAccountBreakdownItem {
  accountId: string | null;
  isUnattributed: boolean;
  calls: number;
  conversationTokens: number;
}

export interface UsageSummary {
  scope: { kind: string; accountId: string | null };
  totals: UsageTokenTotals;
  historicalUnknown: UsageTokenTotals;
  unattributed: UsageTokenTotals | null;
  accounts: UsageAccountBreakdownItem[];
  generatedAtUtc: string;
}

export interface UsageSummaryResponse {
  summary: UsageSummary;
  collector?: UsageCollectorStatus | null;
}

export interface UsageTimeBucket {
  bucketStartUtc: string;
  calls: number;
  conversationTokens: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
}

export interface UsageTimeSeries {
  range: string;
  buckets: UsageTimeBucket[];
  historicalUnknown: UsageTokenTotals;
  generatedAtUtc: string;
}

export interface UsageTimeSeriesResponse {
  timeseries: UsageTimeSeries;
}

export interface UsageModelBreakdownItem {
  modelKey: string | null;
  isUnknownModel: boolean;
  calls: number;
  conversationTokens: number;
  inputTokens: number;
  outputTokens: number;
  thinkingOutputTokens: number;
  cacheReadTokens: number;
}

export interface UsageModelBreakdown {
  scope: { kind: string; accountId: string | null };
  models: UsageModelBreakdownItem[];
  generatedAtUtc: string;
}

export interface UsageModelBreakdownResponse {
  models: UsageModelBreakdown;
}

export type UsageRange = '24h' | '7d' | '30d' | 'all';

export const USAGE_RANGES: readonly UsageRange[] = ['24h', '7d', '30d', 'all'];
