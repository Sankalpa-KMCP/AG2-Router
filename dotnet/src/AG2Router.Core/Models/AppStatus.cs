namespace AG2Router.Core.Models;

public record SystemStatusDto(
    string Status,
    Ag2StatusDto Ag2,
    RouterStatusDto Router,
    TelemetryDto? Telemetry
);

public record Ag2StatusDto(
    bool Connected,
    string Status,
    ActivityStatusDto? Activity,
    string Message
);

public record ActivityStatusDto(
    string State,
    int TotalTrajectories,
    int RunningTrajectories,
    string Timestamp
);

public record RouterStatusDto(
    string State,
    bool AutoSwitchEnabled,
    string? ActiveAccountId,
    string? ActiveAccountEmail,
    string? PendingTargetAccountId,
    string? LastEvaluatedAt,
    string? LastDecisionReason,
    RouterConfigDto Config
);

public record RouterConfigDto(
    bool AutoSwitchEnabled = false,
    int LowQuotaThresholdPercent = 15,
    int MinimumCandidateQuotaPercent = 30,
    int PollingIntervalMs = 10000
);

public record TelemetryDto(
    AccountIdentityDto? CurrentAccount,
    QuotaSnapshotDto? Quota,
    ActivityStatusDto? Activity,
    double? TotalAvailableQuotaPercent,
    string? LastSuccessfulTelemetry
);

public record AccountIdentityDto(
    string Email,
    string? Name,
    string? TierId,
    string? TierName,
    string? RawStatusTimestamp
);

public record QuotaSnapshotDto(
    string Timestamp,
    IReadOnlyList<ModelQuotaDto> Models,
    CreditPoolDto? PromptCredits,
    CreditPoolDto? FlowCredits
);

public record ModelQuotaDto(
    string Label,
    string? ModelOrTier,
    double RemainingFraction,
    string? ResetTime,
    bool IsExhausted
);

public record CreditPoolDto(
    long AvailableCredits,
    long MonthlyCredits,
    long UsedCredits
);
