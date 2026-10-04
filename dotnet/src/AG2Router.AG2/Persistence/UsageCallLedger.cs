using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Persistence;

/// <summary>Base exception for usage-ledger persistence failures.</summary>
public class UsageLedgerException : Exception
{
    public UsageLedgerException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// The durable usage ledger is corrupt or uses an unsupported schema. The store fails closed:
/// no existing bytes are modified and no replacement document is written.
/// </summary>
public sealed class UsageLedgerCorruptionException : UsageLedgerException
{
    public UsageLedgerCorruptionException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// A call key was re-observed with an accounting payload that conflicts with the durable
/// record. The prior record remains intact; conflicts are never resolved by overwriting,
/// merging, or picking a larger or smaller counter value.
/// </summary>
public sealed class UsageLedgerConflictException : UsageLedgerException
{
    public string CallKey { get; }

    public UsageLedgerConflictException(string callKey, string message)
        : base($"{message} CallKey: {callKey}.")
    {
        CallKey = callKey;
    }
}

/// <summary>
/// Durable, idempotent ledger of observed conversation-model usage calls, segmented into one
/// atomic JSON document per UTC observation month. Segmentation is purely a physical write-
/// amplification boundary: it bounds rewrite cost to the affected segments of a batch and is
/// never presented as provider event time, which the usage source does not provide.
/// </summary>
/// <remarks>
/// <para>
/// Persistence follows the repository's established primitives: <see cref="DurableFileWriter"/>
/// for same-volume atomic replacement, <see cref="PathLockRegistry"/> for in-process writer
/// serialization, and <see cref="CrossProcessFileLease"/> for cross-process writer exclusion.
/// </para>
/// <para>
/// <b>Concurrency contract.</b> Batch ingestion runs inside one cross-process transaction per
/// ledger directory, with a fixed acquisition order and reverse release order:
/// <list type="number">
/// <item>process-local gate: <see cref="PathLockRegistry"/> on <c>&lt;ledgerDir&gt;.ingest</c>;</item>
/// <item>global ledger lease: <see cref="CrossProcessFileLease"/> on the same path — the
/// cross-process boundary covering refresh, duplicate/conflict validation, planning, and
/// commit, so two processes can never plan the same new call key into different segments;</item>
/// <item>per-segment leases, acquired in one sorted pass (defense-in-depth for the segment
/// files; also the only cross-process protection for future segment-scoped writers);</item>
/// <item>durable writes, which ignore cancellation once planning has proven the batch coherent.</item>
/// </list>
/// Segment locks are never taken before the global lease on any path. The dedup index is
/// rebuilt inside the global-lease critical section with per-segment (last-write, length)
/// freshness validation, so durable state — never a process-local memory snapshot — is
/// authoritative for duplicate and conflict decisions; process-local state is a cache only.
/// A batch is planned in full against the loaded segments before anything is written, so a
/// conflict or invalid input aborts the whole batch without a single byte changing. Once
/// planning succeeds, the commit phase ignores cancellation so a multi-segment batch cannot be
/// torn by a caller token; a hard process crash between segment writes can still leave a
/// per-segment prefix, and every individual segment file remains internally coherent — a later
/// re-ingest of the same batch idempotently completes the missing records.
/// </para>
/// <para>
/// Queries take only per-segment path locks and read files that writers replace atomically,
/// so every query observes either the complete old or the complete new segment document.
/// </para>
/// <para>
/// Global dedup uses a lazily built call-key-to-segment index (keys only, no record copies),
/// so re-observed historical calls are recognized across segments without keeping the whole
/// ledger in memory. Duplicate insertions with identical accounting payloads are no-ops that
/// leave durable bytes stable; conflicting payloads fail closed.
/// </para>
/// </remarks>
public sealed class DurableUsageCallLedger : IUsageCallLedger
{
    private const string SegmentFileNamePrefix = "usage-calls-";
    private const string SegmentFileNameSuffix = ".json";
    private const string SegmentFileSearchPattern = "usage-calls-*.json";
    private const string IngestGateSuffix = ".ingest";

