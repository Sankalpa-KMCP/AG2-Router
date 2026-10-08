using System.Text.Json.Serialization;

namespace AG2Router.Core.Models;

public static class SwitchResultCodes
{
    public const string Success = "SUCCESS";
    public const string SwitchInProgress = "SWITCH_IN_PROGRESS";
    public const string TargetNotFound = "TARGET_NOT_FOUND";
    public const string TargetNotVaulted = "TARGET_NOT_VAULTED";
    public const string AlreadyActive = "ALREADY_ACTIVE";
    public const string Ag2Busy = "AG2_BUSY";
    public const string TelemetryUnavailable = "TELEMETRY_UNAVAILABLE";
    public const string UnsafeProcess = "UNSAFE_PROCESS";
    public const string Cancelled = "CANCELLED";
    public const string SwitchFailedRolledBack = "SWITCH_FAILED_ROLLED_BACK";
    public const string SwitchFailedRollbackFailed = "SWITCH_FAILED_ROLLBACK_FAILED";
}

public static class SwitchPlanReasonCodes
{
    public const string Success = "SUCCESS";
    public const string TargetNotFound = "TARGET_NOT_FOUND";
    public const string TargetNotVaulted = "TARGET_NOT_VAULTED";
    public const string TargetIneligible = "TARGET_INELIGIBLE";
    public const string AlreadyActive = "ALREADY_ACTIVE";
    public const string Ag2Busy = "AG2_BUSY";
    public const string TelemetryUnavailable = "TELEMETRY_UNAVAILABLE";
    public const string SwitchInProgress = "SWITCH_IN_PROGRESS";
    public const string SafetyBlocked = "SAFETY_BLOCKED";
    public const string CooldownActive = "COOLDOWN_ACTIVE";
    public const string TargetExhausted = "TARGET_EXHAUSTED";
    public const string TargetBelowMinimum = "TARGET_BELOW_MINIMUM";
}

public record SwitchPlanResultDto(
    [property: JsonPropertyName("targetAccountId")] string TargetAccountId,
    [property: JsonPropertyName("targetEmail")] string? TargetEmail,
    [property: JsonPropertyName("targetExists")] bool TargetExists,
    [property: JsonPropertyName("isEligible")] bool IsEligible,
    [property: JsonPropertyName("hasVaultedSession")] bool HasVaultedSession,
    [property: JsonPropertyName("isAlreadyActive")] bool IsAlreadyActive,
    [property: JsonPropertyName("isReserve")] bool IsReserve,
    [property: JsonPropertyName("systemState")] string SystemState,
    [property: JsonPropertyName("safetyState")] string SafetyState,
    [property: JsonPropertyName("workloadModelKey")] string? WorkloadModelKey,
    [property: JsonPropertyName("targetQuotaPercent")] double? TargetQuotaPercent,
    [property: JsonPropertyName("quotaStatus")] string QuotaStatus,
    [property: JsonPropertyName("observationAgeSeconds")] double? ObservationAgeSeconds,
    [property: JsonPropertyName("inCooldown")] bool InCooldown,
    [property: JsonPropertyName("ineligibilityReason")] string? IneligibilityReason,
    [property: JsonPropertyName("admissible")] bool Admissible,
    [property: JsonPropertyName("reasonCode")] string ReasonCode,
    [property: JsonPropertyName("message")] string Message
);

public static class NativeSwitchStates
{
    public const string Idle = "IDLE";
    public const string Preflight = "PREFLIGHT";
    public const string Snapshotting = "SNAPSHOTTING";
    public const string ApplyingCredential = "APPLYING_CREDENTIAL";
    public const string StoppingProcess = "STOPPING_PROCESS";
    public const string Restarting = "RESTARTING";
    public const string Verifying = "VERIFYING";
    public const string Finalizing = "FINALIZING";
    public const string Complete = "COMPLETE";
    public const string RollingBack = "ROLLING_BACK";
    public const string RolledBack = "ROLLED_BACK";
    public const string Failed = "FAILED";
}

