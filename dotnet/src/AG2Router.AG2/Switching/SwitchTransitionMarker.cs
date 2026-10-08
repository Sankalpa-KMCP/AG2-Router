using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AG2Router.AG2.Persistence;

namespace AG2Router.AG2.Switching;

/// <summary>
/// Durable transition marker constants. The marker is the zero-secret sidecar record that
/// keeps at least one recovery artifact on disk across every canonical journal transition
/// (delete predecessor → publish successor); without it the inter-primitive gap could leave
/// no journal at all, which startup used to interpret as clean.
/// </summary>
public static class SwitchTransitionMarkerConstants
{
    public const string Magic = "AG2SWITCHTRNS";
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Schema v1 durable transition marker: the exact journal entry the transaction currently
/// owns (Expected) and the exact intended successor (Next), nested verbatim rather than
/// duplicated. Zero-secret: it may contain only fields already authorized for the canonical
/// journal plus marker metadata. Serialized deterministically with strict parsing; a marker
/// whose expected/next pair is not a known journal transition is classified corrupt and
/// fails recovery closed rather than being ignored.
/// </summary>
public sealed record SwitchTransitionMarkerEntry
{
    public const string ExpectedMagic = SwitchTransitionMarkerConstants.Magic;
    public const string CurrentMagic = SwitchTransitionMarkerConstants.Magic;
    public const int CurrentSchemaVersion = SwitchTransitionMarkerConstants.CurrentSchemaVersion;

    [JsonPropertyName("magic")]
    [JsonPropertyOrder(1)]
    public string Magic { get; init; } = ExpectedMagic;

    [JsonPropertyName("schemaVersion")]
    [JsonPropertyOrder(2)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("transactionId")]
    [JsonPropertyOrder(3)]
    public string TransactionId { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    [JsonPropertyOrder(4)]
    [JsonConverter(typeof(IsoDateTimeOffsetConverter))]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("expected")]
    [JsonPropertyOrder(5)]
    public SwitchJournalEntry Expected { get; init; } = new();

    [JsonPropertyName("next")]
    [JsonPropertyOrder(6)]
    public SwitchJournalEntry Next { get; init; } = new();
}

/// <summary>
/// The journal state transitions the coordinator performs with a marker. Any other pair in
/// a marker is not a record this protocol wrote and is treated as corrupt evidence.
/// </summary>
internal static class SwitchJournalTransitionPairs
{
    private static readonly (SwitchJournalState From, SwitchJournalState To)[] AllowedPairs =
    [
        (SwitchJournalState.RECORDED, SwitchJournalState.CREDENTIAL_APPLYING),
        (SwitchJournalState.RECORDED, SwitchJournalState.ROLLING_BACK),
        (SwitchJournalState.CREDENTIAL_APPLYING, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT),
        (SwitchJournalState.CREDENTIAL_APPLYING, SwitchJournalState.ROLLING_BACK),
        (SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, SwitchJournalState.ROLLING_BACK),
        (SwitchJournalState.ROLLING_BACK, SwitchJournalState.QUARANTINED)
    ];

