using System.Text.Json;
using System.Text.Json.Serialization;

namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Segment header metadata record written as the very first line of each owned JSONL log segment.
/// Replaces legacy comment-based headers with valid JSON (R8) and establishes verified ownership metadata (R7).
/// </summary>
public sealed record DiagnosticSegmentHeader
{
    public const string HeaderRecordType = "segmentHeader";
    public const string CurrentFormat = "AG2Router-Diagnostic-Log-v1";

    [JsonPropertyName("recordType")]
    public string RecordType { get; init; } = HeaderRecordType;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = DiagnosticEvent.CurrentSchemaVersion;

    [JsonPropertyName("format")]
    public string Format { get; init; } = CurrentFormat;

    [JsonPropertyName("runId")]
    public required string RunId { get; init; }

    [JsonPropertyName("segmentId")]
    public required string SegmentId { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public required string CreatedAtUtc { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, DiagnosticEvent.SerializerOptions);

    public static DiagnosticSegmentHeader? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("recordType", out var typeProp) ||
                typeProp.GetString() != HeaderRecordType)
            {
                return null;
            }

            return JsonSerializer.Deserialize<DiagnosticSegmentHeader>(json, DiagnosticEvent.SerializerOptions);
        }
        catch
        {
            return null;
        }
    }
}
