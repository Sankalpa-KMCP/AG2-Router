export interface AccountMetadata {
  id: string;
  email: string;
  name?: string | null;
  alias?: string | null;
  priority: number;
  isReserve: boolean;
  createdAt: string;
  updatedAt?: string | null;
  lastUsedAt?: string | null;
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
  modelOrTier: string;
  label: string;
  remainingFraction: number;
  resetTime?: string | null;
  isExhausted: boolean;
}

export interface CanonicalModelDto {
  canonicalKey: string;
  displayLabel: string;
  modes: string[];
  remainingFraction: number;
  resetTime?: string | null;
  isExhausted: boolean;
  timeUntilReset?: string | null;
  isRollingWindow?: boolean;
}

export interface CreditPoolDto {
  availableCredits: number;
  monthlyCredits: number;
  usedCredits: number;
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
  lowQuotaThresholdPercent: number;
  minimumCandidateQuotaPercent: number;
  pollingIntervalMs: number;
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

export interface SwitchPlanCheck {
  code: string;
  passed: boolean;
  message: string;
}

export interface SwitchPlanDto {
  ready: boolean;
  blockers: string[];
  checks: SwitchPlanCheck[];
}

export interface SwitchStatusDto {
  activeTransactionId?: string | null;
  currentState: string;
  lastResult?: {
    code: number;
    message: string;
  } | null;
}
