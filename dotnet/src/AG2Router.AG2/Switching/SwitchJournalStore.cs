using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AG2Router.AG2.Persistence;

namespace AG2Router.AG2.Switching;

/// <summary>
/// File-backed durable switch transaction journal store conforming to ADR-001.
/// Receives an already-resolved journal file path and delegates atomic replacement to IDurableFileWriter.
/// Does not resolve environment or data-root paths internally.
/// </summary>
public sealed class SwitchJournalStore : ISwitchJournalStore
{
    public const string Magic = SwitchJournalConstants.Magic;
    public const int CurrentSchemaVersion = SwitchJournalConstants.CurrentSchemaVersion;

    private static readonly JsonSerializerOptions SerializerOptions = new()
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

    private readonly string _journalFilePath;
    private readonly IDurableFileWriter _fileWriter;
    private readonly SwitchTransitionMarkerStore _markerStore;

    public string JournalFilePath => _journalFilePath;

    /// <summary>
    /// The transition marker store beside this journal; exposed for fault-injection seams.
    /// </summary>
    internal SwitchTransitionMarkerStore MarkerStore => _markerStore;

    public SwitchJournalStore(string journalFilePath)
        : this(journalFilePath, new DurableFileWriter())
    {
    }

    internal SwitchJournalStore(string journalFilePath, IDurableFileWriter? fileWriter)
    {
        if (string.IsNullOrWhiteSpace(journalFilePath))
        {
            throw new ArgumentException("Journal file path cannot be null or whitespace.", nameof(journalFilePath));
        }

        _journalFilePath = Path.GetFullPath(journalFilePath);
        _fileWriter = fileWriter ?? new DurableFileWriter();
        _markerStore = new SwitchTransitionMarkerStore(SwitchTransitionMarkerStore.PathForJournal(_journalFilePath));
    }

    /// <summary>
    /// Reads and classifies the on-disk switch journal without mutating or deleting corrupt/unsupported files.
    /// </summary>
    public async Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string text;
        try
        {
            if (!File.Exists(_journalFilePath))
            {
                return SwitchJournalReadResult.Absent();
            }

            using var stream = new FileStream(
                _journalFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);

            if (stream.Length == 0)
            {
                return SwitchJournalReadResult.Corrupt("Journal file is empty (zero bytes).");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SwitchJournalReadResult.IoError(ex);
        }

        return ParseEntryFromText(text);
    }

    /// <summary>
    /// Parses and classifies the switch journal text into a SwitchJournalReadResult.
    /// </summary>
    public static SwitchJournalReadResult ParseEntryFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SwitchJournalReadResult.Corrupt("Journal file contains only whitespace.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            return SwitchJournalReadResult.Corrupt($"Malformed JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return SwitchJournalReadResult.Corrupt("Journal JSON root must be an object.");
            }

