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
/// Durable three-state record of whether the target credential/activation boundary may have
/// been crossed for the journaled transaction. Persisted so crash recovery can distinguish a
/// source-only failure (target evidence still trustworthy) from an uncertain target mutation
/// (cached target evidence must be rejected). Serialized as canonical exact uppercase strings.
/// UNKNOWN is the zero value and the conservative default: a missing or ambiguous record must
/// never deserialize as NOT_ATTEMPTED.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SwitchTargetActivationProvenance>))]
public enum SwitchTargetActivationProvenance
{
    UNKNOWN,
    NOT_ATTEMPTED,
    MAY_HAVE_BEEN_ATTEMPTED
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

    /// <summary>
    /// Activation provenance for this transaction. Defaults to UNKNOWN so a missing field
    /// (legacy journal) is treated conservatively by recovery rather than as NOT_ATTEMPTED.
    /// New journals persist NOT_ATTEMPTED at creation and MAY_HAVE_BEEN_ATTEMPTED before the
    /// target credential writer is invoked; the value is monotonic and never regresses.
    /// </summary>
    [JsonPropertyName("targetActivationProvenance")]
    [JsonPropertyOrder(9)]
    public SwitchTargetActivationProvenance TargetActivationProvenance { get; init; }
        = SwitchTargetActivationProvenance.UNKNOWN;
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
/// Status classification for conditional deletion of the durable switch transaction journal file.
/// </summary>
public enum SwitchJournalDeleteStatus
{
    Deleted,
    NotMatched,
    Absent,
    IoError,
    UnsupportedPlatform
}

/// <summary>
/// Result of conditional deletion of the durable switch transaction journal file.
/// </summary>
public sealed record SwitchJournalDeleteResult(
    SwitchJournalDeleteStatus Status,
    string? Message = null,
    Exception? Exception = null)
{
    public static SwitchJournalDeleteResult Deleted() => new(SwitchJournalDeleteStatus.Deleted);
    public static SwitchJournalDeleteResult NotMatched(string message) => new(SwitchJournalDeleteStatus.NotMatched, Message: message);
    public static SwitchJournalDeleteResult Absent() => new(SwitchJournalDeleteStatus.Absent);
    public static SwitchJournalDeleteResult IoError(Exception ex) => new(SwitchJournalDeleteStatus.IoError, Message: ex?.Message, Exception: ex);
    public static SwitchJournalDeleteResult UnsupportedPlatform(string message) => new(SwitchJournalDeleteStatus.UnsupportedPlatform, Message: message);
}

/// <summary>
/// Status classification for ownership-conditional journal writes.
/// Created/Replaced are the only transactionally successful outcomes authorizing forward switching. In CleanupIncomplete,
/// the successor is physically published on disk (DurableSuccessor) but marker cleanup was uncertain or failed,
/// so Entry is withheld (null) for compensation-only rollback; other non-success statuses represent absent, unproven, or failed publication that does not authorize forward progress.
/// </summary>
public enum SwitchJournalWriteStatus
{
    Created,
    Replaced,
    AlreadyExists,
    NotMatched,
    Absent,
    PersistenceFailure,
    UnsupportedPlatform,
    CleanupIncomplete
}

/// <summary>
/// Result of an ownership-conditional journal write. Foreign canonical occupants are never
/// overwritten. Distinguishes physical durability from transaction success: Entry is non-null only upon transaction success
/// (Created or Replaced), authorizing forward mutations. When marker cleanup is uncertain or failed (CleanupIncomplete),
/// Entry is withheld (null) to block forward progress, while DurableSuccessor captures the physically persisted on-disk
/// successor for compensation-only rollback and crash recovery. Admission and routing remain strictly blocked until reconciled.
/// </summary>
public sealed record SwitchJournalWriteResult(
    SwitchJournalWriteStatus Status,
    SwitchJournalEntry? Entry = null,
    string? Message = null,
    Exception? Exception = null,
    SwitchJournalEntry? DurableSuccessor = null)
{
    public static SwitchJournalWriteResult Created(SwitchJournalEntry entry) =>
        new(SwitchJournalWriteStatus.Created, Entry: entry ?? throw new ArgumentNullException(nameof(entry)), DurableSuccessor: entry);
    public static SwitchJournalWriteResult Replaced(SwitchJournalEntry entry) =>
        new(SwitchJournalWriteStatus.Replaced, Entry: entry ?? throw new ArgumentNullException(nameof(entry)), DurableSuccessor: entry);
    public static SwitchJournalWriteResult AlreadyExists(string message) => new(SwitchJournalWriteStatus.AlreadyExists, Message: message);
    public static SwitchJournalWriteResult NotMatched(string message) => new(SwitchJournalWriteStatus.NotMatched, Message: message);
    public static SwitchJournalWriteResult Absent(string message) => new(SwitchJournalWriteStatus.Absent, Message: message);
    public static SwitchJournalWriteResult PersistenceFailure(string message, Exception? exception = null) =>
        new(SwitchJournalWriteStatus.PersistenceFailure, Message: message, Exception: exception);
    public static SwitchJournalWriteResult UnsupportedPlatform(string message) => new(SwitchJournalWriteStatus.UnsupportedPlatform, Message: message);
    public static SwitchJournalWriteResult CleanupIncomplete(SwitchJournalEntry durableSuccessor, string message, Exception? exception = null) =>
        new(SwitchJournalWriteStatus.CleanupIncomplete, Entry: null, Message: message, Exception: exception, DurableSuccessor: durableSuccessor ?? throw new ArgumentNullException(nameof(durableSuccessor)));
}

/// <summary>
/// Status classification for acquiring an exact canonical journal companion guard.
/// </summary>
public enum ExactJournalGuardStatus
{
    Acquired,
    Absent,
    NotMatched,
    Corrupt,
    IoError,
    UnsupportedPlatform
}

/// <summary>
/// Disposable handle holding the exact canonical journal file open with FileShare.Read,
/// which excludes write and delete sharing and prevents external deletion, renaming, or modification.
/// </summary>
public interface IExactJournalGuard : IDisposable, IAsyncDisposable
{
    SwitchJournalEntry GuardedEntry { get; }
}

/// <summary>
/// Result of acquiring an exact canonical journal companion guard.
/// </summary>
public sealed record ExactJournalGuardResult(
    ExactJournalGuardStatus Status,
    IExactJournalGuard? Guard = null,
    string? Message = null,
    Exception? Exception = null)
{
    public static ExactJournalGuardResult Acquired(IExactJournalGuard guard) =>
        new(ExactJournalGuardStatus.Acquired, Guard: guard ?? throw new ArgumentNullException(nameof(guard)));

    public static ExactJournalGuardResult Absent(string? message = null) =>
        new(ExactJournalGuardStatus.Absent, Message: message ?? "Canonical journal file is absent.");

    public static ExactJournalGuardResult NotMatched(string? message = null) =>
        new(ExactJournalGuardStatus.NotMatched, Message: message ?? "Canonical journal content does not match the expected entry.");

    public static ExactJournalGuardResult Corrupt(string? message = null, Exception? exception = null) =>
        new(ExactJournalGuardStatus.Corrupt, Message: message ?? "Canonical journal content is corrupt.", Exception: exception);

    public static ExactJournalGuardResult IoError(Exception exception) =>
        new(ExactJournalGuardStatus.IoError, Message: exception?.Message, Exception: exception ?? throw new ArgumentNullException(nameof(exception)));

    public static ExactJournalGuardResult UnsupportedPlatform(string? message = null) =>
        new(ExactJournalGuardStatus.UnsupportedPlatform, Message: message ?? "Exact journal guard is not supported on this platform.");
}

/// <summary>
/// Status classification for conditional marker deletion under an exact canonical companion guard.
/// </summary>
public enum GuardedMarkerDeleteStatus
{
    Deleted,
    CanonicalAbsent,
    CanonicalMismatch,
    CanonicalCorrupt,
    MarkerAbsentUnexpected,
    MarkerMismatch,
    PersistenceFailure,
    UnsupportedPlatform
}

/// <summary>
/// Result of conditional marker deletion under an exact canonical companion guard.
/// </summary>
public sealed record GuardedMarkerDeleteResult(
    GuardedMarkerDeleteStatus Status,
    string? Message = null,
    Exception? Exception = null)
{
    public static GuardedMarkerDeleteResult Deleted() =>
        new(GuardedMarkerDeleteStatus.Deleted);

    public static GuardedMarkerDeleteResult CanonicalAbsent(string? message = null) =>
        new(GuardedMarkerDeleteStatus.CanonicalAbsent, Message: message ?? "Canonical companion journal is absent.");

    public static GuardedMarkerDeleteResult CanonicalMismatch(string? message = null) =>
        new(GuardedMarkerDeleteStatus.CanonicalMismatch, Message: message ?? "Canonical companion journal does not match expected entry.");

    public static GuardedMarkerDeleteResult CanonicalCorrupt(string? message = null, Exception? exception = null) =>
        new(GuardedMarkerDeleteStatus.CanonicalCorrupt, Message: message ?? "Canonical companion journal is corrupt.", Exception: exception);

    public static GuardedMarkerDeleteResult MarkerAbsentUnexpected(string? message = null) =>
        new(GuardedMarkerDeleteStatus.MarkerAbsentUnexpected, Message: message ?? "Transition marker is absent unexpectedly.");

    public static GuardedMarkerDeleteResult MarkerMismatch(string? message = null) =>
        new(GuardedMarkerDeleteStatus.MarkerMismatch, Message: message ?? "Transition marker does not match expected marker.");

    public static GuardedMarkerDeleteResult PersistenceFailure(string? message = null, Exception? exception = null) =>
        new(GuardedMarkerDeleteStatus.PersistenceFailure, Message: message ?? "Marker deletion under canonical guard failed.", Exception: exception);

    public static GuardedMarkerDeleteResult UnsupportedPlatform(string? message = null) =>
        new(GuardedMarkerDeleteStatus.UnsupportedPlatform, Message: message ?? "Guarded marker deletion is not supported on this platform.");
}

/// <summary>
/// Contract for switch journal persistence, including the durable transition marker that
/// must exist beside the canonical journal throughout every owned state transition.
/// </summary>
public interface ISwitchJournalStore : ISwitchTransitionMarkerStore
{
    public const string Magic = SwitchJournalConstants.Magic;
    public const int CurrentSchemaVersion = SwitchJournalConstants.CurrentSchemaVersion;