    public static bool IsAllowed(SwitchJournalState from, SwitchJournalState to) =>
        from != to && Array.IndexOf(AllowedPairs, (from, to)) >= 0;
}

/// <summary>
/// Status classification for reading the durable transition marker file.
/// </summary>
public enum SwitchTransitionMarkerReadStatus
{
    Absent,
    Valid,
    Corrupt,
    UnsupportedVersion,
    IoError
}

/// <summary>
/// Classification result of reading the durable transition marker file.
/// </summary>
public sealed record SwitchTransitionMarkerReadResult(
    SwitchTransitionMarkerReadStatus Status,
    SwitchTransitionMarkerEntry? Marker = null,
    string? ErrorMessage = null,
    Exception? Exception = null)
{
    public static SwitchTransitionMarkerReadResult Absent() =>
        new(SwitchTransitionMarkerReadStatus.Absent);

    public static SwitchTransitionMarkerReadResult Valid(SwitchTransitionMarkerEntry marker) =>
        new(SwitchTransitionMarkerReadStatus.Valid, Marker: marker ?? throw new ArgumentNullException(nameof(marker)));

    public static SwitchTransitionMarkerReadResult Corrupt(string errorMessage, Exception? exception = null) =>
        new(SwitchTransitionMarkerReadStatus.Corrupt, ErrorMessage: errorMessage, Exception: exception);

    public static SwitchTransitionMarkerReadResult UnsupportedVersion(string errorMessage) =>
        new(SwitchTransitionMarkerReadStatus.UnsupportedVersion, ErrorMessage: errorMessage);

    public static SwitchTransitionMarkerReadResult IoError(Exception exception) =>
        new(SwitchTransitionMarkerReadStatus.IoError, ErrorMessage: exception?.Message, Exception: exception ?? throw new ArgumentNullException(nameof(exception)));
}

/// <summary>
/// Status classification for create-if-absent publication of a transition marker. Every
/// non-successful status means the marker path's current bytes were preserved untouched.
/// </summary>
public enum SwitchTransitionMarkerWriteStatus
{
    Created,
    AlreadyExists,
    PersistenceFailure,
    UnsupportedPlatform
}

/// <summary>
/// Result of a create-if-absent marker publication. A non-successful status leaves the
/// marker path exactly as it was found; the caller fails closed without having overwritten
/// foreign recovery evidence.
/// </summary>
public sealed record SwitchTransitionMarkerWriteResult(
    SwitchTransitionMarkerWriteStatus Status,
    SwitchTransitionMarkerEntry? Marker = null,
    string? Message = null,
    Exception? Exception = null)
{
    public static SwitchTransitionMarkerWriteResult Created(SwitchTransitionMarkerEntry marker) =>
        new(SwitchTransitionMarkerWriteStatus.Created, Marker: marker ?? throw new ArgumentNullException(nameof(marker)));

    public static SwitchTransitionMarkerWriteResult AlreadyExists(string message) =>
        new(SwitchTransitionMarkerWriteStatus.AlreadyExists, Message: message);

    public static SwitchTransitionMarkerWriteResult PersistenceFailure(string message, Exception? exception = null) =>
        new(SwitchTransitionMarkerWriteStatus.PersistenceFailure, Message: message, Exception: exception);

    public static SwitchTransitionMarkerWriteResult UnsupportedPlatform(string message) =>
        new(SwitchTransitionMarkerWriteStatus.UnsupportedPlatform, Message: message);
}

/// <summary>
/// Status classification for exact-entry conditional deletion of a transition marker.
/// </summary>
public enum SwitchTransitionMarkerDeleteStatus
{
    Deleted,
    NotMatched,
    Absent,
    IoError,
    UnsupportedPlatform
}

/// <summary>
/// Result of exact-entry conditional marker deletion. A replacement or corrupt marker is
/// never deleted: only the exact marker entry the caller owns may be removed.
/// </summary>
public sealed record SwitchTransitionMarkerDeleteResult(
    SwitchTransitionMarkerDeleteStatus Status,
    string? Message = null,
    Exception? Exception = null)
{
    public static SwitchTransitionMarkerDeleteResult Deleted() => new(SwitchTransitionMarkerDeleteStatus.Deleted);
    public static SwitchTransitionMarkerDeleteResult NotMatched(string message) => new(SwitchTransitionMarkerDeleteStatus.NotMatched, Message: message);
    public static SwitchTransitionMarkerDeleteResult Absent() => new(SwitchTransitionMarkerDeleteStatus.Absent);
    public static SwitchTransitionMarkerDeleteResult IoError(Exception exception) => new(SwitchTransitionMarkerDeleteStatus.IoError, Message: exception?.Message, Exception: exception);
    public static SwitchTransitionMarkerDeleteResult UnsupportedPlatform(string message) => new(SwitchTransitionMarkerDeleteStatus.UnsupportedPlatform, Message: message);
}

/// <summary>
/// Contract for transition marker persistence. Implementations must publish create-if-absent
/// (never overwrite an existing marker), delete only the exact owned marker entry, and
/// preserve corrupt/foreign bytes untouched.
/// </summary>
public interface ISwitchTransitionMarkerStore
{
    string MarkerFilePath { get; }

