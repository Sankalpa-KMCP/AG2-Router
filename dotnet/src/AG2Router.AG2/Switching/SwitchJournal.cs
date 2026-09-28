using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AG2Router.AG2.Switching;

/// <summary>
/// Authoritative state enum for the switch transaction journal defined in ADR-001.
/// Serialized as canonical exact uppercase strings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SwitchJournalState>))]
public enum SwitchJournalState
{
    RECORDED,
    CREDENTIAL_APPLYING,
    TARGET_IDENTITY_VERIFIED_PRECOMMIT,
    ROLLING_BACK,
    QUARANTINED
}

/// <summary>
/// Durable switch transaction journal constants.
/// </summary>
public static class SwitchJournalConstants
{
    public const string Magic = "AG2SWITCHJRNL";
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Schema v1 switch transaction journal record conforming to ADR-001.
/// Adheres to strict minimality and zero-secret invariants.
/// </summary>
public sealed record SwitchJournalEntry
{
    public const string ExpectedMagic = SwitchJournalConstants.Magic;
    public const string CurrentMagic = SwitchJournalConstants.Magic;
    public const int CurrentSchemaVersion = SwitchJournalConstants.CurrentSchemaVersion;

    [JsonPropertyName("magic")]
    [JsonPropertyOrder(1)]
    public string Magic { get; init; } = ExpectedMagic;

    [JsonPropertyName("schemaVersion")]
    [JsonPropertyOrder(2)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("transactionId")]
    [JsonPropertyOrder(3)]
    public string TransactionId { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    [JsonPropertyOrder(4)]
    public SwitchJournalState State { get; init; }

    [JsonPropertyName("updatedAt")]
    [JsonPropertyOrder(5)]
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("sourceAccountId")]
    [JsonPropertyOrder(6)]
    public string SourceAccountId { get; init; } = string.Empty;

    [JsonPropertyName("targetAccountId")]
    [JsonPropertyOrder(7)]
    public string TargetAccountId { get; init; } = string.Empty;

    [JsonPropertyName("quarantineReasonCode")]
    [JsonPropertyOrder(8)]
    public string? QuarantineReasonCode { get; init; }
}

/// <summary>
/// Status classification for reading the durable switch transaction journal file.
/// </summary>
public enum SwitchJournalReadStatus
{
    Absent,
    Valid,
    Corrupt,
    UnsupportedVersion,
    IoError
}

/// <summary>
/// Classification result of reading the durable switch transaction journal file.
/// </summary>
public sealed record SwitchJournalReadResult(
    SwitchJournalReadStatus Status,
    SwitchJournalEntry? Entry = null,
    string? ErrorMessage = null,
    Exception? Exception = null)
{
    public static SwitchJournalReadResult Absent() =>
        new(SwitchJournalReadStatus.Absent);

    public static SwitchJournalReadResult Valid(SwitchJournalEntry entry) =>
        new(SwitchJournalReadStatus.Valid, Entry: entry ?? throw new ArgumentNullException(nameof(entry)));

    public static SwitchJournalReadResult Corrupt(string errorMessage, Exception? exception = null) =>
        new(SwitchJournalReadStatus.Corrupt, ErrorMessage: errorMessage, Exception: exception);

    public static SwitchJournalReadResult UnsupportedVersion(string errorMessage) =>
        new(SwitchJournalReadStatus.UnsupportedVersion, ErrorMessage: errorMessage);

    public static SwitchJournalReadResult IoError(Exception exception) =>
        new(SwitchJournalReadStatus.IoError, ErrorMessage: exception?.Message, Exception: exception ?? throw new ArgumentNullException(nameof(exception)));
}

/// <summary>
/// Contract for switch journal persistence.
/// </summary>
public interface ISwitchJournalStore
{
    public const string Magic = SwitchJournalConstants.Magic;
    public const int CurrentSchemaVersion = SwitchJournalConstants.CurrentSchemaVersion;

    string JournalFilePath { get; }

    Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Status classification for startup switch transaction journal reconciliation.
/// </summary>
public enum StartupJournalReconciliationStatus
{
    Clean,
    Quarantined,
    Degraded
}

/// <summary>
/// Result of startup switch transaction journal reconciliation conforming to ADR-001.
/// </summary>
public sealed record StartupJournalReconciliationResult(
    StartupJournalReconciliationStatus Status,
    string? Message = null,
    SwitchJournalEntry? RetainedEntry = null
);

/// <summary>
/// Formats DateTimeOffset as ISO-8601 UTC in roundtrip "O" format.
/// </summary>
internal sealed class IsoDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Expected string for DateTimeOffset.");
        }

        string? str = reader.GetString();
        if (str == null || !DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            throw new JsonException($"Invalid DateTimeOffset format: '{str}'.");
        }

        return dto;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
}