    // A reader that races a cross-process File.Replace can observe the brief ERROR_SHARING_
    // VIOLATION window; retrying briefly mirrors the CrossProcessFileLease acquisition cadence.
    private static readonly TimeSpan SegmentReadRetryDelay = TimeSpan.FromMilliseconds(25);
    private const int SegmentReadMaxAttempts = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter<UsageAccountAttributionBasis>(allowIntegerValues: false),
            new JsonStringEnumConverter<UsageTimeAttribution>(allowIntegerValues: false)
        }
    };

    private readonly string _ledgerDirectory;
    private readonly SemaphoreSlim _ingestGate;
    private readonly IDurableFileWriter _fileWriter;
    private readonly TimeProvider _timeProvider;

    // Internal fault-injection seams; production always acquires the real cross-process leases.
    internal Func<string, CancellationToken, Task<IAsyncDisposable>> AcquireIngestLeaseAsync { get; set; } =
        async (path, cancellationToken) => await CrossProcessFileLease.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
    internal Func<string, CancellationToken, Task<IAsyncDisposable>> AcquireSegmentLeaseAsync { get; set; } =
        async (path, cancellationToken) => await CrossProcessFileLease.AcquireAsync(path, cancellationToken).ConfigureAwait(false);

    private sealed record SegmentKeyCacheEntry(DateTimeOffset LastWriteUtc, long Length, List<string> Keys);

    // Guards the dedup index. Call keys are held here; records never are, so the memory
    // footprint stays bounded by the number of distinct calls, not by record content.
    private readonly object _stateLock = new();
    private Dictionary<string, string> _keyToSegment = new(StringComparer.Ordinal);
    private Dictionary<string, SegmentKeyCacheEntry> _segmentKeyCache = new(StringComparer.Ordinal);

    private string IngestLeasePath => _ledgerDirectory + IngestGateSuffix;

    public DurableUsageCallLedger(string? ledgerDirectory = null, TimeProvider? timeProvider = null)
        : this(ledgerDirectory, new DurableFileWriter(), timeProvider)
    {
    }