    Task<SwitchTransitionMarkerReadResult> ReadTransitionMarkerAsync(CancellationToken cancellationToken = default);
    Task<SwitchTransitionMarkerWriteResult> CreateTransitionMarkerIfAbsentAsync(
        SwitchTransitionMarkerEntry marker, CancellationToken cancellationToken = default);
    Task<SwitchTransitionMarkerDeleteResult> DeleteTransitionMarkerIfUnchangedAsync(
        SwitchTransitionMarkerEntry expectedMarker, CancellationToken cancellationToken = default);
}

/// <summary>
/// File-backed durable transition marker store. Sits beside the canonical switch journal
/// (canonical path + <see cref="PathSuffix"/>) and mirrors its ownership discipline: durable
/// same-directory temporary file published with a non-replacing rename, exact-entry
/// conditional delete whose verification and disposition share one handle, and strict
/// classification that never mutates or deletes corrupt/unsupported bytes.
/// </summary>
public sealed class SwitchTransitionMarkerStore : ISwitchTransitionMarkerStore
{
    public const string PathSuffix = ".transition";

    public const string Magic = SwitchTransitionMarkerConstants.Magic;
    public const int CurrentSchemaVersion = SwitchTransitionMarkerConstants.CurrentSchemaVersion;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters =
        {
            new JsonStringEnumConverter<SwitchJournalState>(allowIntegerValues: false),
            new JsonStringEnumConverter<SwitchTargetActivationProvenance>(allowIntegerValues: false)
        }
    };

    private readonly string _markerFilePath;

    public string MarkerFilePath => _markerFilePath;

    public SwitchTransitionMarkerStore(string markerFilePath)
    {
        if (string.IsNullOrWhiteSpace(markerFilePath))
        {
            throw new ArgumentException("Marker file path cannot be null or whitespace.", nameof(markerFilePath));
        }

        _markerFilePath = Path.GetFullPath(markerFilePath);
    }

    /// <summary>
    /// Derives the conventional marker path from the canonical journal path so both files
    /// always live in the same directory as one recovery-evidence pair.
    /// </summary>
    public static string PathForJournal(string journalFilePath) =>
        Path.GetFullPath(journalFilePath) + PathSuffix;

    /// <summary>Reads and classifies the marker without mutating or deleting corrupt/unsupported files.</summary>
    public async Task<SwitchTransitionMarkerReadResult> ReadTransitionMarkerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string text;
        try
        {
            if (!File.Exists(_markerFilePath))
            {
                return SwitchTransitionMarkerReadResult.Absent();
            }

            using var stream = new FileStream(
                _markerFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);

            if (stream.Length == 0)
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Transition marker file is empty (zero bytes).");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SwitchTransitionMarkerReadResult.IoError(ex);
        }

