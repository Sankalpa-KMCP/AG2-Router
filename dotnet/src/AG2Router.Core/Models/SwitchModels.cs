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
    [property: JsonPropertyName("finishedAt")] string FinishedAt
);

public record NativeSwitchStatus(
    [property: JsonPropertyName("activeTransactionId")] string? ActiveTransactionId,
    [property: JsonPropertyName("currentState")] string CurrentState,
    [property: JsonPropertyName("lastResult")] NativeSwitchResult? LastResult
);

public interface INativeAccountSwitchCoordinator
{
    Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default);
    NativeSwitchStatus GetStatus();
}
