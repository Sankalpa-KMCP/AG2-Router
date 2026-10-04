using System.Text.Json.Serialization;

namespace AG2Router.Core.Models;

/// <summary>Scope selector for usage aggregation queries.</summary>
public enum UsageScopeKind
{
    /// <summary>Every unique persisted call, including unattributed ones.</summary>
    All,

    /// <summary>Only calls defensibly attributed to <see cref="UsageScope.AccountId"/>.</summary>
    Account,

    /// <summary>Only calls with no defensible account attribution.</summary>
    Unattributed
}

/// <summary>
/// Scope for one usage aggregation query. <see cref="UsageScopeKind.Account"/> requires the
/// internal managed account id; emails are never used as usage scope keys.
/// </summary>
public sealed record UsageScope(UsageScopeKind Kind, string? AccountId = null)
{
    public static UsageScope All { get; } = new(UsageScopeKind.All);
    public static UsageScope Unattributed { get; } = new(UsageScopeKind.Unattributed);
    public static UsageScope ForAccount(string accountId) => new(UsageScopeKind.Account, accountId);

    [JsonIgnore]
    public bool IsValid => Kind switch
    {
        UsageScopeKind.All => AccountId is null,
        UsageScopeKind.Unattributed => AccountId is null,
        UsageScopeKind.Account => !string.IsNullOrWhiteSpace(AccountId),
        _ => false
    };
}

/// <summary>
/// Aggregated token counters for one scope. Sums over optional counters include only calls
/// that reported them; the companion call counts preserve the reported-vs-unknown distinction
/// so the UI never silently treats unreported cache reads as zero.
/// </summary>
public sealed record UsageTokenTotals
{
    [JsonPropertyName("calls")]
    public long Calls { get; init; }

    [JsonPropertyName("conversationTokens")]
    public long ConversationTokens { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("responseOutputTokens")]
    public long ResponseOutputTokens { get; init; }

    [JsonPropertyName("responseOutputCalls")]
    public long ResponseOutputCalls { get; init; }

    [JsonPropertyName("thinkingOutputTokens")]
    public long ThinkingOutputTokens { get; init; }

    [JsonPropertyName("thinkingOutputCalls")]
    public long ThinkingOutputCalls { get; init; }

    [JsonPropertyName("cacheReadTokens")]
    public long CacheReadTokens { get; init; }

    [JsonPropertyName("cacheReportedCalls")]
    public long CacheReportedCalls { get; init; }

    [JsonPropertyName("cacheUnknownCalls")]
    public long CacheUnknownCalls { get; init; }

    [JsonPropertyName("outputMismatchCalls")]
    public long OutputMismatchCalls { get; init; }

    public static UsageTokenTotals Empty { get; } = new();
}

/// <summary>Per-model usage breakdown row. Unknown response models keep an explicit bucket.</summary>
public sealed record UsageModelBreakdownItem
{
    [JsonPropertyName("modelKey")]
    public string? ModelKey { get; init; }

    [JsonPropertyName("isUnknownModel")]
    public bool IsUnknownModel { get; init; }

    [JsonPropertyName("calls")]
    public long Calls { get; init; }

    [JsonPropertyName("conversationTokens")]
    public long ConversationTokens { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("thinkingOutputTokens")]
    public long ThinkingOutputTokens { get; init; }

    [JsonPropertyName("cacheReadTokens")]
    public long CacheReadTokens { get; init; }
}

/// <summary>
/// Per-account distribution row for the All Accounts scope. Unattributed usage is an explicit
/// row so the UI can explain why per-account rows may sum to less than the global total.
/// Account ids are durable historical references and may no longer exist in the account store.
/// </summary>
public sealed record UsageAccountBreakdownItem
{
    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }

    [JsonPropertyName("isUnattributed")]
    public bool IsUnattributed { get; init; }

    [JsonPropertyName("calls")]
    public long Calls { get; init; }

    [JsonPropertyName("conversationTokens")]
    public long ConversationTokens { get; init; }
}