        return ParseMarkerFromText(text);
    }

    /// <summary>
    /// Parses and classifies transition marker text into a strict result. A marker whose
    /// nested entries are not valid journal entries, whose transaction bindings disagree, or
    /// whose expected/next pair is not a known journal transition is Corrupt: it must fail
    /// recovery closed, never be ignored or cleaned up.
    /// </summary>
    public static SwitchTransitionMarkerReadResult ParseMarkerFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SwitchTransitionMarkerReadResult.Corrupt("Transition marker file contains only whitespace.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return SwitchTransitionMarkerReadResult.Corrupt($"Malformed JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Transition marker JSON root must be an object.");
            }

            var root = doc.RootElement;

            if (!TryGetProperty(root, "magic", out var magicProp) || magicProp.ValueKind != JsonValueKind.String)
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Missing or invalid required property 'magic'.");
            }

            string? magic = magicProp.GetString();
            if (!string.Equals(magic, Magic, StringComparison.Ordinal))
            {
                return SwitchTransitionMarkerReadResult.UnsupportedVersion($"Unsupported magic '{magic}', expected '{Magic}'.");
            }

            if (!TryGetProperty(root, "schemaVersion", out var versionProp) ||
                versionProp.ValueKind != JsonValueKind.Number ||
                !versionProp.TryGetInt32(out int version))
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Missing or invalid required property 'schemaVersion'.");
            }

            if (version != CurrentSchemaVersion)
            {
                return SwitchTransitionMarkerReadResult.UnsupportedVersion($"Unsupported schema version {version}, expected {CurrentSchemaVersion}.");
            }

            if (!TryGetProperty(root, "transactionId", out var txIdProp) || txIdProp.ValueKind != JsonValueKind.String)
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Missing or invalid required property 'transactionId'.");
            }

            string? txId = txIdProp.GetString();
            if (string.IsNullOrWhiteSpace(txId) || !Guid.TryParse(txId, out _))
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Property 'transactionId' is not a valid UUID.");
            }

            if (!TryGetProperty(root, "createdAt", out var createdAtProp) ||
                createdAtProp.ValueKind != JsonValueKind.String ||
                !createdAtProp.TryGetDateTimeOffset(out var createdAt))
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Missing or invalid required property 'createdAt'.");
            }

            if (!TryGetProperty(root, "expected", out var expectedProp) || expectedProp.ValueKind != JsonValueKind.Object)
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Missing or invalid required property 'expected'.");
            }

            var expectedResult = SwitchJournalStore.ParseEntryElement(expectedProp);
            if (expectedResult.Status != SwitchJournalReadStatus.Valid || expectedResult.Entry == null)
            {
                return SwitchTransitionMarkerReadResult.Corrupt(
                    $"Property 'expected' is not a valid switch journal entry: {expectedResult.ErrorMessage ?? expectedResult.Status.ToString()}");
            }

            if (!TryGetProperty(root, "next", out var nextProp) || nextProp.ValueKind != JsonValueKind.Object)
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Missing or invalid required property 'next'.");
            }

            var nextResult = SwitchJournalStore.ParseEntryElement(nextProp);
            if (nextResult.Status != SwitchJournalReadStatus.Valid || nextResult.Entry == null)
            {
                return SwitchTransitionMarkerReadResult.Corrupt(
                    $"Property 'next' is not a valid switch journal entry: {nextResult.ErrorMessage ?? nextResult.Status.ToString()}");
            }

            var expected = expectedResult.Entry;
            var next = nextResult.Entry;

            // Internal consistency: both nested entries belong to the marker's transaction,
            // and the pair is a transition this protocol actually performs. Anything else is
            // corrupt evidence whose staleness cannot be proven.
            if (!string.Equals(expected.TransactionId, txId, StringComparison.Ordinal) ||
                !string.Equals(next.TransactionId, txId, StringComparison.Ordinal))
            {
                return SwitchTransitionMarkerReadResult.Corrupt("Transition marker transaction does not bind both nested journal entries.");
            }

            if (!SwitchJournalTransitionPairs.IsAllowed(expected.State, next.State))
            {
                return SwitchTransitionMarkerReadResult.Corrupt(
                    $"Transition marker pair '{expected.State}' -> '{next.State}' is not a valid journal transition.");
            }

            var marker = new SwitchTransitionMarkerEntry
            {
                Magic = magic!,
                SchemaVersion = version,
                TransactionId = txId!,
                CreatedAt = createdAt,
                Expected = expected,
                Next = next
            };
            var validation = ValidateMarker(marker);
            return validation == null
                ? SwitchTransitionMarkerReadResult.Valid(marker)
                : SwitchTransitionMarkerReadResult.Corrupt(validation);
        }
    }

    internal Func<Task>? BeforeDispositionHookAsync { get; set; }
    internal Func<Task>? AfterPublishHookAsync { get; set; }
    internal Func<Task>? BeforePublishHookAsync { get; set; }

    /// <summary>
    /// Creates the marker only while the marker path is absent at the atomic commit boundary:
    /// the serialized marker is written to a durable same-directory temporary file
    /// (write-through, flushed to disk) and published with a non-replacing rename. An existing
    /// marker — valid, foreign, or corrupt — is never overwritten; its bytes are preserved and
    /// an AlreadyExists outcome is reported. An ambiguous publish is classified by reading the
    /// marker path back before any ownership is reported.
    /// </summary>
    public async Task<SwitchTransitionMarkerWriteResult> CreateTransitionMarkerIfAbsentAsync(
        SwitchTransitionMarkerEntry marker,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(marker);

        if (marker.Expected is null || marker.Next is null)
        {
            return SwitchTransitionMarkerWriteResult.PersistenceFailure(
                "Transition marker requires both the expected and next journal entries.");
        }

        var validation = ValidateMarker(marker);
        if (validation != null)
        {
            return SwitchTransitionMarkerWriteResult.PersistenceFailure(validation);
        }

        var normalized = NormalizeMarker(marker);
        string json = JsonSerializer.Serialize(normalized, SerializerOptions);

        string? tempPath = null;
        try
        {
            tempPath = await DurableTempFile.WriteAsync(_markerFilePath, json, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (BeforePublishHookAsync != null)
                    await BeforePublishHookAsync().ConfigureAwait(false);
                File.Move(tempPath, _markerFilePath, overwrite: false);
                tempPath = null;
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // The rename either landed or failed atomically; classify by reading the
                // marker path instead of guessing (ambiguous-persistence rule).
                return await ClassifyMarkerPublishOutcomeAsync(normalized, moveError, cancellationToken).ConfigureAwait(false);
            }

            if (AfterPublishHookAsync != null)
            {
                try
                {
                    await AfterPublishHookAsync().ConfigureAwait(false);
                }
                catch (Exception hookError)
                {
                    return await ClassifyMarkerPublishOutcomeAsync(normalized, hookError, cancellationToken).ConfigureAwait(false);
                }
            }

            return SwitchTransitionMarkerWriteResult.Created(normalized);
        }
        finally
        {
            DurableTempFile.TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Deletes the marker if and only if its current on-disk content matches expectedMarker
    /// exactly. Under Windows, verification and deletion are tied to the same underlying file
    /// handle. A replacement or corrupt marker is never deleted, and no transaction-ID-only
    /// deletion exists.
    /// </summary>
    public async Task<SwitchTransitionMarkerDeleteResult> DeleteTransitionMarkerIfUnchangedAsync(
        SwitchTransitionMarkerEntry expectedMarker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedMarker);
        cancellationToken.ThrowIfCancellationRequested();

        var deleteResult = await WindowsConditionalFileDelete.DeleteIfExactAsync(
            _markerFilePath,
            text => ClassifyMarkerContent(text, expectedMarker),
            BeforeDispositionHookAsync,
            cancellationToken).ConfigureAwait(false);

        return deleteResult.Status switch
        {
            ConditionalDeleteStatus.Deleted => SwitchTransitionMarkerDeleteResult.Deleted(),
            ConditionalDeleteStatus.Absent => SwitchTransitionMarkerDeleteResult.Absent(),
            ConditionalDeleteStatus.IoError => SwitchTransitionMarkerDeleteResult.IoError(
                deleteResult.Exception ?? new IOException(deleteResult.Message ?? "Marker deletion I/O error.")),
            ConditionalDeleteStatus.UnsupportedPlatform => SwitchTransitionMarkerDeleteResult.UnsupportedPlatform(
                deleteResult.Message ?? "Conditional marker deletion is not supported on this platform."),
            _ => SwitchTransitionMarkerDeleteResult.NotMatched(deleteResult.Message ?? "Transition marker does not match the expected entry."),
        };
    }

    private static (bool Match, string? NotMatchedReason) ClassifyMarkerContent(string text, SwitchTransitionMarkerEntry expectedMarker)
    {
        var parseResult = ParseMarkerFromText(text);
        if (parseResult.Status != SwitchTransitionMarkerReadStatus.Valid || parseResult.Marker == null)
        {
            return (false, $"Transition marker is not in valid state: {parseResult.ErrorMessage ?? parseResult.Status.ToString()}");
        }

        if (!MarkersMatch(parseResult.Marker, expectedMarker))
        {
            return (false, "Transition marker contents do not match the expected marker entry.");
        }

        return (true, null);
    }

    private async Task<SwitchTransitionMarkerWriteResult> ClassifyMarkerPublishOutcomeAsync(
        SwitchTransitionMarkerEntry normalized,
        Exception failure,
        CancellationToken cancellationToken)
    {
        SwitchTransitionMarkerReadResult readback;
        try
        {
            readback = await ReadTransitionMarkerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception readError)
        {
            return SwitchTransitionMarkerWriteResult.PersistenceFailure(
                $"Marker publish outcome could not be classified: {failure.Message}; readback failed: {readError.Message}",
                new AggregateException(failure, readError));
        }

        if (readback.Status == SwitchTransitionMarkerReadStatus.Valid && readback.Marker != null)
        {
            if (MarkersMatch(readback.Marker, normalized))
            {
                // The publish landed despite the reported failure: ownership is provable.
                return SwitchTransitionMarkerWriteResult.Created(readback.Marker);
            }

            return SwitchTransitionMarkerWriteResult.AlreadyExists(
                "A different transition marker occupies the marker path; it was preserved untouched.");
        }

        if (readback.Status is SwitchTransitionMarkerReadStatus.Corrupt or SwitchTransitionMarkerReadStatus.UnsupportedVersion)
        {
            return SwitchTransitionMarkerWriteResult.AlreadyExists(
                "The marker path is occupied by unreadable marker bytes; they were preserved untouched.");
        }

        if (readback.Status == SwitchTransitionMarkerReadStatus.Absent)
        {
            return SwitchTransitionMarkerWriteResult.PersistenceFailure(
                $"Marker publish failed and no marker is present: {failure.Message}", failure);
        }

        return SwitchTransitionMarkerWriteResult.PersistenceFailure(
            $"Marker publish outcome is uncertain: {failure.Message}; readback status {readback.Status}.", failure);
    }

    /// <summary>
    /// Structural validation shared by publication: a marker that would not read back as a
    /// valid, internally consistent record must not be persisted.
    /// </summary>
    internal static string? ValidateMarker(SwitchTransitionMarkerEntry marker)
    {
        foreach (var entry in new[] { marker.Expected, marker.Next })
        {
            var nested = SwitchJournalStore.ParseEntryFromText(JsonSerializer.Serialize(entry, SerializerOptions));
            if (nested.Status != SwitchJournalReadStatus.Valid)
                return "Marker nested journal entry is not valid canonical schema-v1 evidence.";
        }

        if (string.IsNullOrWhiteSpace(marker.TransactionId) || !Guid.TryParse(marker.TransactionId, out _))
        {
            return "Marker TransactionId must be a valid UUID.";
        }

        if (!string.Equals(marker.Expected.TransactionId, marker.TransactionId, StringComparison.Ordinal) ||
            !string.Equals(marker.Next.TransactionId, marker.TransactionId, StringComparison.Ordinal))
        {
            return "Marker transaction must bind both nested journal entries.";
        }

        if (!SwitchJournalTransitionPairs.IsAllowed(marker.Expected.State, marker.Next.State))
        {
            return $"Marker pair '{marker.Expected.State}' -> '{marker.Next.State}' is not a valid journal transition.";
        }

        if (!string.Equals(marker.Expected.SourceAccountId, marker.Next.SourceAccountId, StringComparison.Ordinal) ||
            !string.Equals(marker.Expected.TargetAccountId, marker.Next.TargetAccountId, StringComparison.Ordinal))
        {
            return "Marker source and target identities must remain unchanged across the transition.";
        }

        // Markers describe new coordinator writes, not migrations of legacy canonical
        // journals. Those journals retain UNKNOWN parsing; no new marker may invent a
        // source-only boundary from unknown or target-attempted evidence.
        var before = marker.Expected.TargetActivationProvenance;
        var after = marker.Next.TargetActivationProvenance;
        bool validProvenance = (marker.Expected.State, marker.Next.State) switch
        {
            (SwitchJournalState.RECORDED, SwitchJournalState.CREDENTIAL_APPLYING) =>
                before == SwitchTargetActivationProvenance.NOT_ATTEMPTED && after == SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED,
            (SwitchJournalState.RECORDED, SwitchJournalState.ROLLING_BACK) =>
                before == SwitchTargetActivationProvenance.NOT_ATTEMPTED && after == before,
            (SwitchJournalState.ROLLING_BACK, SwitchJournalState.QUARANTINED) =>
                before is SwitchTargetActivationProvenance.NOT_ATTEMPTED or SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED && after == before,
            _ => before == SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED && after == before
        };
        if (!validProvenance)
            return "Marker activation provenance is inconsistent with the coordinator transition.";

        return null;
    }

    private static SwitchTransitionMarkerEntry NormalizeMarker(SwitchTransitionMarkerEntry marker) =>
        marker with
        {
            Magic = Magic,
            SchemaVersion = CurrentSchemaVersion,
            CreatedAt = marker.CreatedAt == default ? DateTimeOffset.UtcNow : marker.CreatedAt
        };

    /// <summary>
    /// Exact marker equality, including both nested journal entries. Marker deletion and
    /// stale-marker reconciliation compare this way so a materially different marker (same
    /// transaction ID or not) survives.
    /// </summary>
    internal static bool MarkersMatch(SwitchTransitionMarkerEntry actual, SwitchTransitionMarkerEntry expected)
    {
        return string.Equals(actual.Magic, expected.Magic, StringComparison.Ordinal)
            && actual.SchemaVersion == expected.SchemaVersion
            && string.Equals(actual.TransactionId, expected.TransactionId, StringComparison.Ordinal)
            && actual.CreatedAt == expected.CreatedAt
            && SwitchJournalStore.EntriesMatch(actual.Expected, expected.Expected)
            && SwitchJournalStore.EntriesMatch(actual.Next, expected.Next);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
