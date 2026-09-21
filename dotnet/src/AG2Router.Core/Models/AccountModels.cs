using System.Text.Json.Serialization;

namespace AG2Router.Core.Models;

/// <summary>
/// Account validation status values matching the TypeScript router domain model.
/// </summary>
public static class AccountValidationStatus
{
    public const string Valid = "VALID";
    public const string Expired = "EXPIRED";
    public const string Unvalidated = "UNVALIDATED";
    public const string Failed = "FAILED";
}

/// <summary>
/// Input for registering a new account.
/// </summary>
public record CreateAccountInput(
    string Email,
    string? Name = null,
    int? Priority = null,
    bool? IsReserve = null,
    bool? HasVaultedSession = null,
    string? Notes = null,
    string? Alias = null
);

/// <summary>
/// Input for updating existing account metadata.
/// </summary>
public record UpdateAccountInput(
    string? Name = null,
    int? Priority = null,
    bool? IsReserve = null,
    string? ValidationStatus = null,
    bool? HasVaultedSession = null,
    string? LastActiveAt = null,
    string? Notes = null,
    string? Alias = null
);

/// <summary>
/// Cached quota overview for an account.
/// </summary>
public record AccountQuotaSummary(
    [property: JsonPropertyName("remainingFraction")] double RemainingFraction,
    [property: JsonPropertyName("promptCredits")] double? PromptCredits,
    [property: JsonPropertyName("flowCredits")] double? FlowCredits,
    [property: JsonPropertyName("lastUpdated")] string LastUpdated
);

/// <summary>
/// Enriched account summary presented to dashboard and routing selector.
/// </summary>
public record AccountSummary(
    [property: JsonPropertyName("metadata")] AccountMetadata Metadata,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("hasVaultedSession")] bool HasVaultedSession,
    [property: JsonPropertyName("quota")] AccountQuotaSummary? Quota
);

/// <summary>
/// Enrollment request options.
/// </summary>
public record EnrollmentOptions(
    string? Name = null,
    int? Priority = null,
    bool? IsReserve = null,
    string? Notes = null,
    string? Alias = null
);

/// <summary>
/// Enrollment result returned to callers.
/// </summary>
public record EnrollmentResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("account")] AccountMetadata Account,
    [property: JsonPropertyName("isNew")] bool IsNew,
    [property: JsonPropertyName("message")] string Message
);
