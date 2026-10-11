using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Immutable schema v1 diagnostic event record. Closed schema with allowlisted typed fields,
/// strictly bounded size (maximum 8 KiB), and no free-form property bags.
/// Satisfies valid JSONL requirements (R8) with typed recordType.
/// </summary>
public sealed record DiagnosticEvent
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxSerializedBytes = 8192; // 8 KiB

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new JsonStringEnumConverter<DiagnosticSeverity>(allowIntegerValues: false),
            new JsonStringEnumConverter<DiagnosticOutcome>(allowIntegerValues: false),
            new JsonStringEnumConverter<DiagnosticFailureCategory>(allowIntegerValues: false),
            new JsonStringEnumConverter<RecoveryStatus>(allowIntegerValues: false),
            new JsonStringEnumConverter<AffectedFeature>(allowIntegerValues: false)
        }
    };

    // Metadata & Classification
    [JsonPropertyName("recordType")]
    public string RecordType { get; init; } = "event";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string TimestampUtc { get; init; }
    public required long Sequence { get; init; }
    public required string EventInstanceId { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required int EventId { get; init; }
    public required string EventName { get; init; }
    public required string Component { get; init; }
    public required string Operation { get; init; }
    public required string RunId { get; init; }
    public required string CorrelationId { get; init; }
    public required string OperationId { get; init; }
    public required DiagnosticOutcome Outcome { get; init; }
    public required DiagnosticFailureCategory FailureCategory { get; init; }
    public required string SafeMessage { get; init; }
    public required RecoveryStatus RecoveryStatus { get; init; }
    public required AffectedFeature AffectedFeature { get; init; }

    // Optional fields
    public string? ParentOperationId { get; init; }
    public int? CauseEventId { get; init; }
    public double? DurationMs { get; init; }
    public int? Attempt { get; init; }
    public string? Protocol { get; init; }
    public int? HttpStatus { get; init; }
    public string? FailurePhase { get; init; }
    public string? ExceptionType { get; init; }
    public int? HResult { get; init; }
    public string? OriginSymbol { get; init; }
    public string? NextActionCode { get; init; }
    public string? ApplicationVersion { get; init; }
    public string? SourceCommit { get; init; }

    public DiagnosticEvent Sanitize()
    {
        return this with
        {
            RecordType = "event",
            SchemaVersion = CurrentSchemaVersion,
            TimestampUtc = DateTime.TryParse(TimestampUtc, out _) ? TimestampUtc : DateTime.UtcNow.ToString("o"),
            EventName = DiagnosticEventCatalog.GetName(EventId),
            SafeMessage = DiagnosticEventCatalog.GetSafeDescription(EventId),
            Component = DiagnosticSanitizer.SanitizeComponent(Component),
            Operation = DiagnosticSanitizer.SanitizeOperation(Operation),
            OriginSymbol = DiagnosticSanitizer.SanitizeOriginSymbol(OriginSymbol),
            FailurePhase = DiagnosticSanitizer.SanitizeFailurePhase(FailurePhase),
            NextActionCode = DiagnosticSanitizer.SanitizeNextActionCode(NextActionCode),
            Protocol = DiagnosticSanitizer.SanitizeProtocol(Protocol),
            ExceptionType = DiagnosticSanitizer.SanitizeExceptionType(ExceptionType),
            ApplicationVersion = DiagnosticSanitizer.SanitizeVersion(ApplicationVersion),
            SourceCommit = DiagnosticSanitizer.SanitizeCommit(SourceCommit),
            RunId = DiagnosticRunContext.RunId,
            CorrelationId = DiagnosticSanitizer.SanitizeIdentifier(CorrelationId),
            OperationId = DiagnosticSanitizer.SanitizeIdentifier(OperationId),
            EventInstanceId = DiagnosticSanitizer.SanitizeIdentifier(EventInstanceId),
            ParentOperationId = !string.IsNullOrWhiteSpace(ParentOperationId)
                ? DiagnosticSanitizer.SanitizeIdentifier(ParentOperationId)
                : null
        };
    }

    public string ToJson()
    {
        var sanitized = Sanitize();
        string json = JsonSerializer.Serialize(sanitized, SerializerOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxSerializedBytes)
        {
            throw new InvalidOperationException($"Diagnostic event serialized size exceeds maximum {MaxSerializedBytes} bytes.");
        }
        return json;
    }

    public static DiagnosticEvent? FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxSerializedBytes)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("recordType", out var recordType) &&
                recordType.GetString() == DiagnosticSegmentHeader.HeaderRecordType)
            {
                return null;
            }

            return JsonSerializer.Deserialize<DiagnosticEvent>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
