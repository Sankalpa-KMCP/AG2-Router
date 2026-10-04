using System.Text.Json.Serialization;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Persistence;

/// <summary>
/// Root serialization document for one usage-ledger segment. A segment groups calls by the
/// UTC calendar month of their first observation; this partition is a physical write-
/// amplification boundary only and must never be presented as provider event time.
/// </summary>
internal sealed record UsageLedgerSegmentDocument
{
    public const string CurrentMagic = "AG2USAGELDGR";
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("magic")]
    public string Magic { get; init; } = CurrentMagic;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("segmentMonth")]
    public string SegmentMonth { get; init; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("calls")]
    public IReadOnlyList<UsageCallRecord> Calls { get; init; } = [];

    [JsonConstructor]
    public UsageLedgerSegmentDocument(
        string? magic,
        int schemaVersion,
        string? segmentMonth,
        DateTimeOffset updatedAt,
        IReadOnlyList<UsageCallRecord>? calls)
    {
        // Deserialization supplies null for both a missing property and an explicit null;
        // only an explicit array may represent an existing empty segment.
        Magic = magic ?? throw new ArgumentNullException(nameof(magic), "A usage segment magic value is required.");
        SegmentMonth = segmentMonth ?? throw new ArgumentNullException(nameof(segmentMonth), "A usage segment month is required.");
        Calls = calls ?? throw new ArgumentNullException(nameof(calls), "A usage segment calls array is required.");
        SchemaVersion = schemaVersion;
        UpdatedAt = updatedAt;
    }
}
