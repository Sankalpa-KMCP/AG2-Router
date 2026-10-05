using System.Text;
using System.Text.Json;
using AG2Router.AG2.Routing;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Persistence;

/// <summary>
/// File-backed durable store for per-account model quota observations.
/// Writes atomically via IDurableFileWriter and synchronizes in-process via PathLockRegistry.
/// Fails closed on corrupt data or unsupported schema versions.
/// </summary>
public sealed class DurableQuotaObservationStore : IQuotaObservationStore
{
    public const int CurrentSchemaVersion = QuotaObservationsDocument.CurrentSchemaVersion;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _pathLock;
    private readonly IDurableFileWriter _fileWriter;

    public DurableQuotaObservationStore(string? filePath = null)
        : this(filePath, new DurableFileWriter())
    {
    }

    internal DurableQuotaObservationStore(string? filePath, IDurableFileWriter? fileWriter)
    {
        _filePath = Path.GetFullPath(string.IsNullOrWhiteSpace(filePath)
            ? ResolveDefaultFilePath()
            : filePath);
        _pathLock = PathLockRegistry.Get(_filePath);
        _fileWriter = fileWriter ?? new DurableFileWriter();
    }

    public string GetFilePath() => _filePath;

    public static string ResolveDefaultFilePath(
        string? dataDirectory = null,
        string? currentDirectory = null,
        string? localApplicationData = null)
    {
        string cwd = string.IsNullOrWhiteSpace(currentDirectory)
            ? Environment.CurrentDirectory
            : currentDirectory;
        string? configured = dataDirectory ?? Environment.GetEnvironmentVariable("DATA_DIR");
        string directory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            directory = Path.GetFullPath(configured, cwd);
        }
        else
        {
            string? localRoot = localApplicationData ?? Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrWhiteSpace(localRoot))
            {
                localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            }
            directory = Path.Combine(localRoot, "AG2-Router", "data");
        }
        return Path.GetFullPath(Path.Combine(directory, "quota-observations.json"));
    }

    public async Task<IReadOnlyList<AccountModelQuotaObservation>> GetAllObservationsAsync(
        CancellationToken cancellationToken = default)
    {
        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            return items.AsReadOnly();
        }
        finally
        {
            _pathLock.Release();
        }
    }

    public async Task<IReadOnlyList<AccountModelQuotaObservation>> GetObservationsForAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return Array.Empty<AccountModelQuotaObservation>();

        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            return items.Where(o => string.Equals(o.AccountId, accountId.Trim(), StringComparison.Ordinal)).ToList().AsReadOnly();
        }
        finally
        {
            _pathLock.Release();
        }
    }

    public async Task<AccountModelQuotaObservation?> GetObservationAsync(
        string accountId,
        string modelKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(modelKey)) return null;
        string? canonicalKey = CandidateSelector.CanonicalizeModelKey(modelKey);
        if (canonicalKey == null) return null;

        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            return items.FirstOrDefault(o =>
                string.Equals(o.AccountId, accountId.Trim(), StringComparison.Ordinal) &&
                string.Equals(o.ModelKey, canonicalKey, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _pathLock.Release();
        }
    }

    public Task RecordObservationsAsync(
        string accountId,
        IEnumerable<AccountModelQuotaObservation> observations,
        CancellationToken cancellationToken = default) =>
        RecordAsync(accountId, observations, null, null, cancellationToken);

    public Task RecordCompleteSnapshotAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations,
        DateTimeOffset observedAtUtc, string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return RecordAsync(accountId, observations, observedAtUtc, source, cancellationToken);
    }

    private async Task RecordAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations,
        DateTimeOffset? completeSnapshotTime, string? completeSnapshotSource, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("AccountId cannot be null or whitespace.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(observations);

        string normalizedAccountId = accountId.Trim();
        var incomingList = new List<AccountModelQuotaObservation>();

        foreach (var obs in observations)
        {
            if (obs == null)
                throw new ArgumentNullException(nameof(observations), "Observation item cannot be null.");

            if (completeSnapshotTime.HasValue && obs.ObservedAtUtc != completeSnapshotTime.Value)
                throw new ArgumentException("Complete snapshot rows must share the snapshot observation time.", nameof(observations));

            if (string.IsNullOrWhiteSpace(obs.AccountId))
                throw new ArgumentException("Observation AccountId cannot be null or whitespace.", nameof(observations));

            if (!string.Equals(obs.AccountId.Trim(), normalizedAccountId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Observation AccountId '{obs.AccountId}' does not match target accountId '{normalizedAccountId}'.",
                    nameof(observations));
            }

            string? canonicalKey = CandidateSelector.CanonicalizeModelKey(obs.ModelKey);
            if (string.IsNullOrWhiteSpace(canonicalKey))
            {
                throw new ArgumentException("Observation ModelKey cannot be null or whitespace.", nameof(observations));
            }

            if (obs.RemainingFraction.HasValue && (!double.IsFinite(obs.RemainingFraction.Value) || obs.RemainingFraction < 0.0 || obs.RemainingFraction > 1.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(obs.RemainingFraction),
                    obs.RemainingFraction,
                    $"RemainingFraction must be a finite number between 0.0 and 1.0.");
            }

            incomingList.Add(new AccountModelQuotaObservation(
                normalizedAccountId,
                canonicalKey,
                obs.RemainingFraction,
                obs.ResetTime,
                obs.ObservedAtUtc,
                obs.Source
            ));
        }

        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease.AcquireAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var existingItems = await LoadStateAsync(cancellationToken).ConfigureAwait(false);

            if (completeSnapshotTime.HasValue)
            {
                var presentKeys = incomingList.Select(o => o.ModelKey).ToHashSet(StringComparer.Ordinal);
                incomingList.AddRange(existingItems.Where(o => o.AccountId == normalizedAccountId && !presentKeys.Contains(o.ModelKey))
                    .Select(o => new AccountModelQuotaObservation(normalizedAccountId, o.ModelKey, null, null,
                        completeSnapshotTime.Value, completeSnapshotSource!)));
            }

            var merged = new Dictionary<(string AccountId, string ModelKey), AccountModelQuotaObservation>();
            foreach (var item in existingItems)
            {
                string cKey = CandidateSelector.CanonicalizeModelKey(item.ModelKey) ?? item.ModelKey.Trim().ToLowerInvariant();
                merged[(item.AccountId, cKey)] = item;
            }

            foreach (var incoming in QuotaObservationEvidence.Consolidate(incomingList))
            {
                var key = (incoming.AccountId, incoming.ModelKey);
                if (!merged.TryGetValue(key, out var previous) || incoming.ObservedAtUtc > previous.ObservedAtUtc)
                    merged[key] = incoming;
                else if (incoming.ObservedAtUtc == previous.ObservedAtUtc)
                    merged[key] = QuotaObservationEvidence.Consolidate([previous, incoming]).Single();
            }

            var mergedList = merged.Values
                .OrderBy(o => o.AccountId, StringComparer.Ordinal)
                .ThenBy(o => o.ModelKey, StringComparer.Ordinal)
                .ToList();

            var doc = new QuotaObservationsDocument(
                schemaVersion: CurrentSchemaVersion,
                updatedAt: DateTimeOffset.UtcNow,
                observations: mergedList
            );

            string json = JsonSerializer.Serialize(doc, JsonOptions);
            await _fileWriter.WriteAtomicAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pathLock.Release();
        }
    }

    private async Task<List<AccountModelQuotaObservation>> LoadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new List<AccountModelQuotaObservation>();
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(_filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"Failed to read quota observation store file '{_filePath}'.", ex);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException($"Quota observation store '{_filePath}' is empty or truncated.");
        }

        QuotaObservationsDocument doc;
        try
        {
            doc = JsonSerializer.Deserialize<QuotaObservationsDocument>(text, JsonOptions)
                ?? throw new InvalidDataException($"Quota observation store '{_filePath}' deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Quota observation store '{_filePath}' is malformed or invalid JSON.", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or FormatException)
        {
            throw new InvalidDataException($"Quota observation store '{_filePath}' contains invalid observation data.", ex);
        }

        if (doc.SchemaVersion != 1 && doc.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported quota observation store schema version '{doc.SchemaVersion}' (expected {CurrentSchemaVersion}).");
        }

        if (doc.Observations == null)
        {
            throw new InvalidDataException($"Quota observation store '{_filePath}' is missing its observations array.");
        }

        foreach (var obs in doc.Observations)
        {
            if (obs == null)
                throw new InvalidDataException("Quota observation store contains null observation entry.");
            if (string.IsNullOrWhiteSpace(obs.AccountId))
                throw new InvalidDataException("Quota observation store contains entry with empty AccountId.");
            if (string.IsNullOrWhiteSpace(obs.ModelKey))
                throw new InvalidDataException("Quota observation store contains entry with empty ModelKey.");
            if (obs.RemainingFraction.HasValue && (!double.IsFinite(obs.RemainingFraction.Value) || obs.RemainingFraction < 0.0 || obs.RemainingFraction > 1.0))
                throw new InvalidDataException($"Quota observation store contains entry with invalid RemainingFraction: {obs.RemainingFraction}.");
            if (string.IsNullOrWhiteSpace(obs.Source))
                throw new InvalidDataException("Quota observation store contains entry with empty Source.");
        }

        return QuotaObservationEvidence.Consolidate(doc.Observations).ToList();
    }

    public async Task InvalidateIfUnchangedAsync(AccountModelQuotaObservation expected, DateTimeOffset invalidatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await _pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await CrossProcessFileLease.AcquireAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var items = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            int index = items.FindIndex(o => o.AccountId == expected.AccountId && o.ModelKey == expected.ModelKey);
            if (index < 0 || items[index] != expected) return;
            items[index] = new AccountModelQuotaObservation(expected.AccountId, expected.ModelKey, null, null,
                invalidatedAtUtc < expected.ObservedAtUtc ? expected.ObservedAtUtc : invalidatedAtUtc, "LiveTargetVerificationRejected");
            var document = new QuotaObservationsDocument(CurrentSchemaVersion, invalidatedAtUtc, items);
            await _fileWriter.WriteAtomicAsync(_filePath, JsonSerializer.Serialize(document, JsonOptions), cancellationToken).ConfigureAwait(false);
        }
        finally { _pathLock.Release(); }
    }
}
