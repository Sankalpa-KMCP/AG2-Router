namespace AG2Router.Core.Models;

public record CandidateEvaluation(
    AccountMetadata Account,
    double RemainingFraction,
    int QuotaPercent,
    bool IsEligible,
    string? IneligibilityReason,
    double Score
);

public record SelectionResult(
    bool ShouldSwitch,
    string Reason,
    string? CurrentAccountId,
    double? CurrentQuotaFraction,
    CandidateEvaluation? BestCandidate,
    IReadOnlyList<CandidateEvaluation> Candidates
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
