using System.Text.Json.Serialization;

namespace AG2Router.Core.Models;

/// <summary>
/// Durable per-account model quota observation conforming to Slice 2 specifications.
/// Represents a point-in-time quota observation for a specific model under an account.
/// Contains zero secret fields.
/// </summary>
public record AccountModelQuotaObservation
{
    [JsonPropertyName("accountId")]
    public string AccountId { get; init; }

    [JsonPropertyName("modelKey")]
    public string ModelKey { get; init; }

    [JsonPropertyName("remainingFraction")]
    public double? RemainingFraction { get; init; }

    [JsonPropertyName("resetTime")]
    public string? ResetTime { get; init; }

    [JsonPropertyName("observedAtUtc")]
    public DateTimeOffset ObservedAtUtc { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; }

    [JsonConstructor]
    public AccountModelQuotaObservation(
        string accountId,
        string modelKey,
        double? remainingFraction,
        string? resetTime,
        DateTimeOffset observedAtUtc,
        string source)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("AccountId cannot be null or whitespace.", nameof(accountId));
        if (string.IsNullOrWhiteSpace(modelKey))
            throw new ArgumentException("ModelKey cannot be null or whitespace.", nameof(modelKey));
        if (remainingFraction.HasValue && (!double.IsFinite(remainingFraction.Value) || remainingFraction < 0.0 || remainingFraction > 1.0))
            throw new ArgumentOutOfRangeException(nameof(remainingFraction), remainingFraction, "RemainingFraction must be a finite number between 0.0 and 1.0.");
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source cannot be null or whitespace.", nameof(source));

        AccountId = accountId.Trim();
        ModelKey = modelKey.Trim().ToLowerInvariant();
        RemainingFraction = remainingFraction;
        ResetTime = resetTime;
        ObservedAtUtc = observedAtUtc;
        Source = source.Trim();
    }

    public void Deconstruct(
        out string accountId,
        out string modelKey,
        out double? remainingFraction,
        out string? resetTime,
        out DateTimeOffset observedAtUtc,
        out string source)
    {
        accountId = AccountId;
        modelKey = ModelKey;
        remainingFraction = RemainingFraction;
        resetTime = ResetTime;
        observedAtUtc = ObservedAtUtc;
        source = Source;
    }
}

/// <summary>
/// Root serialization document for quota observations persistence store.
/// Contains schema version, update timestamp, and the collection of observations.
/// </summary>
public record QuotaObservationsDocument
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("observations")]
    public IReadOnlyList<AccountModelQuotaObservation> Observations { get; init; }

    [JsonConstructor]
    public QuotaObservationsDocument(
        int schemaVersion,
        DateTimeOffset updatedAt,
        IReadOnlyList<AccountModelQuotaObservation>? observations)
    {
        SchemaVersion = schemaVersion;
        UpdatedAt = updatedAt;
        // Deserialization supplies null for both a missing property and an explicit null.
        // Only an explicit collection may represent an existing empty document.
        Observations = observations ?? throw new ArgumentNullException(nameof(observations), "An observations array is required.");
    }
}

/// <summary>
/// One atomic read of the durable quota observation store: the consolidated observation rows
/// and the durable evidence revision they were read at. The revision changes whenever a
/// durable observation mutation lands (record, complete snapshot, or account-wide
/// invalidation), so a reader can prove its rows still describe current evidence. The rows
/// and revision always describe the same durable state because they come from one read.
/// </summary>
public sealed record QuotaObservationSnapshot(
    IReadOnlyList<AccountModelQuotaObservation> Observations,
    long Revision);