            return ParseEntryElement(doc.RootElement);
        }
    }

    /// <summary>
    /// Validates a serialized journal-entry JSON element against the canonical schema v1
    /// shape. Shared with the transition marker, whose nested expected/next entries must
    /// satisfy exactly the same strictness as the canonical journal.
    /// </summary>
    internal static SwitchJournalReadResult ParseEntryElement(JsonElement root)
    {
        // 1. Validate Magic
        if (!TryGetProperty(root, "magic", out var magicProp) || magicProp.ValueKind != JsonValueKind.String)
        {
            return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'magic'.");
        }

        string? magic = magicProp.GetString();
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            return SwitchJournalReadResult.UnsupportedVersion($"Unsupported magic '{magic}', expected '{Magic}'.");
        }

        // 2. Validate SchemaVersion
        if (!TryGetProperty(root, "schemaVersion", out var versionProp) ||
            versionProp.ValueKind != JsonValueKind.Number ||
            !versionProp.TryGetInt32(out int version))
        {
            return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'schemaVersion'.");
        }

        if (version != CurrentSchemaVersion)
        {
            return SwitchJournalReadResult.UnsupportedVersion($"Unsupported schema version {version}, expected {CurrentSchemaVersion}.");
        }

        // 3. Validate TransactionId
        if (!TryGetProperty(root, "transactionId", out var txIdProp) || txIdProp.ValueKind != JsonValueKind.String)
        {
            return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'transactionId'.");
        }

        string? txId = txIdProp.GetString();
        if (string.IsNullOrWhiteSpace(txId))
        {
            return SwitchJournalReadResult.Corrupt("Property 'transactionId' cannot be empty.");
        }

        if (!Guid.TryParse(txId, out _))
        {
            return SwitchJournalReadResult.Corrupt($"Property 'transactionId' '{txId}' is not a valid UUID format.");
        }

        // 4. Validate State
        if (!TryGetProperty(root, "state", out var stateProp) || stateProp.ValueKind != JsonValueKind.String)
        {
            return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'state'.");
        }

        string? stateStr = stateProp.GetString();
        if (string.IsNullOrWhiteSpace(stateStr) ||
            !Enum.TryParse<SwitchJournalState>(stateStr, ignoreCase: false, out var state) ||
            !Enum.IsDefined(state) ||
            state.ToString() != stateStr)
        {
            return SwitchJournalReadResult.Corrupt($"Property 'state' '{stateStr}' is not a valid canonical SwitchJournalState.");
        }

        // 5. Validate UpdatedAt
        if (!TryGetProperty(root, "updatedAt", out var updatedAtProp) ||
            updatedAtProp.ValueKind != JsonValueKind.String ||
            !updatedAtProp.TryGetDateTimeOffset(out var updatedAt))
        {
            return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'updatedAt'.");
        }

        // 6. Validate SourceAccountId
        if (!TryGetProperty(root, "sourceAccountId", out var sourceProp) || sourceProp.ValueKind != JsonValueKind.String)
        {
            return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'sourceAccountId'.");
        }

        string? sourceId = sourceProp.GetString();
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return SwitchJournalReadResult.Corrupt("Property 'sourceAccountId' cannot be empty.");
        }

        // 7. Validate TargetAccountId
        if (!TryGetProperty(root, "targetAccountId", out var targetProp) || targetProp.ValueKind != JsonValueKind.String)
        {
            return SwitchJournalReadResult.Corrupt("Missing or invalid required property 'targetAccountId'.");
        }

        string? targetId = targetProp.GetString();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return SwitchJournalReadResult.Corrupt("Property 'targetAccountId' cannot be empty.");
        }

        // 8. Optional QuarantineReasonCode
        string? quarantineReason = null;
        if (TryGetProperty(root, "quarantineReasonCode", out var qProp) && qProp.ValueKind != JsonValueKind.Null)
        {
            if (qProp.ValueKind == JsonValueKind.String)
            {
                quarantineReason = qProp.GetString();
            }
            else
            {
                return SwitchJournalReadResult.Corrupt("Property 'quarantineReasonCode' must be a string or null.");
            }
        }

        // 9. Optional TargetActivationProvenance. Absence is the legacy form and must read
        // as UNKNOWN, never as NOT_ATTEMPTED: an old journal proves nothing about whether
        // the target credential boundary was crossed.
        var provenance = SwitchTargetActivationProvenance.UNKNOWN;
        if (TryGetProperty(root, "targetActivationProvenance", out var pProp) && pProp.ValueKind != JsonValueKind.Null)
        {
            if (pProp.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(pProp.GetString()) ||
                !Enum.TryParse<SwitchTargetActivationProvenance>(pProp.GetString(), ignoreCase: false, out var parsedProvenance) ||
                !Enum.IsDefined(parsedProvenance) ||
                parsedProvenance.ToString() != pProp.GetString())
            {
                return SwitchJournalReadResult.Corrupt("Property 'targetActivationProvenance' is not a valid canonical SwitchTargetActivationProvenance.");
            }

            provenance = parsedProvenance;
        }

        var entry = new SwitchJournalEntry
        {
            Magic = magic!,
            SchemaVersion = version,
            TransactionId = txId!,
            State = state,
            UpdatedAt = updatedAt,
            SourceAccountId = sourceId!,
            TargetAccountId = targetId!,
            QuarantineReasonCode = quarantineReason,
            TargetActivationProvenance = provenance
        };

        return SwitchJournalReadResult.Valid(entry);
    }

    internal Func<bool> IsWindowsPlatform { get; set; } = OperatingSystem.IsWindows;
    internal Func<Task>? BeforeDispositionHookAsync { get; set; }
    internal Func<Task>? BeforeGuardAcquireHookAsync { get; set; }
    internal Func<Task>? AfterGuardAcquireHookAsync { get; set; }

    /// <summary>
    /// Deletes the journal file if and only if its current on-disk content matches expectedEntry.
    /// Under Windows, ties verification and deletion to the same underlying file handle to eliminate
    /// post-read replacement races.
    /// </summary>
    public async Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(
        SwitchJournalEntry expectedEntry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedEntry);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsWindowsPlatform())
        {
            return await DeleteIfUnchangedWindowsAsync(expectedEntry, cancellationToken).ConfigureAwait(false);
        }

        return DeleteIfUnchangedNonWindows(expectedEntry);
    }

    /// <summary>
    /// Atomically validates and deletes the journal file on Windows to prevent TOCTOU race
    /// conditions. Contents are inspected through the same handle that carries the delete
    /// disposition, so the compared bytes are the deleted bytes. The handle's share mode
    /// (FILE_SHARE_READ | FILE_SHARE_DELETE) does not exclude external rename/delete —
    /// deletion is authorized by the exact-entry comparison, and any external interference
    /// remains durable evidence that the post-cleanup path proof and admission rereads
    /// observe fail-closed.
    /// </summary>
    private async Task<SwitchJournalDeleteResult> DeleteIfUnchangedWindowsAsync(
        SwitchJournalEntry expectedEntry,
        CancellationToken cancellationToken)
    {
        var deleteResult = await WindowsConditionalFileDelete.DeleteIfExactAsync(
            _journalFilePath,
            text => ClassifyJournalContent(text, expectedEntry),
            BeforeDispositionHookAsync,
            cancellationToken).ConfigureAwait(false);

        return deleteResult.Status switch
        {
            ConditionalDeleteStatus.Deleted => SwitchJournalDeleteResult.Deleted(),
            ConditionalDeleteStatus.Absent => SwitchJournalDeleteResult.Absent(),
            ConditionalDeleteStatus.IoError => SwitchJournalDeleteResult.IoError(
                deleteResult.Exception ?? new IOException(deleteResult.Message ?? "Journal deletion I/O error.")),
            ConditionalDeleteStatus.UnsupportedPlatform => SwitchJournalDeleteResult.UnsupportedPlatform(
                deleteResult.Message ?? "Conditional switch journal deletion is only supported on Windows."),
            _ => SwitchJournalDeleteResult.NotMatched(deleteResult.Message ?? "Journal contents do not match expected entry."),
        };
    }

    private static (bool Match, string? NotMatchedReason) ClassifyJournalContent(string text, SwitchJournalEntry expectedEntry)
    {
        var parseResult = ParseEntryFromText(text);
        if (parseResult.Status != SwitchJournalReadStatus.Valid || parseResult.Entry == null)
        {
            return (false, $"Journal is not in valid state: {parseResult.ErrorMessage ?? parseResult.Status.ToString()}");
        }

        if (!EntriesMatch(parseResult.Entry, expectedEntry))
        {
            return (false, "Journal contents do not match expected entry.");
        }

        return (true, null);
    }

    private static SwitchJournalDeleteResult DeleteIfUnchangedNonWindows(SwitchJournalEntry expectedEntry)
    {
        return SwitchJournalDeleteResult.UnsupportedPlatform("Conditional switch journal deletion is only supported on Windows.");
    }

    internal static bool EntriesMatch(SwitchJournalEntry actual, SwitchJournalEntry expected)
    {
        return string.Equals(actual.Magic, expected.Magic, StringComparison.Ordinal)
            && actual.SchemaVersion == expected.SchemaVersion
            && string.Equals(actual.TransactionId, expected.TransactionId, StringComparison.Ordinal)
            && actual.State == expected.State
            && actual.UpdatedAt == expected.UpdatedAt
            && string.Equals(actual.SourceAccountId, expected.SourceAccountId, StringComparison.Ordinal)
            && string.Equals(actual.TargetAccountId, expected.TargetAccountId, StringComparison.Ordinal)
            && string.Equals(actual.QuarantineReasonCode, expected.QuarantineReasonCode, StringComparison.Ordinal)
            && actual.TargetActivationProvenance == expected.TargetActivationProvenance;
    }

    /// <summary>
    /// Validates and writes a journal entry atomically to disk.
    /// Overwrites existing journal state via atomic replacement.
    /// Ownership-conditional creation and transitions use
    /// <see cref="CreateIfAbsentAsync"/> and <see cref="ReplaceIfUnchangedAsync"/> instead.
    /// </summary>
    public async Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry);

        var normalized = NormalizeEntry(entry);

        string json = JsonSerializer.Serialize(normalized, SerializerOptions);
        await _fileWriter.WriteAtomicAsync(_journalFilePath, json, cancellationToken).ConfigureAwait(false);
    }

    internal Func<Task>? AfterOwnedDeleteHookAsync { get; set; }
    internal Func<Task>? AfterPublishHookAsync { get; set; }
    internal Func<Task>? BeforeCreatePublishHookAsync { get; set; }

    /// <summary>
    /// Creates the journal only while the canonical path is absent at the atomic commit
    /// boundary: the serialized entry is written to a durable same-directory temporary file
    /// (write-through, flushed to disk) and published with a non-replacing rename, which the
    /// filesystem fails atomically when the destination name exists. A foreign journal or
    /// corrupt bytes appearing after admission can therefore never be overwritten; their
    /// bytes are preserved and an AlreadyExists outcome is reported. An ambiguous publish
    /// failure is classified by reading the canonical path back before any ownership is
    /// reported.
    /// </summary>
    public async Task<SwitchJournalWriteResult> CreateIfAbsentAsync(
        SwitchJournalEntry nextEntry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(nextEntry);

        SwitchJournalEntry normalized;
        try
        {
            ValidateEntry(nextEntry);
        }
        catch (ArgumentException ex)
        {
            return SwitchJournalWriteResult.PersistenceFailure(ex.Message, ex);
        }

        normalized = NormalizeEntry(nextEntry);

        string? tempPath = null;
        try
        {
            tempPath = await DurableTempFile.WriteAsync(
                _journalFilePath,
                JsonSerializer.Serialize(normalized, SerializerOptions),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (BeforeCreatePublishHookAsync != null)
                    await BeforeCreatePublishHookAsync().ConfigureAwait(false);
                File.Move(tempPath, _journalFilePath, overwrite: false);
                tempPath = null;
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // The rename either landed or failed atomically; classify by reading the
                // canonical path instead of guessing (ambiguous-persistence rule).
                return await ClassifyPublishOutcomeAsync(
                    normalized, expectedEntry: null, moveError, cancellationToken).ConfigureAwait(false);
            }

            if (AfterPublishHookAsync != null)
            {
                try
                {
                    await AfterPublishHookAsync().ConfigureAwait(false);
                }
                catch (Exception hookError)
                {
                    return await ClassifyPublishOutcomeAsync(
                        normalized, expectedEntry: null, hookError, cancellationToken).ConfigureAwait(false);
                }
            }

            return SwitchJournalWriteResult.Created(normalized);
        }
        finally
        {
            DurableTempFile.TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Replaces the caller's owned journal entry only while the canonical journal still
    /// represents exactly that entry, composing the two audited ownership-checked
    /// primitives: the exact-entry conditional delete (comparison and disposition through
    /// one handle whose share mode still permits external rename/delete — deletion is
    /// authorized by the exact-entry comparison, and external interference survives as
    /// durable evidence) followed by the create-if-absent publish. The successor's durable
    /// temporary file is written and flushed BEFORE the predecessor is removed, so the
    /// journal-free window shrinks to the single publish rename. If any actor seizes the
    /// freed name in the inter-primitive gap, the publish fails and the occupying bytes
    /// survive untouched: no sequence of interleavings can overwrite a foreign or
    /// replacement journal. An ambiguous publish outcome is classified by reading the
    /// canonical path back. Callers hold the transition marker durable across this
    /// operation, so even the residual rename window leaves recovery evidence on disk.
    /// </summary>
    public async Task<SwitchJournalWriteResult> ReplaceIfUnchangedAsync(
        SwitchJournalEntry expectedEntry,
        SwitchJournalEntry nextEntry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(expectedEntry);
        ArgumentNullException.ThrowIfNull(nextEntry);

        SwitchJournalEntry normalizedNext;
        try
        {
            ValidateEntry(nextEntry);
        }
        catch (ArgumentException ex)
        {
            return SwitchJournalWriteResult.PersistenceFailure(ex.Message, ex);
        }

        normalizedNext = NormalizeEntry(nextEntry);

        if (!IsWindowsPlatform())
        {
            return SwitchJournalWriteResult.UnsupportedPlatform(
                "Ownership-conditional journal transitions are only supported on Windows.");
        }

        string? tempPath = null;
        try
        {
            // Prepare the successor's flushed bytes before the predecessor is removed; the
            // durable transition marker additionally covers this window end to end.
            tempPath = await DurableTempFile.WriteAsync(
                _journalFilePath,
                JsonSerializer.Serialize(normalizedNext, SerializerOptions),
                cancellationToken).ConfigureAwait(false);

            var delete = await DeleteIfUnchangedAsync(expectedEntry, cancellationToken).ConfigureAwait(false);
            if (delete.Status != SwitchJournalDeleteStatus.Deleted)
            {
                return delete.Status switch
                {
                    SwitchJournalDeleteStatus.NotMatched => SwitchJournalWriteResult.NotMatched(
                        "The canonical journal no longer matches the entry owned by this transaction; its current bytes were preserved."),
                    SwitchJournalDeleteStatus.Absent => SwitchJournalWriteResult.Absent(
                        "The owned journal entry is no longer present at the canonical path."),
                    SwitchJournalDeleteStatus.UnsupportedPlatform => SwitchJournalWriteResult.UnsupportedPlatform(
                        "Conditional journal deletion is not supported on this platform."),
                    _ => SwitchJournalWriteResult.PersistenceFailure(
                        $"Failed to remove the owned journal entry: {delete.Message}", delete.Exception),
                };
            }

            if (AfterOwnedDeleteHookAsync != null)
            {
                try
                {
                    await AfterOwnedDeleteHookAsync().ConfigureAwait(false);
                }
                catch (Exception hookError)
                {
                    return SwitchJournalWriteResult.PersistenceFailure(
                        "Post-delete hook failed after the owned entry was removed.", hookError);
                }
            }

            try
            {
                File.Move(tempPath, _journalFilePath, overwrite: false);
                tempPath = null;
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // The rename either landed or failed atomically; classify by reading the
                // canonical path instead of guessing (ambiguous-persistence rule).
                return await ClassifyReplaceOutcomeAsync(
                    normalizedNext, expectedEntry, moveError, cancellationToken).ConfigureAwait(false);
            }

            if (AfterPublishHookAsync != null)
            {
                try
                {
                    await AfterPublishHookAsync().ConfigureAwait(false);
                }
                catch (Exception hookError)
                {
                    return await ClassifyReplaceOutcomeAsync(
                        normalizedNext, expectedEntry, hookError, cancellationToken).ConfigureAwait(false);
                }
            }

            return SwitchJournalWriteResult.Replaced(normalizedNext);
        }
        finally
        {
            DurableTempFile.TryDelete(tempPath);
        }
    }

    private async Task<SwitchJournalWriteResult> ClassifyPublishOutcomeAsync(
        SwitchJournalEntry normalizedNext,
        SwitchJournalEntry? expectedEntry,
        Exception failure,
        CancellationToken cancellationToken)
    {
        SwitchJournalReadResult readback;
        try
        {
            readback = await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception readError)
        {
            return SwitchJournalWriteResult.PersistenceFailure(
                $"Journal publish outcome could not be classified: {failure.Message}; readback failed: {readError.Message}",
                new AggregateException(failure, readError));
        }

        if (readback.Status == SwitchJournalReadStatus.Valid && readback.Entry != null)
        {
            if (EntriesMatch(readback.Entry, normalizedNext))
            {
                // The publish landed despite the reported failure: ownership is provable.
                return SwitchJournalWriteResult.Created(readback.Entry);
            }

            if (expectedEntry != null && EntriesMatch(readback.Entry, expectedEntry))
            {
                // The canonical journal still holds the expected entry: ownership is
                // unchanged and the transition simply did not happen.
                return SwitchJournalWriteResult.PersistenceFailure(
                    $"Journal publish failed and the expected entry remains: {failure.Message}", failure);
            }

            return SwitchJournalWriteResult.AlreadyExists(
                "A different journal occupies the canonical path; it was preserved untouched.");
        }

        if (readback.Status is SwitchJournalReadStatus.Corrupt or SwitchJournalReadStatus.UnsupportedVersion)
        {
            // The canonical name is occupied by unreadable bytes; they are preserved and the
            // outcome reports an occupied path, never an overwrite.
            return SwitchJournalWriteResult.AlreadyExists(
                "The canonical path is occupied by unreadable journal bytes; they were preserved untouched.");
        }

        if (readback.Status == SwitchJournalReadStatus.Absent)
        {
            return SwitchJournalWriteResult.PersistenceFailure(
                $"Journal publish failed and no journal is present: {failure.Message}", failure);
        }

        return SwitchJournalWriteResult.PersistenceFailure(
            $"Journal publish outcome is uncertain: {failure.Message}; readback status {readback.Status}.",
            failure);
    }

    /// <summary>
    /// Maps the shared publish-outcome classification onto the replace contract: a proven
    /// next entry reports Replaced (ownership recovered), an occupied canonical path
    /// reports NotMatched with the occupant preserved, and every other classification is
    /// passed through unchanged.
    /// </summary>
    private async Task<SwitchJournalWriteResult> ClassifyReplaceOutcomeAsync(
        SwitchJournalEntry normalizedNext,
        SwitchJournalEntry? expectedEntry,
        Exception failure,
        CancellationToken cancellationToken)
    {
        var classified = await ClassifyPublishOutcomeAsync(
            normalizedNext, expectedEntry, failure, cancellationToken).ConfigureAwait(false);
        return classified.Status switch
        {
            SwitchJournalWriteStatus.Created when classified.Entry != null =>
                SwitchJournalWriteResult.Replaced(classified.Entry),
            SwitchJournalWriteStatus.AlreadyExists => SwitchJournalWriteResult.NotMatched(
                "A replacement journal occupied the canonical path during the transition; it was preserved untouched."),
            _ => classified,
        };
    }

    private static void ValidateEntry(SwitchJournalEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TransactionId))
        {
            throw new ArgumentException("TransactionId cannot be null or whitespace.", nameof(entry));
        }

        if (!Guid.TryParse(entry.TransactionId, out _))
        {
            throw new ArgumentException($"TransactionId '{entry.TransactionId}' must be a valid UUID format.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.SourceAccountId))
        {
            throw new ArgumentException("SourceAccountId cannot be null or whitespace.", nameof(entry));
        }

        if (string.IsNullOrWhiteSpace(entry.TargetAccountId))
        {
            throw new ArgumentException("TargetAccountId cannot be null or whitespace.", nameof(entry));
        }

        if (!Enum.IsDefined(entry.State))
        {
            throw new ArgumentException($"State '{entry.State}' is not a defined SwitchJournalState.", nameof(entry));
        }
    }

    private static SwitchJournalEntry NormalizeEntry(SwitchJournalEntry entry) =>
        entry with
        {
            Magic = Magic,
            SchemaVersion = CurrentSchemaVersion,
            UpdatedAt = entry.UpdatedAt == default ? DateTimeOffset.UtcNow : entry.UpdatedAt
        };

    /// <summary>
    /// Deletes the journal file if it exists. Idempotent if absent.
    /// Allows I/O and permission exceptions to propagate.
    /// </summary>
    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (File.Exists(_journalFilePath))
            {
                File.Delete(_journalFilePath);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Directory does not exist, so file does not exist; cleanly idempotent.
        }

        return Task.CompletedTask;
    }

    // Transition marker persistence beside the canonical journal. The marker must exist
    // throughout every owned journal transition; recovery and admission treat any other
    // marker state as blocking evidence. Delegated to the sibling marker store so both
    // files share one path derivation and fault-injection seams.

    /// <summary>Reads and classifies the transition marker beside this journal.</summary>
    public Task<SwitchTransitionMarkerReadResult> ReadTransitionMarkerAsync(CancellationToken cancellationToken = default) =>
        _markerStore.ReadTransitionMarkerAsync(cancellationToken);

    /// <summary>
    /// Creates the transition marker only while the marker path is absent at the atomic
    /// commit boundary; an existing marker is never overwritten.
    /// </summary>
    public Task<SwitchTransitionMarkerWriteResult> CreateTransitionMarkerIfAbsentAsync(
        SwitchTransitionMarkerEntry marker, CancellationToken cancellationToken = default) =>
        _markerStore.CreateTransitionMarkerIfAbsentAsync(marker, cancellationToken);

    /// <summary>
    /// Deletes only the exact marker entry the caller owns; a replacement or corrupt
    /// marker survives untouched.
    /// </summary>
    public Task<SwitchTransitionMarkerDeleteResult> DeleteTransitionMarkerIfUnchangedAsync(
        SwitchTransitionMarkerEntry expectedMarker, CancellationToken cancellationToken = default) =>
        _markerStore.DeleteTransitionMarkerIfUnchangedAsync(expectedMarker, cancellationToken);

    private sealed class ExactJournalGuard : IExactJournalGuard
    {
        private readonly FileStream _stream;
        private bool _disposed;

        public SwitchJournalEntry GuardedEntry { get; }

        public ExactJournalGuard(FileStream stream, SwitchJournalEntry guardedEntry)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            GuardedEntry = guardedEntry ?? throw new ArgumentNullException(nameof(guardedEntry));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _stream.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Proves and holds the exact canonical journal file open under exclusive sharing
    /// (FileShare.Read) across marker mutations, ensuring that external deletion,
    /// renaming, or modification of the canonical evidence is prevented.
    /// </summary>
    public async Task<ExactJournalGuardResult> AcquireExactJournalGuardAsync(
        SwitchJournalEntry expectedEntry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedEntry);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsWindowsPlatform())
        {
            return ExactJournalGuardResult.UnsupportedPlatform(
                "Exact canonical journal companion guard is only supported on Windows.");
        }

        if (BeforeGuardAcquireHookAsync != null)
        {
            await BeforeGuardAcquireHookAsync().ConfigureAwait(false);
        }

        FileStream stream;
        try
        {
            if (!File.Exists(_journalFilePath))
            {
                return ExactJournalGuardResult.Absent("Canonical journal file is absent.");
            }

            stream = new FileStream(
                _journalFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            return ExactJournalGuardResult.Absent("Canonical journal file is absent.");
        }
        catch (DirectoryNotFoundException)
        {
            return ExactJournalGuardResult.Absent("Canonical journal directory is absent.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ExactJournalGuardResult.IoError(ex);
        }

        try
        {
            if (stream.Length == 0)
            {
                stream.Dispose();
                return ExactJournalGuardResult.Corrupt("Canonical journal file is empty (zero bytes).");
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            var parseResult = ParseEntryFromText(text);
            if (parseResult.Status != SwitchJournalReadStatus.Valid || parseResult.Entry == null)
            {
                stream.Dispose();
                return ExactJournalGuardResult.Corrupt(
                    parseResult.ErrorMessage ?? "Canonical journal file is not a valid journal entry.");
            }

            if (!EntriesMatch(parseResult.Entry, expectedEntry))
            {
                stream.Dispose();
                return ExactJournalGuardResult.NotMatched(
                    "Canonical journal content does not match the expected canonical entry.");
            }

            if (AfterGuardAcquireHookAsync != null)
            {
                await AfterGuardAcquireHookAsync().ConfigureAwait(false);
            }

            return ExactJournalGuardResult.Acquired(new ExactJournalGuard(stream, parseResult.Entry));
        }
        catch (Exception ex)
        {
            stream.Dispose();
            if (ex is OperationCanceledException)
            {
                throw;
            }
            return ExactJournalGuardResult.IoError(ex);
        }
    }

    /// <summary>
    /// Proves and guards the exact canonical companion before conditionally removing the
    /// transition marker, and holds the canonical file protected for the entire marker
    /// deletion operation. If the canonical companion cannot be proven and guarded, the
    /// marker is preserved untouched.
    /// </summary>
    public async Task<GuardedMarkerDeleteResult> DeleteTransitionMarkerWhileCanonicalGuardedAsync(
        SwitchJournalEntry expectedCanonical,
        SwitchTransitionMarkerEntry expectedMarker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedCanonical);
        ArgumentNullException.ThrowIfNull(expectedMarker);
        cancellationToken.ThrowIfCancellationRequested();

        var guardResult = await AcquireExactJournalGuardAsync(expectedCanonical, cancellationToken).ConfigureAwait(false);
        if (guardResult.Status != ExactJournalGuardStatus.Acquired || guardResult.Guard == null)
        {
            return guardResult.Status switch
            {
                ExactJournalGuardStatus.Absent => GuardedMarkerDeleteResult.CanonicalAbsent(guardResult.Message),
                ExactJournalGuardStatus.NotMatched => GuardedMarkerDeleteResult.CanonicalMismatch(guardResult.Message),
                ExactJournalGuardStatus.Corrupt => GuardedMarkerDeleteResult.CanonicalCorrupt(guardResult.Message, guardResult.Exception),
                ExactJournalGuardStatus.UnsupportedPlatform => GuardedMarkerDeleteResult.UnsupportedPlatform(guardResult.Message),
                _ => GuardedMarkerDeleteResult.PersistenceFailure(guardResult.Message ?? "Failed to acquire canonical companion guard.", guardResult.Exception)
            };
        }

        using (guardResult.Guard)
        {
            var markerDelete = await DeleteTransitionMarkerIfUnchangedAsync(expectedMarker, cancellationToken).ConfigureAwait(false);
            return markerDelete.Status switch
            {
                SwitchTransitionMarkerDeleteStatus.Deleted => GuardedMarkerDeleteResult.Deleted(),
                SwitchTransitionMarkerDeleteStatus.Absent => GuardedMarkerDeleteResult.MarkerAbsentUnexpected(
                    markerDelete.Message ?? "Transition marker was absent when conditional deletion was attempted."),
                SwitchTransitionMarkerDeleteStatus.NotMatched => GuardedMarkerDeleteResult.MarkerMismatch(
                    markerDelete.Message ?? "Transition marker contents do not match expected entry."),
                SwitchTransitionMarkerDeleteStatus.UnsupportedPlatform => GuardedMarkerDeleteResult.UnsupportedPlatform(
                    markerDelete.Message ?? "Conditional marker deletion is not supported on this platform."),
                _ => GuardedMarkerDeleteResult.PersistenceFailure(
                    markerDelete.Message ?? "Marker deletion under canonical guard failed.", markerDelete.Exception)
            };
        }
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