public record NativeSwitchResult(
    [property: JsonPropertyName("transactionId")] string? TransactionId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("targetAccountId")] string TargetAccountId,
    [property: JsonPropertyName("targetEmail")] string? TargetEmail,
    [property: JsonPropertyName("previousAccountId")] string? PreviousAccountId,
    [property: JsonPropertyName("previousEmail")] string? PreviousEmail,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("stagesCompleted")] IReadOnlyList<string> StagesCompleted,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("finishedAt")] string FinishedAt,
    [property: JsonPropertyName("manualRecoveryRequired")] bool ManualRecoveryRequired = false
);

public sealed record ExplicitSwitchRequest(
    [property: JsonPropertyName("confirm")] bool Confirm
);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JournalResolutionStatus
{
    NoJournal,
    CleanCleanupCompleted,
    ResolvedRestartRequired,
    NotResolvable,
    ProofFailed,
    PersistenceFailure
}

public sealed record JournalResolutionResult(
    [property: JsonPropertyName("status")] JournalResolutionStatus Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("coherentAccountId")] string? CoherentAccountId = null,
    [property: JsonPropertyName("restartRequired")] bool RestartRequired = false,
    [property: JsonPropertyName("reasonCode")] string? ReasonCode = null
);

public sealed record ResolveQuarantineRequest(
    [property: JsonPropertyName("confirm")] bool Confirm
);

public static class JournalRecoveryStates
{
    public const string None = "NONE";
    public const string ActionRequired = "ACTION_REQUIRED";
    public const string RestartRequired = "RESTART_REQUIRED";
    public const string NotResolvable = "NOT_RESOLVABLE";
    public const string Unknown = "UNKNOWN";
}

public record NativeSwitchStatus(
    [property: JsonPropertyName("activeTransactionId")] string? ActiveTransactionId,
    [property: JsonPropertyName("currentState")] string CurrentState,
    [property: JsonPropertyName("lastResult")] NativeSwitchResult? LastResult,
    [property: JsonPropertyName("quarantineActive")] bool QuarantineActive = false,
    [property: JsonPropertyName("journalRecoveryState")] string JournalRecoveryState = JournalRecoveryStates.None
);

public interface INativeAccountSwitchCoordinator
{
    Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default);
    Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
        CancellationToken cancellationToken = default) => SwitchAsync(targetAccountId, cancellationToken);
    Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId,
        string? expectedActiveAccountId,
        Func<bool> planIsCurrent,
        string? requiredWorkloadModelKey,
        double? minimumCandidateQuotaPercent,
        CancellationToken cancellationToken = default) => SwitchAutomaticallyAsync(targetAccountId, expectedActiveAccountId, planIsCurrent, cancellationToken);
    Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Acquires configuration ownership at the final currentness proof and retains it
    /// until source interruption is issued, or disposes it if admission/stop fails.
    /// Acquisition runs under switch ownership; it must not reacquire that ownership.
    /// </summary>
    Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        Func<CancellationToken, Task<IDisposable>> acquireInterruptionAdmissionAsync,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This coordinator does not support owned automatic interruption admission.");

    /// <summary>
    /// Asynchronously acquires configuration ownership at the final currentness proof and retains it
    /// until source interruption is issued, or disposes it if admission/stop fails.
    /// </summary>
    Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<Task<bool>> planIsCurrentAsync,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        Func<CancellationToken, Task<IDisposable>> acquireInterruptionAdmissionAsync,
        CancellationToken cancellationToken = default) =>
        SwitchAutomaticallyAsync(targetAccountId, expectedActiveAccountId,
            () => planIsCurrentAsync().GetAwaiter().GetResult(),
            requiredWorkloadModelKey, minimumCandidateQuotaPercent,
            acquireInterruptionAdmissionAsync, cancellationToken);
    NativeSwitchStatus GetStatus();
    Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    bool CanAdmitSwitch(out string? blockingReason)
    {
        blockingReason = null;
        return true;
    }
}
