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