/// <summary>Lifetime totals for one usage scope, split by time-attribution basis.</summary>
public sealed record UsageSummaryResult
{
    [JsonPropertyName("scope")]
    public UsageScope Scope { get; init; } = UsageScope.All;

    [JsonPropertyName("totals")]
    public UsageTokenTotals Totals { get; init; } = UsageTokenTotals.Empty;

    /// <summary>
    /// The subset of <see cref="Totals"/> whose calls have unknown historical event time.
    /// These calls count here and in per-model totals but never in time-series buckets.
    /// </summary>
    [JsonPropertyName("historicalUnknown")]
    public UsageTokenTotals HistoricalUnknown { get; init; } = UsageTokenTotals.Empty;

    /// <summary>
    /// Present only for <see cref="UsageScopeKind.All"/>: the explicit unattributed remainder
    /// that explains why per-account totals may sum to less than the global total.
    /// </summary>
    [JsonPropertyName("unattributed")]
    public UsageTokenTotals? Unattributed { get; init; }

    /// <summary>
    /// Present only for <see cref="UsageScopeKind.All"/>: per-account distribution including an
    /// explicit unattributed row. Historical account ids may no longer exist in the account store.
    /// </summary>
    [JsonPropertyName("accounts")]
    public IReadOnlyList<UsageAccountBreakdownItem> Accounts { get; init; } = [];

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }
}

/// <summary>One observation-time trend bucket (UTC). Only ObservationTime calls participate.</summary>
public sealed record UsageTimeBucket
{
    [JsonPropertyName("bucketStartUtc")]
    public DateTimeOffset BucketStartUtc { get; init; }

    [JsonPropertyName("calls")]
    public long Calls { get; init; }

    [JsonPropertyName("conversationTokens")]
    public long ConversationTokens { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cacheReadTokens")]
    public long CacheReadTokens { get; init; }
}

/// <summary>Observation-time trend for one scope plus the separated historical-unknown totals.</summary>
public sealed record UsageTimeSeriesResult
{
    [JsonPropertyName("range")]
    public string Range { get; init; } = string.Empty;

    [JsonPropertyName("buckets")]
    public IReadOnlyList<UsageTimeBucket> Buckets { get; init; } = [];

    [JsonPropertyName("historicalUnknown")]
    public UsageTokenTotals HistoricalUnknown { get; init; } = UsageTokenTotals.Empty;

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }
}

/// <summary>Per-model usage breakdown for one scope.</summary>
public sealed record UsageModelBreakdownResult
{
    [JsonPropertyName("scope")]
    public UsageScope Scope { get; init; } = UsageScope.All;

    [JsonPropertyName("models")]
    public IReadOnlyList<UsageModelBreakdownItem> Models { get; init; } = [];

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }
}

/// <summary>
/// Sanitized collector status for API/UI consumption. Contains no process paths, ports,
/// CSRF tokens, command lines, or raw error text.
/// </summary>
public sealed record UsageCollectorDiagnostics
{
    [JsonPropertyName("baselineEstablished")]
    public bool BaselineEstablished { get; init; }

    [JsonPropertyName("lastCollectionAttemptAtUtc")]
    public DateTimeOffset? LastCollectionAttemptAtUtc { get; init; }

    [JsonPropertyName("lastSuccessfulCollectionAtUtc")]
    public DateTimeOffset? LastSuccessfulCollectionAtUtc { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }

    [JsonPropertyName("instancesDiscovered")]
    public int InstancesDiscovered { get; init; }

    [JsonPropertyName("instancesHealthy")]
    public int InstancesHealthy { get; init; }

    [JsonPropertyName("integrityAvailable")]
    public bool IntegrityAvailable { get; init; } = true;

    public static UsageCollectorDiagnostics Unavailable(string reason) => new()
    {
        IntegrityAvailable = false,
        LastError = reason,
    };
}