    internal DurableUsageCallLedger(string? ledgerDirectory, IDurableFileWriter? fileWriter, TimeProvider? timeProvider = null)
    {
        _ledgerDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(ledgerDirectory)
            ? ResolveDefaultLedgerDirectory()
            : ledgerDirectory);
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
        _timeProvider = timeProvider ?? TimeProvider.System;
        // Registry-keyed so every in-process store instance over one ledger directory shares
        // the same gate; the ".ingest" suffix cannot collide with segment file paths.
        _ingestGate = PathLockRegistry.Get(_ledgerDirectory + IngestGateSuffix);
    }

    public string LedgerDirectoryPath => _ledgerDirectory;

    /// <summary>
    /// Default ledger directory: <c>DATA_DIR</c>/usage when set, otherwise the stable per-user
    /// <c>%LOCALAPPDATA%/AG2-Router/data/usage</c> directory, mirroring the other durable stores.
    /// </summary>
    public static string ResolveDefaultLedgerDirectory(
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
        return Path.GetFullPath(Path.Combine(directory, "usage"));
    }

    public async Task<UsageIngestResult> IngestBatchAsync(
        IReadOnlyList<UsageCallRecord> calls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calls);
        if (calls.Any(static call => call is null))
            throw new ArgumentException("Batch must not contain null entries.", nameof(calls));
        if (calls.Count == 0)
            return new UsageIngestResult(InsertedCount: 0, DuplicateCount: 0);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Resolve duplicates and conflicts inside the batch itself so later planning sees
        //    at most one accounting payload per call key.
        var plannedCalls = new Dictionary<string, UsageCallRecord>(StringComparer.Ordinal);
        int duplicates = 0;
        foreach (var call in calls)
        {
            if (plannedCalls.TryGetValue(call.CallKey, out var prior))
            {
                if (!prior.AccountingEquals(call))
                    throw new UsageLedgerConflictException(call.CallKey,
                        "A single batch contained conflicting accounting payloads for one call key.");
                duplicates++;
                continue;
            }
            plannedCalls[call.CallKey] = call;
        }

        // 2. Cross-process transaction boundary. The in-process gate plus the global ledger
        //    lease make refresh, duplicate/conflict validation, planning, and commit one
        //    critical section shared by every writer process on this ledger directory.
        await _ingestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? ingestLease = null;
        try
        {
            ingestLease = await AcquireIngestLeaseAsync(IngestLeasePath, cancellationToken).ConfigureAwait(false);
            RefreshIndex(cancellationToken);
            Dictionary<string, string> indexSnapshot;
            lock (_stateLock)
            {
                indexSnapshot = new Dictionary<string, string>(_keyToSegment, StringComparer.Ordinal);
            }

            // 3. Acquire the cross-process lease of every involved segment — the target segment
            //    of each planned record plus any segment the index names as a key's owner — in
            //    one sorted pass so overlapping processes cannot acquire the same pair of
            //    leases in opposite order.
            var leaseSegments = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var call in plannedCalls.Values)
            {
                leaseSegments.Add(SegmentIdFor(call.FirstObservedAtUtc));
                if (indexSnapshot.TryGetValue(call.CallKey, out var ownerSegment))
                    leaseSegments.Add(ownerSegment);
            }

            var acquiredLeases = new List<IAsyncDisposable>(leaseSegments.Count);
            var acquiredPathLocks = new List<SemaphoreSlim>(leaseSegments.Count);
            try
            {
                foreach (var segmentId in leaseSegments)
                {
                    var lease = await AcquireSegmentLeaseAsync(SegmentPath(segmentId), cancellationToken)
                        .ConfigureAwait(false);
                    acquiredLeases.Add(lease);
                }

                // Per-segment path locks exclude in-process queries from the replacement
                // window; queries hold the same locks while reading. Acquired after the
                // leases and released before them: path locks are always innermost.
                foreach (var segmentId in leaseSegments)
                {
                    var pathLock = PathLockRegistry.Get(SegmentPath(segmentId));
                    await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    acquiredPathLocks.Add(pathLock);
                }

                // 4. Load current segment state under the held leases. With the ingest gate
                //    held, this state is authoritative for every in-process writer.
                var segmentRecords = new Dictionary<string, Dictionary<string, UsageCallRecord>>(StringComparer.Ordinal);
                foreach (var segmentId in leaseSegments)
                    segmentRecords[segmentId] = LoadSegmentRecords(SegmentPath(segmentId), segmentId);

                // 5. Plan the whole batch before writing anything. The loaded segments are the
                //    authoritative duplicate/conflict oracle; the index only expanded the
                //    lease set so those segments would be loaded here.
                var changedSegments = new SortedSet<string>(StringComparer.Ordinal);
                var insertedBySegment = new Dictionary<string, List<UsageCallRecord>>(StringComparer.Ordinal);
                int inserted = 0;
                foreach (var call in plannedCalls.Values)
                {
                    bool recognized = false;
                    foreach (var segmentId in leaseSegments)
                    {
                        if (!segmentRecords[segmentId].TryGetValue(call.CallKey, out var existing))
                            continue;

                        if (!existing.AccountingEquals(call))
                            throw new UsageLedgerConflictException(call.CallKey,
                                "A previously persisted call was re-observed with a conflicting accounting payload; the durable record was left intact.");
                        // Records never relocate between segments: the first-persisted segment
                        // stays authoritative even when the call is re-observed later.
                        duplicates++;
                        recognized = true;
                        break;
                    }
                    if (recognized)
                        continue;

                    string targetSegment = SegmentIdFor(call.FirstObservedAtUtc);
                    segmentRecords[targetSegment][call.CallKey] = call;
                    changedSegments.Add(targetSegment);
                    if (!insertedBySegment.TryGetValue(targetSegment, out var insertedCalls))
                    {
                        insertedCalls = [];
                        insertedBySegment[targetSegment] = insertedCalls;
                    }
                    insertedCalls.Add(call);
                    inserted++;
                }

                // 6. Commit. Once planning has proven the batch coherent, cancellation is not
                //    honored: records are never partially committed inside a segment, and the
                //    index is advanced after each durable segment replacement.
                if (changedSegments.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var segmentId in leaseSegments)
                    {
                        if (!changedSegments.Contains(segmentId))
                            continue;

                        var document = new UsageLedgerSegmentDocument(
                            UsageLedgerSegmentDocument.CurrentMagic,
                            UsageLedgerSegmentDocument.CurrentSchemaVersion,
                            segmentId,
                            _timeProvider.GetUtcNow(),
                            segmentRecords[segmentId].Values
                                .OrderBy(static record => record.CallKey, StringComparer.Ordinal)
                                .ToArray());
                        string json = JsonSerializer.Serialize(document, SerializerOptions);
                        await _fileWriter.WriteAtomicAsync(SegmentPath(segmentId), json, CancellationToken.None)
                            .ConfigureAwait(false);

                        var insertedCalls = insertedBySegment[segmentId];
                        var written = new FileInfo(SegmentPath(segmentId));
                        // The cache must describe the COMPLETE rewritten segment: every key the
                        // file now contains (pre-existing keys plus this batch's inserts), never
                        // only the delta — a truncated set would make the next freshness-
                        // validated refresh forget pre-existing keys and later duplicate them
                        // across segments.
                        var writtenEntry = new SegmentKeyCacheEntry(
                            new DateTimeOffset(written.LastWriteTimeUtc.Ticks, TimeSpan.Zero),
                            written.Length,
                            segmentRecords[segmentId].Keys.ToList());
                        lock (_stateLock)
                        {
                            _segmentKeyCache[segmentId] = writtenEntry;
                            foreach (var insertedCall in insertedCalls)
                                _keyToSegment[insertedCall.CallKey] = segmentId;
                        }
                    }
                }

                return new UsageIngestResult(inserted, duplicates);
            }
            finally
            {
                // Reverse acquisition order: path locks (innermost) first, then segment
                // leases, then the global ledger lease, then the process-local gate. Every
                // release is guaranteed even when a lease disposal itself fails.
                for (int i = acquiredPathLocks.Count - 1; i >= 0; i--)
                    acquiredPathLocks[i].Release();
                foreach (var lease in acquiredLeases)
                {
                    // A disposal failure does not invalidate committed state, so the captured
                    // exception is deliberately dropped; lock ownership is released unconditionally.
                    _ = await LeaseCleanup.TryDisposeAsync(lease).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Reverse acquisition order: segment leases were released in the inner finally,
            // then the global ledger lease, then the process-local gate. Every release is
            // guaranteed even when a lease disposal itself fails.
            if (ingestLease is not null)
                _ = await LeaseCleanup.TryDisposeAsync(ingestLease).ConfigureAwait(false);
            _ingestGate.Release();
        }
    }

    public async Task<IReadOnlyList<UsageCallRecord>> GetAllCallsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<UsageCallRecord>();
        foreach (var segmentId in EnumerateSegmentIds())
        {
            results.AddRange(await ReadSegmentCallsAsync(segmentId, cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    public async Task<IReadOnlyList<UsageCallRecord>> GetCallsByAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("A managed account id is required.", nameof(accountId));
        string normalized = accountId.Trim();
        return (await GetAllCallsAsync(cancellationToken).ConfigureAwait(false))
            .Where(call => string.Equals(call.AccountId, normalized, StringComparison.Ordinal))
            .ToList();
    }

    public async Task<IReadOnlyList<UsageCallRecord>> GetUnattributedCallsAsync(CancellationToken cancellationToken = default)
    {
        return (await GetAllCallsAsync(cancellationToken).ConfigureAwait(false))
            .Where(call => call.AccountAttributionBasis == UsageAccountAttributionBasis.Unattributed)
            .ToList();
    }

    public async Task<IReadOnlyList<UsageCallRecord>> GetObservationTimeCallsBetweenAsync(
        DateTimeOffset fromUtcInclusive,
        DateTimeOffset toUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        if (fromUtcInclusive.Offset != TimeSpan.Zero || toUtcExclusive.Offset != TimeSpan.Zero)
            throw new ArgumentException("Observation-time range bounds must be expressed in UTC (zero offset).");
        if (fromUtcInclusive > toUtcExclusive)
            throw new ArgumentException("The observation-time range start must not exceed its end.");
        return (await GetAllCallsAsync(cancellationToken).ConfigureAwait(false))
            .Where(call => call.TimeAttribution == UsageTimeAttribution.ObservationTime &&
                call.FirstObservedAtUtc >= fromUtcInclusive &&
                call.FirstObservedAtUtc < toUtcExclusive)
            .ToList();
    }

    public async Task<IReadOnlyList<UsageCallRecord>> GetCallsByModelAsync(string responseModelKey, CancellationToken cancellationToken = default)
    {
        string? canonical = UsageCallKeys.CanonicalizeResponseModel(responseModelKey)
            ?? throw new ArgumentException(
                "A canonical response model key is required; unknown-model calls are returned by GetAllCallsAsync.",
                nameof(responseModelKey));
        return (await GetAllCallsAsync(cancellationToken).ConfigureAwait(false))
            .Where(call => string.Equals(call.ResponseModelKey, canonical, StringComparison.Ordinal))
            .ToList();
    }

    private async Task<IReadOnlyList<UsageCallRecord>> ReadSegmentCallsAsync(string segmentId, CancellationToken cancellationToken)
    {
        string segmentPath = SegmentPath(segmentId);
        var pathLock = PathLockRegistry.Get(segmentPath);
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadSegmentRecords(segmentPath, segmentId).Values
                .OrderBy(static record => record.CallKey, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            pathLock.Release();
        }
    }

    private IEnumerable<string> EnumerateSegmentIds()
    {
        if (!Directory.Exists(_ledgerDirectory))
            return [];

        return Directory.GetFiles(_ledgerDirectory, SegmentFileSearchPattern)
            .Select(TryParseSegmentIdFromFileName)
            .Where(static segmentId => segmentId is not null)
            .Cast<string>()
            .OrderBy(static segmentId => segmentId, StringComparer.Ordinal)
            .ToList();
    }

    // Rebuilds the global key index from durable state. Called only while holding the global
    // ingest lease, so a freshness-validated index equals the ledger exactly for the whole
    // transaction: no protocol-obeying writer can mutate segments between the stat check and
    // the plan. Segments whose (last-write, length) match the cache are reused without a
    // re-parse; new, changed, or vanished segments are re-read, failing closed on corruption.
    private void RefreshIndex(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            var newIndex = new Dictionary<string, string>(StringComparer.Ordinal);
            var newCache = new Dictionary<string, SegmentKeyCacheEntry>(StringComparer.Ordinal);
            if (!Directory.Exists(_ledgerDirectory))
            {
                _keyToSegment = newIndex;
                _segmentKeyCache = newCache;
                return;
            }

            foreach (string path in Directory.GetFiles(_ledgerDirectory, SegmentFileSearchPattern))
            {
                string? segmentId = TryParseSegmentIdFromFileName(path);
                if (segmentId is null)
                    continue;

                var info = new FileInfo(path);
                long length = info.Length;
                var lastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc.Ticks, TimeSpan.Zero);
                if (_segmentKeyCache.TryGetValue(segmentId, out var cached) &&
                    cached.Length == length && cached.LastWriteUtc == lastWriteUtc)
                {
                    newCache[segmentId] = cached;
                }
                else
                {
                    var records = LoadSegmentRecords(path, segmentId);
                    newCache[segmentId] = new SegmentKeyCacheEntry(
                        lastWriteUtc, length, records.Keys.ToList());
                }

                foreach (string key in newCache[segmentId].Keys)
                {
                    if (!newIndex.TryAdd(key, segmentId))
                        throw new UsageLedgerCorruptionException(
                            $"Call key '{key}' exists in multiple usage ledger segments ('{newIndex[key]}' and '{segmentId}'); global key uniqueness is violated.");
                }
            }

            _keyToSegment = newIndex;
            _segmentKeyCache = newCache;
        }
    }

    private static string ReadSegmentText(string segmentPath)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(segmentPath);
            }
            catch (IOException ex) when (attempt < SegmentReadMaxAttempts && IsSharingViolation(ex))
            {
                Thread.Sleep(SegmentReadRetryDelay);
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) =>
        ex.HResult == unchecked((int)0x80070020) /* ERROR_SHARING_VIOLATION */;

    private Dictionary<string, UsageCallRecord> LoadSegmentRecords(string segmentPath, string expectedSegmentId)
    {
        if (!File.Exists(segmentPath))
            return new Dictionary<string, UsageCallRecord>(StringComparer.Ordinal);

        // Transient I/O failures propagate as-is so callers can distinguish them from
        // structural corruption; nothing is ever written on either path. The brief sharing-
        // violation window of a cross-process File.Replace is retried, after which the reader
        // observes either the complete old or the complete new document.
        string text = ReadSegmentText(segmentPath);

        UsageLedgerSegmentDocument document;
        try
        {
            document = JsonSerializer.Deserialize<UsageLedgerSegmentDocument>(text, SerializerOptions)
                ?? throw new UsageLedgerCorruptionException(
                    $"Usage ledger segment '{segmentPath}' deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or ArgumentOutOfRangeException)
        {
            throw new UsageLedgerCorruptionException(
                $"Usage ledger segment '{segmentPath}' is malformed or violates schema version {UsageLedgerSegmentDocument.CurrentSchemaVersion}.", ex);
        }

        if (!string.Equals(document.Magic, UsageLedgerSegmentDocument.CurrentMagic, StringComparison.Ordinal))
            throw new UsageLedgerCorruptionException(
                $"Usage ledger segment '{segmentPath}' has unsupported magic '{document.Magic}', expected '{UsageLedgerSegmentDocument.CurrentMagic}'.");
        if (document.SchemaVersion != UsageLedgerSegmentDocument.CurrentSchemaVersion)
            throw new UsageLedgerCorruptionException(
                $"Usage ledger segment '{segmentPath}' has unsupported schema version {document.SchemaVersion} (expected {UsageLedgerSegmentDocument.CurrentSchemaVersion}).");
        if (!string.Equals(document.SegmentMonth, expectedSegmentId, StringComparison.Ordinal))
            throw new UsageLedgerCorruptionException(
                $"Usage ledger segment '{segmentPath}' declares segment month '{document.SegmentMonth}' but is stored as '{expectedSegmentId}'.");

        var records = new Dictionary<string, UsageCallRecord>(StringComparer.Ordinal);
        foreach (var call in document.Calls)
        {
            if (!records.TryAdd(call.CallKey, call))
                throw new UsageLedgerCorruptionException(
                    $"Usage ledger segment '{segmentPath}' contains duplicate call key '{call.CallKey}'.");
        }
        return records;
    }

    /// <summary>Test-only invariant probe: snapshot of the freshness cache's key sets.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> SegmentKeyCacheSnapshot()
    {
        lock (_stateLock)
        {
            return _segmentKeyCache.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)pair.Value.Keys.ToList(),
                StringComparer.Ordinal);
        }
    }

    private string SegmentPath(string segmentId) =>
        Path.Combine(_ledgerDirectory, $"{SegmentFileNamePrefix}{segmentId}{SegmentFileNameSuffix}");

    private static string SegmentIdFor(DateTimeOffset firstObservedAtUtc) =>
        firstObservedAtUtc.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static string? TryParseSegmentIdFromFileName(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        if (!fileName.StartsWith(SegmentFileNamePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(SegmentFileNameSuffix, StringComparison.Ordinal))
            return null;

        string candidate = fileName[SegmentFileNamePrefix.Length..^SegmentFileNameSuffix.Length];
        if (!DateTimeOffset.TryParseExact(
                candidate, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return null;
        return candidate;
    }
}