    string JournalFilePath { get; }

    Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default);
    Task DeleteAsync(CancellationToken cancellationToken = default);
    Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(SwitchJournalEntry expectedEntry, CancellationToken cancellationToken = default);

    // Ownership-conditional writes: the first journal may be created only while the
    // canonical path is absent at the atomic commit boundary, and a state transition may
    // replace only the exact entry the caller currently owns. Failed transitions preserve
    // foreign occupants; the coordinator retains marker evidence if the predecessor was
    // already deleted (ADR-006 write-side ownership).
    Task<SwitchJournalWriteResult> CreateIfAbsentAsync(SwitchJournalEntry nextEntry, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Ownership-conditional journal creation is unavailable.");

    Task<SwitchJournalWriteResult> ReplaceIfUnchangedAsync(SwitchJournalEntry expectedEntry, SwitchJournalEntry nextEntry, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Ownership-conditional journal transitions are unavailable.");

    // Exact canonical companion guard: proves and holds the exact canonical journal file
    // open under exclusive sharing (FileShare.Read) across marker mutations, ensuring that
    // external deletion, renaming, or modification of the canonical evidence is prevented
    // while a transition marker is being removed or rotated.
    Task<ExactJournalGuardResult> AcquireExactJournalGuardAsync(
        SwitchJournalEntry expectedEntry,
        CancellationToken cancellationToken = default) =>
        new SwitchJournalStore(JournalFilePath).AcquireExactJournalGuardAsync(expectedEntry, cancellationToken);

    // Guarded transition marker cleanup: proves and guards the exact canonical companion
    // before conditionally removing the transition marker, and holds the canonical file
    // protected for the entire marker deletion operation.
    Task<GuardedMarkerDeleteResult> DeleteTransitionMarkerWhileCanonicalGuardedAsync(
        SwitchJournalEntry expectedCanonical,
        SwitchTransitionMarkerEntry expectedMarker,
        CancellationToken cancellationToken = default) =>
        new SwitchJournalStore(JournalFilePath).DeleteTransitionMarkerWhileCanonicalGuardedAsync(
            expectedCanonical, expectedMarker, cancellationToken);

    // Transition-marker defaults derive the marker path from the canonical journal path so
    // every implementer gets production-faithful marker persistence beside its journal.
    // The marker must exist whenever a canonical journal transition is in flight; recovery
    // and admission treat any other marker state as blocking evidence.
    string ISwitchTransitionMarkerStore.MarkerFilePath => SwitchTransitionMarkerStore.PathForJournal(JournalFilePath);

    Task<SwitchTransitionMarkerReadResult> ISwitchTransitionMarkerStore.ReadTransitionMarkerAsync(CancellationToken cancellationToken) =>
        new SwitchTransitionMarkerStore(SwitchTransitionMarkerStore.PathForJournal(JournalFilePath))
            .ReadTransitionMarkerAsync(cancellationToken);

    Task<SwitchTransitionMarkerWriteResult> ISwitchTransitionMarkerStore.CreateTransitionMarkerIfAbsentAsync(
        SwitchTransitionMarkerEntry marker, CancellationToken cancellationToken) =>
        new SwitchTransitionMarkerStore(SwitchTransitionMarkerStore.PathForJournal(JournalFilePath))
            .CreateTransitionMarkerIfAbsentAsync(marker, cancellationToken);

    Task<SwitchTransitionMarkerDeleteResult> ISwitchTransitionMarkerStore.DeleteTransitionMarkerIfUnchangedAsync(
        SwitchTransitionMarkerEntry expectedMarker, CancellationToken cancellationToken) =>
        new SwitchTransitionMarkerStore(SwitchTransitionMarkerStore.PathForJournal(JournalFilePath))
            .DeleteTransitionMarkerIfUnchangedAsync(expectedMarker, cancellationToken);
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
