namespace AG2Router.Core.Models;

public record CandidateEvaluation(
    AccountMetadata Account,
    double RemainingFraction,
    int QuotaPercent,
    bool IsEligible,
    string? IneligibilityReason,
    double Score
);

public static class CandidatePoolReasonCodes
{
    public const string Ready = "READY";
    public const string NoEnrolledAlternatives = "NO_ENROLLED_ALTERNATIVES";
    public const string ValidationOrSessionFailed = "VALIDATION_OR_SESSION_FAILED";
    public const string AllInCooldown = "ALL_IN_COOLDOWN";
    public const string EvidenceStaleOrUnknown = "EVIDENCE_STALE_OR_UNKNOWN";
    public const string AllExhausted = "ALL_EXHAUSTED";
    public const string AllBelowMinimum = "ALL_BELOW_MINIMUM";
    public const string QuotaDepleted = "QUOTA_DEPLETED";
    public const string ReserveOnly = "RESERVE_ONLY";
}

public record CandidatePoolStatusDto(
    bool HasUsableCandidate,
    string ReasonCode,
    string Message,
    int EnrolledCandidatesCount,
    int EligibleCandidatesCount,
    int UsableCandidatesCount,
    string? EarliestResetTime = null
);

public record SelectionResult(
    bool ShouldSwitch,
    string Reason,
    string? CurrentAccountId,
    double? CurrentQuotaFraction,
    CandidateEvaluation? BestCandidate,
    IReadOnlyList<CandidateEvaluation> Candidates,
    CandidatePoolStatusDto? PoolStatus = null
);

public static class RoutingSafetyGateState
{
    public const string Idle = "IDLE";
    public const string LowQuotaDetected = "LOW_QUOTA_DETECTED";
    public const string SwitchPending = "SWITCH_PENDING";
    public const string WaitingForIdle = "WAITING_FOR_IDLE";
    public const string SwitchInProgress = "SWITCH_IN_PROGRESS";
    public const string Verifying = "VERIFYING";
    public const string SwitchCompleted = "SWITCH_COMPLETED";
    public const string SwitchFailed = "SWITCH_FAILED";
    public const string Cooldown = "COOLDOWN";
    public const string ManualRecoveryRequired = "MANUAL_RECOVERY_REQUIRED";
}

public record SafetyGateAssessment(
    bool CanProceed,
    string CurrentState,
    string Reason,
    int ActiveTrajectoriesCount
);
