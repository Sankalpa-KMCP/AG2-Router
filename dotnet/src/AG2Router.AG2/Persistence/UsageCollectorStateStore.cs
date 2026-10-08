using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Persistence;

/// <summary>
/// Durable collector checkpoint: the smallest state needed for honest attribution continuity.
/// Stores only the last completed collection instant and the internal managed account ids that
/// the last verified collection cycle observed. Never stores emails, credentials, ports,
/// process paths, or conversation identifiers.
/// </summary>
public sealed record UsageCollectorCheckpoint
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; init; }

    [JsonPropertyName("lastCollectionCompletedAtUtc")]
    public DateTimeOffset? LastCollectionCompletedAtUtc { get; init; }

    /// <summary>
    /// Internal account ids verified at the end of the last collection cycle. Empty when the
    /// last cycle could not verify any account identity; attribution continuity requires a
    /// matching id here plus matching within-cycle observations.
    /// </summary>
    [JsonPropertyName("verifiedAccountIds")]
    public IReadOnlyList<string> VerifiedAccountIds { get; init; } = [];

    [JsonConstructor]
    public UsageCollectorCheckpoint(
        int schemaVersion,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? lastCollectionCompletedAtUtc,
        IReadOnlyList<string>? verifiedAccountIds)
    {
        SchemaVersion = schemaVersion;
        UpdatedAtUtc = updatedAtUtc;
        LastCollectionCompletedAtUtc = lastCollectionCompletedAtUtc;
        VerifiedAccountIds = verifiedAccountIds ?? [];
    }

    public static UsageCollectorCheckpoint Empty { get; } = new(
        CurrentSchemaVersion, default, null, []);
}

/// <summary>The collector checkpoint store is corrupt or uses an unsupported schema.</summary>
public sealed class UsageCollectorStateException : Exception
{
    public UsageCollectorStateException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// File-backed durable store for the usage collector checkpoint. Follows the repository's
/// durable-store conventions: versioned document, strict validation, fail-closed on corrupt
/// or unsupported data (existing bytes are never replaced), atomic replacement via
/// <see cref="DurableFileWriter"/> under <see cref="PathLockRegistry"/> and
/// <see cref="CrossProcessFileLease"/>.
/// </summary>
public sealed class UsageCollectorStateStore
{
    private const string StateFileName = "usage-collector-state.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _pathLock;
    private readonly IDurableFileWriter _fileWriter;
    private readonly TimeProvider _timeProvider;

    public UsageCollectorStateStore(string? ledgerDirectory = null, TimeProvider? timeProvider = null)
        : this(ledgerDirectory, new DurableFileWriter(), timeProvider)
    {
    }

    internal UsageCollectorStateStore(string? ledgerDirectory, IDurableFileWriter? fileWriter, TimeProvider? timeProvider = null)
    {
        string directory = Path.GetFullPath(string.IsNullOrWhiteSpace(ledgerDirectory)
            ? DurableUsageCallLedger.ResolveDefaultLedgerDirectory()
            : ledgerDirectory);
        _filePath = Path.Combine(directory, StateFileName);
        _pathLock = PathLockRegistry.Get(_filePath);
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string FilePath => _filePath;

    public async Task<UsageCollectorCheckpoint> GetCheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadCheckpoint();
        }
        finally
        {
            _pathLock.Release();
        }
    }

    public async Task SaveCheckpointAsync(
        DateTimeOffset? lastCollectionCompletedAtUtc,
        IReadOnlyList<string> verifiedAccountIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedAccountIds);
        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease.AcquireAsync(_filePath, cancellationToken).ConfigureAwait(false);

            // Fail closed if existing state is corrupt or unsupported: existing bytes must never be overwritten.
            _ = ReadCheckpoint();

            var checkpoint = new UsageCollectorCheckpoint(
                UsageCollectorCheckpoint.CurrentSchemaVersion,
                _timeProvider.GetUtcNow(),
                lastCollectionCompletedAtUtc,
                verifiedAccountIds.Distinct(StringComparer.Ordinal).OrderBy(static id => id, StringComparer.Ordinal).ToList());
            await _fileWriter.WriteAtomicAsync(_filePath, JsonSerializer.Serialize(checkpoint, SerializerOptions), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _pathLock.Release();
        }
    }

    private UsageCollectorCheckpoint ReadCheckpoint()
    {
        if (!File.Exists(_filePath))
            return UsageCollectorCheckpoint.Empty;

        string text = File.ReadAllText(_filePath);
        if (string.IsNullOrWhiteSpace(text))
            throw new UsageCollectorStateException($"Usage collector state '{_filePath}' is empty or truncated.");

        UsageCollectorCheckpoint checkpoint;
        try
        {
            checkpoint = JsonSerializer.Deserialize<UsageCollectorCheckpoint>(text, SerializerOptions)
                ?? throw new UsageCollectorStateException($"Usage collector state '{_filePath}' deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or ArgumentOutOfRangeException)
        {
            throw new UsageCollectorStateException(
                $"Usage collector state '{_filePath}' is malformed or violates schema version {UsageCollectorCheckpoint.CurrentSchemaVersion}.", ex);
        }

        if (checkpoint.SchemaVersion != UsageCollectorCheckpoint.CurrentSchemaVersion)
            throw new UsageCollectorStateException(
                $"Unsupported usage collector state schema version {checkpoint.SchemaVersion} (expected {UsageCollectorCheckpoint.CurrentSchemaVersion}).");

        if (checkpoint.VerifiedAccountIds.Any(static id => string.IsNullOrWhiteSpace(id)))
            throw new UsageCollectorStateException("Usage collector state contains an empty verified account id.");

        return checkpoint;
    }
}
