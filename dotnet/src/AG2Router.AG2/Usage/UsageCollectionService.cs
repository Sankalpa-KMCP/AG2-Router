using AG2Router.AG2.Discovery;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Rpc;
using AG2Router.AG2.Security;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Usage;

/// <summary>
/// Long-lived usage collection service. Each cycle enumerates every validated Antigravity
/// language-server instance, applies change gating before fetching per-cascade generator
/// metadata, maps the minimal accounting subset into durable usage records, and ingests one
/// combined batch so global dedup merges overlapping conversations across instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attribution continuity.</b> The first successful cycle in a process is the baseline: its
/// newly observed calls are <see cref="UsageTimeAttribution.HistoricalUnknown"/> and
/// <see cref="UsageAccountAttributionBasis.Unattributed"/> because nothing proves when they ran
/// or who ran them. Every later cycle is forward observation: an endpoint's newly seen calls
/// receive <see cref="UsageTimeAttribution.ObservationTime"/> and
/// <see cref="UsageAccountAttributionBasis.VerifiedObservation"/> only when the same internal
/// managed account was verified at the endpoint immediately before the fetch, immediately after
/// it, and at the end of the previous cycle (durable checkpoint). Any identity change —
/// including a router-controlled account switch or a restart gap — costs one conservative
/// cycle instead of manufacturing attribution.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Follows the corrected polling patterns: no overlapping cycles, bounded
/// disposal that drains the active cycle, no publication after disposal, every owned task
/// observed, and collector failures degraded to diagnostics instead of crashing the host.
/// </para>
/// </remarks>
public sealed class UsageCollectionService : IAsyncDisposable
{
    /// <summary>Internal default cadence; usage polling must not hammer the language servers.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    private readonly IUsageCallLedger _ledger;
    private readonly UsageCollectorStateStore _stateStore;
    private readonly UsageInstanceDiscovery _discovery;
    private readonly IUsageRpcClient _rpcClient;
    private readonly IAccountStore _accountStore;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _log;

    private readonly SemaphoreSlim _cycleSemaphore = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _lifetimeCts;
    private CancellationTokenSource? _activeCycleCts;
    private TaskCompletionSource<bool>? _activeCycleCompletion;
    private Task? _loopTask;
    private Task? _drainTask;
    private TimeSpan _interval = DefaultInterval;
    private bool _stopping;
    private bool _disposeRequested;
    private bool _resourcesDisposed;
    private UsageCollectorDiagnostics _currentDiagnostics = new();

    /// <summary>
    /// Per-observation-source continuity (F-04/C2 hardening). Continuity is scoped to one
    /// detected language-server endpoint: a newly appearing or returning endpoint always gets
    /// a conservative baseline, and one healthy instance can never grant forward observation
    /// to another. Keyed by the in-process endpoint identity; deliberately not persisted,
    /// because every collector process start re-baselines conservatively anyway.
    /// </summary>
    private sealed record EndpointContinuity(bool Established, string? LastVerifiedAccountId);
    private readonly Dictionary<string, EndpointContinuity> _continuity = new(StringComparer.Ordinal);

    // The known-key set is a cache only: ledger dedup remains the durable authority, and a
    // cold restart rebuilds it with one ledger scan.
    private HashSet<string> _knownCallKeys = new(StringComparer.Ordinal);
    private bool _knownCallKeysSeeded;
    private readonly Dictionary<string, Dictionary<string, TrajectoryChangeSignature>> _changeCursors = new(StringComparer.Ordinal);

    public UsageCollectionService(
        IUsageCallLedger ledger,
        UsageCollectorStateStore stateStore,
        UsageInstanceDiscovery discovery,
        IUsageRpcClient rpcClient,
        IAccountStore accountStore,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    private sealed record TrajectoryChangeSignature(string? Status, int? StepCount, string? LastModifiedTime);

    public UsageCollectorDiagnostics CurrentDiagnostics =>
        Volatile.Read(ref _currentDiagnostics);

    /// <summary>True when at least one endpoint has completed its baseline cycle this process.</summary>
    public bool BaselineEstablished
    {
        get
        {
            lock (_stateLock)
            {
                return _continuity.Values.Any(static continuity => continuity.Established);
            }
        }
    }

    /// <summary>Narrow test/ops seam: bounds the public disposal wait. Defaults to 10 seconds.</summary>
    internal TimeSpan DisposalWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Narrow test seam: the owned drain task, when a late cycle is being awaited.</summary>
    internal Task? DrainTaskForTest
    {
        get { lock (_stateLock) return _drainTask; }
    }

    internal bool ResourcesDisposedForTest
    {
        get { lock (_stateLock) return _resourcesDisposed; }
    }

    /// <summary>
    /// Test-only seam (production null): invoked inside the cycle's immediately-after-completion
    /// position, after the cycle has fully relinquished its resources. Lets tests prove the
    /// ownership handoff ordering deterministically.
    /// </summary>
    internal Action? AfterCompletionSignalForTest { get; set; }

    public TimeSpan Interval
    {
        get { lock (_stateLock) return _interval; }
        set
        {
            if (value <= TimeSpan.Zero) return;
            lock (_stateLock) _interval = value;
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            if (_disposeRequested || _loopTask is { IsCompleted: false })
                return;
            _stopping = false;
            _lifetimeCts?.Dispose();
            _lifetimeCts = new CancellationTokenSource();
            var token = _lifetimeCts.Token;
            _loopTask = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        TimeSpan interval;
                        lock (_stateLock) interval = _interval;
                        try
                        {
                            await Task.Delay(interval, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        await RunCollectionCycleAsync(token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Usage collection loop stopped unexpectedly: {Sanitize(ex)}");
                }
            }, token);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool>? completion;
        lock (_stateLock)
        {
            _stopping = true;
            _lifetimeCts?.Cancel();
            completion = _activeCycleCompletion;
        }
        if (completion is not null && !completion.Task.IsCompleted)
        {
            try
            {
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? drain;
        lock (_stateLock)
        {
            if (!_disposeRequested)
            {
                // 1. Close admission; 2. request cancellation — including the active cycle's
                // own source, because a direct-called cycle (no Start loop) links only to its
                // caller's token and would otherwise never observe disposal.
                _disposeRequested = true;
                _stopping = true;
                _lifetimeCts?.Cancel();
                _activeCycleCts?.Cancel();
                var loopTask = _loopTask;
                var completion = _activeCycleCompletion;
                // 3./4. If a cycle still owns the semaphore, an owned drain keeps every
                // resource it can reach alive until that ownership ends; only then are
                // resources disposed. The public wait below stays bounded either way.
                _drainTask = completion is { Task.IsCompleted: false }
                    ? Task.Run(() => DrainAfterActiveCycleAsync(loopTask, completion))
                    : null;
            }
            drain = _drainTask;
        }

        if (drain is not null)
        {
            // Bounded public wait: if the late cycle outlives it, the drain continues in the
            // background and performs the resource disposal exactly once.
            try { await drain.WaitAsync(DisposalWait).ConfigureAwait(false); }
            catch { }
        }
        else
        {
            DisposeResourcesOnce();
        }
    }

    private async Task DrainAfterActiveCycleAsync(Task? loopTask, TaskCompletionSource<bool>? completion)
    {
        if (loopTask is not null)
        {
            try { await loopTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log?.Invoke($"Usage collection loop faulted during disposal drain: {Sanitize(ex)}");
            }
        }
        if (completion is not null)
        {
            // The completion signal is set by the cycle's finally block, so the drain observes
            // eventual completion (or the caller-observed fault) no matter how late it runs.
            try { await completion.Task.ConfigureAwait(false); }
            catch (Exception ex)
            {
                _log?.Invoke($"Usage collection cycle faulted during disposal drain: {Sanitize(ex)}");
            }
        }
        DisposeResourcesOnce();
    }

    private void DisposeResourcesOnce()
    {
        lock (_stateLock)
        {
            if (_resourcesDisposed)
                return;
            _resourcesDisposed = true;
            _lifetimeCts?.Dispose();
            _lifetimeCts = null;
            _loopTask = null;
        }
        _cycleSemaphore.Dispose();
    }

    /// <summary>
    /// Runs one full collection cycle. Public so tests and the polling loop share one code path.
    /// </summary>
    public async Task<UsageCollectorDiagnostics> RunCollectionCycleAsync(CancellationToken cancellationToken = default)
    {
        if (_disposeRequested)
            throw new ObjectDisposedException(nameof(UsageCollectionService));

        // No overlapping cycles: a skipped tick is cheaper than a concurrent scan.
        if (!await _cycleSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return CurrentDiagnostics;

        CancellationTokenSource? cycleCts = null;
        TaskCompletionSource<bool>? completion = null;
        try
        {
            lock (_stateLock)
            {
                if (_stopping || _disposeRequested)
                    return CurrentDiagnostics;
                if (_lifetimeCts is not null)
                {
                    cycleCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
                }
                else
                {
                    cycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }
                _activeCycleCts = cycleCts;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeCycleCompletion = completion;
            }

            UsageCollectorDiagnostics diagnostics;
            try
            {
                diagnostics = await RunCycleCoreAsync(cycleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The fault must be observed even when the cycle's caller is the owned drain,
                // which awaits the completion signal rather than the faulting task.
                _log?.Invoke($"Usage collection cycle faulted: {Sanitize(ex)}");
                throw;
            }
            PublishDiagnostics(diagnostics);
            return diagnostics;
        }
        finally
        {
            lock (_stateLock)
            {
                _activeCycleCts = null;
                _activeCycleCompletion = null;
            }
            // Ownership handoff order is load-bearing: the cycle must completely relinquish
            // every resource the disposal drain is allowed to destroy BEFORE signaling
            // completion. Signaling earlier would let the drain dispose the semaphore while
            // this finally still owns it, making Release() throw ObjectDisposedException.
            cycleCts?.Dispose();
            _cycleSemaphore.Release();
            completion?.TrySetResult(true);
            // Test seam (production null): invoked after the ownership handoff is complete —
            // the cycle has released the semaphore and signaled completion, so the drain may
            // dispose resources. Ordering here is load-bearing (see F-04); the parked test
            // hook proves drain disposal cannot precede the cycle's Release.
            AfterCompletionSignalForTest?.Invoke();
        }
    }

    private async Task<UsageCollectorDiagnostics> RunCycleCoreAsync(CancellationToken cancellationToken)
    {
        var attemptedAtUtc = _timeProvider.GetUtcNow();
        int instancesDiscovered = 0;
        int instancesHealthy = 0;
        int trajectoriesInspected = 0;
        int trajectoriesChanged = 0;
        int callsPersisted = 0;
        int duplicateCalls = 0;
        int unattributedCalls = 0;
        int malformedEntries = 0;
        string? lastError = null;
        bool integrityAvailable = true;

        // The checkpoint is a durable diagnostics record. Continuity itself is tracked per
        // observation source in memory; unreadable checkpoint state cannot create or revoke
        // attribution, but it is surfaced as a failure so operators know persistence is broken.
        try
        {
            _ = await _stateStore.GetCheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UsageCollectorStateException ex)
        {
            lastError = "Collector continuity checkpoint is unreadable; runbook recovery is required.";
            _log?.Invoke($"Usage collector checkpoint unavailable: {Sanitize(ex)}");
        }

        bool discoverySucceeded = false;
        IReadOnlyList<UsageInstanceEndpoint> endpoints;
        try
        {
            endpoints = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Usage instance discovery failed: {Sanitize(ex)}");
            return Diagnostics(attemptedAtUtc, success: false,
                lastError: "Antigravity instance discovery failed.", integrityAvailable: true,
                discovered: 0, healthy: 0);
        }
        instancesDiscovered = endpoints.Count;
        discoverySucceeded = true;
        if (endpoints.Count == 0)
        {
            // A successful discovery proving no instances are running breaks continuity for
            // every known source; each will be re-baselined when it returns.
            lock (_stateLock)
            {
                _continuity.Clear();
                _changeCursors.Clear();
            }
            return Diagnostics(attemptedAtUtc, success: false,
                lastError: "No validated Antigravity instances are running.", integrityAvailable: true,
                discovered: 0, healthy: 0);
        }

        var endpointsEstablished = new Dictionary<string, bool>(StringComparer.Ordinal);
        var listedEndpointKeys = new List<string>();

        // Per-endpoint identity evidence, observed before any fetch.
        var endpointIdentityBefore = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            endpointIdentityBefore[EndpointKey(endpoint)] = await ObserveAccountIdentityAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        var observedAtUtc = _timeProvider.GetUtcNow();
        var perEndpointRecords = new List<(UsageInstanceEndpoint Endpoint, List<UsageCallRecord> Records)>();
        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string endpointKey = EndpointKey(endpoint);
            var endpointRecords = new List<UsageCallRecord>();
            perEndpointRecords.Add((endpoint, endpointRecords));
            try
            {
                var summaries = await _rpcClient.GetAllCascadeTrajectoriesAsync(
                    endpoint.Port, endpoint.Protocol, endpoint.CsrfToken, cancellationToken).ConfigureAwait(false);
                if (summaries?.TrajectorySummaries is null)
                {
                    continue;
                }
                instancesHealthy++;
                listedEndpointKeys.Add(endpointKey);
                var changed = SelectChangedTrajectories(endpointKey, summaries.TrajectorySummaries);
                trajectoriesInspected += summaries.TrajectorySummaries.Count;
                trajectoriesChanged += changed.Count;

                foreach (var cascadeId in changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<RawGeneratorMetadataEntry> entries;
                    try
                    {
                        entries = await _rpcClient.GetCascadeTrajectoryGeneratorMetadataAsync(
                            endpoint.Port, endpoint.Protocol, endpoint.CsrfToken, cascadeId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // One broken cascade or instance must not erase other results.
                        lastError = "One or more conversations could not be inspected.";
                        _log?.Invoke($"Usage metadata fetch failed for a conversation: {Sanitize(ex)}");
                        // The cursor must not retain a signature whose metadata was never
                        // fetched: reverting it makes the next cycle re-detect the change and
                        // retry, so a transient failure cannot permanently suppress the call.
                        RevertCursorForFailedFetch(endpointKey, cascadeId);
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        if (TryMapEntry(entry, cascadeId, observedAtUtc, out var record)) endpointRecords.Add(record);
                        else malformedEntries++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = "One Antigravity instance could not be queried.";
                _log?.Invoke($"Usage trajectory enumeration failed for one instance: {Sanitize(ex)}");
            }
        }

        // Per-endpoint identity evidence, observed after all fetches.
        var endpointIdentityAfter = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints)
        {
            endpointIdentityAfter[EndpointKey(endpoint)] = await ObserveAccountIdentityAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }

        // Attribution decision, per observation source. An endpoint's first successful cycle
        // (new instance, returning instance, or collector restart) is a conservative baseline:
        // newly seen calls are HistoricalUnknown + Unattributed. Only an endpoint with
        // established continuity whose verified account agrees immediately before and after
        // the fetch earns ObservationTime + VerifiedObservation — one healthy instance can
        // never grant that to another, and any identity change costs one conservative cycle.
        var mappedCalls = new List<UsageCallRecord>();
        foreach (var (endpoint, endpointRecords) in perEndpointRecords)
        {
            string key = EndpointKey(endpoint);
            string? identityBefore = endpointIdentityBefore[key];
            string? identityAfter = endpointIdentityAfter[key];
            lock (_stateLock)
            {
                endpointsEstablished[key] = _continuity.TryGetValue(key, out var continuity) && continuity.Established;
            }

            bool forwardVerified = endpointsEstablished[key] &&
                identityBefore is not null &&
                string.Equals(identityBefore, identityAfter, StringComparison.Ordinal) &&
                string.Equals(LastVerifiedAccountFor(key), identityBefore, StringComparison.Ordinal);

            foreach (var candidate in endpointRecords)
            {
                // Time and account attribution rest on separate evidence: a call observed
                // during continuous forward collection has an observation time even when the
                // account behind it could not be verified — but a source without established
                // continuity has neither.
                var basis = forwardVerified
                    ? UsageAccountAttributionBasis.VerifiedObservation
                    : UsageAccountAttributionBasis.Unattributed;
                if (basis == UsageAccountAttributionBasis.Unattributed)
                    unattributedCalls++;
                mappedCalls.Add(candidate with
                {
                    AccountAttributionBasis = basis,
                    AccountId = forwardVerified ? identityBefore : null,
                    TimeAttribution = endpointsEstablished[key]
                        ? UsageTimeAttribution.ObservationTime
                        : UsageTimeAttribution.HistoricalUnknown,
                    FirstObservedAtUtc = observedAtUtc,
                });
            }
        }

        // Skip calls already known to this process; the ledger remains the durable authority.
        await SeedKnownCallKeysAsync(cancellationToken).ConfigureAwait(false);
        var freshCalls = new List<UsageCallRecord>(mappedCalls.Count);
        lock (_stateLock)
        {
            foreach (var record in mappedCalls)
            {
                if (_knownCallKeys.Add(record.CallKey))
                    freshCalls.Add(record);
            }
        }

        bool cycleSucceeded = true;
        try
        {
            var ingest = await _ledger.IngestBatchAsync(freshCalls, cancellationToken).ConfigureAwait(false);
            callsPersisted = ingest.InsertedCount;
            duplicateCalls = ingest.DuplicateCount;
        }
        catch (UsageLedgerConflictException ex)
        {
            // Accounting integrity wins: conflicts are surfaced, never overwritten or retried.
            cycleSucceeded = false;
            integrityAvailable = false;
            lastError = "A usage accounting conflict was detected; the ledger was left intact.";
            _log?.Invoke($"Usage ledger conflict for a call: {Sanitize(ex)}");
        }
        catch (UsageLedgerCorruptionException ex)
        {
            cycleSucceeded = false;
            integrityAvailable = false;
            lastError = "Usage history storage is unreadable; collection is paused.";
            _log?.Invoke($"Usage ledger corruption: {Sanitize(ex)}");
        }

        // Continuity checkpoint: verified identities at cycle end (after-observations).
        var verifiedIds = endpointIdentityAfter.Values
            .Where(static id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();
        if (cycleSucceeded)
        {
            try
            {
                await _stateStore.SaveCheckpointAsync(_timeProvider.GetUtcNow(), verifiedIds, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = "Collector continuity checkpoint could not be saved; continuity itself is tracked per observation source.";
                _log?.Invoke($"Usage checkpoint save failed: {Sanitize(ex)}");
            }

            if (discoverySucceeded)
            {
                lock (_stateLock)
                {
                    // A successful discovery is authoritative: every listed endpoint with a
                    // completed scan ends its cycle with verified continuity state, and every
                    // endpoint absent from the discovery has its continuity broken (it will be
                    // re-baselined if it returns). A failed discovery changes nothing.
                    var listed = listedEndpointKeys.ToHashSet(StringComparer.Ordinal);
                    foreach (var key in listedEndpointKeys)
                    {
                        _continuity[key] = new EndpointContinuity(true, endpointIdentityAfter.GetValueOrDefault(key));
                    }
                    foreach (var staleKey in _continuity.Keys.Where(key => !listed.Contains(key)).ToList())
                    {
                        _continuity.Remove(staleKey);
                        _changeCursors.Remove(staleKey);
                    }
                }
            }
        }

        bool baselineEstablished = instancesHealthy > 0 && listedEndpointKeys.All(key =>
        {
            lock (_stateLock)
            {
                return _continuity.TryGetValue(key, out var continuity) && continuity.Established;
            }
        });

        return new UsageCollectorDiagnostics
        {
            BaselineEstablished = baselineEstablished,
            LastCollectionAttemptAtUtc = attemptedAtUtc,
            LastSuccessfulCollectionAtUtc = cycleSucceeded ? _timeProvider.GetUtcNow() : null,
            LastError = lastError,
            InstancesDiscovered = instancesDiscovered,
            InstancesHealthy = instancesHealthy,
            IntegrityAvailable = integrityAvailable,
        };

        UsageCollectorDiagnostics Diagnostics(DateTimeOffset at, bool success, string? lastError, bool integrityAvailable, int discovered, int healthy) =>
            new()
            {
                BaselineEstablished = BaselineEstablished,
                LastCollectionAttemptAtUtc = at,
                LastSuccessfulCollectionAtUtc = success ? _timeProvider.GetUtcNow() : null,
                LastError = lastError,
                InstancesDiscovered = discovered,
                InstancesHealthy = healthy,
                IntegrityAvailable = integrityAvailable,
            };
    }

    private async Task SeedKnownCallKeysAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _knownCallKeysSeeded))
            return;
        var all = await _ledger.GetAllCallsAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
        {
            foreach (var record in all)
                _knownCallKeys.Add(record.CallKey);
        }
        Volatile.Write(ref _knownCallKeysSeeded, true);
    }

    private async Task<string?> ObserveAccountIdentityAsync(UsageInstanceEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _rpcClient.GetUserStatusAsync(
                endpoint.Port, endpoint.Protocol, endpoint.CsrfToken, cancellationToken).ConfigureAwait(false);
            string? email = status?.UserStatus?.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email))
                return null;
            var account = await _accountStore.GetAccountByEmailAsync(email, cancellationToken).ConfigureAwait(false);
            return account?.Id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private List<string> SelectChangedTrajectories(
        string endpointKey, Dictionary<string, RawTrajectorySummary> summaries)
    {
        lock (_stateLock)
        {
            _changeCursors.TryGetValue(endpointKey, out var cursor);
            cursor ??= new Dictionary<string, TrajectoryChangeSignature>(StringComparer.Ordinal);
            var changed = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (summaryKey, summary) in summaries)
            {
                string cascadeId = summary.CascadeId ?? summaryKey;
                if (string.IsNullOrWhiteSpace(cascadeId) || !seen.Add(cascadeId))
                    continue;

                var signature = new TrajectoryChangeSignature(summary.Status, summary.StepCount, summary.LastModifiedTime);
                var previous = cursor.TryGetValue(cascadeId, out var priorSignature) ? priorSignature : null;
                bool hasMarker = summary.LastModifiedTime is not null && summary.StepCount is not null;
                // Correctness over RPC frugality: an ambiguous or missing change marker always
                // refetches, because a missed call is worse than an unnecessary request.
                bool signatureChanged = previous is null || !hasMarker ||
                    previous.Status != signature.Status ||
                    previous.StepCount != signature.StepCount ||
                    previous.LastModifiedTime != signature.LastModifiedTime;
                if (signatureChanged)
                    changed.Add(cascadeId);
                cursor[cascadeId] = signature;
            }

            foreach (var staleKey in cursor.Keys.Where(key => !seen.Contains(key)).ToList())
                cursor.Remove(staleKey);
            _changeCursors[endpointKey] = cursor;
            return changed;
        }
    }

    /// <summary>
    /// Removes a failed cascade's signature from the endpoint's change cursor so the next
    /// cycle re-detects it as changed and retries the metadata fetch. A cursor entry may
    /// only record a signature whose metadata fetch completed.
    /// </summary>
    private void RevertCursorForFailedFetch(string endpointKey, string cascadeId)
    {
        lock (_stateLock)
        {
            if (_changeCursors.TryGetValue(endpointKey, out var cursor))
                cursor.Remove(cascadeId);
        }
    }

    private bool TryMapEntry(
        RawGeneratorMetadataEntry entry,
        string fallbackCascadeId,
        DateTimeOffset observedAtUtc,
        out UsageCallRecord record)
    {
        record = null!;
        var usage = entry.Usage;
        if (usage is null || string.IsNullOrWhiteSpace(usage.ResponseId))
            return false;

        string cascadeId = !string.IsNullOrWhiteSpace(entry.CascadeId) ? entry.CascadeId : fallbackCascadeId;
        if (!UsageRpcPayloads.TryGetInt64Token(usage.InputTokens, out var inputTokens) ||
            !UsageRpcPayloads.TryGetInt64Token(usage.OutputTokens, out var outputTokens))
            return false;

        long? responseOutput = UsageRpcPayloads.TryGetInt64Token(usage.ResponseOutputTokens, out var responseOutputTokens) ? responseOutputTokens : null;
        long? thinkingOutput = UsageRpcPayloads.TryGetInt64Token(usage.ThinkingOutputTokens, out var thinkingOutputTokens) ? thinkingOutputTokens : null;
        long? cacheRead = UsageRpcPayloads.TryGetInt64Token(usage.CacheReadTokens, out var cacheReadTokens) ? cacheReadTokens : null;
        var model = UsageCallKeys.CanonicalizeResponseModel(entry.ChatModel?.ResponseModel);

        try
        {
            record = new UsageCallRecord(
                UsageCallKeys.Compute(cascadeId, usage.ResponseId),
                model,
                provider: null,
                inputTokens,
                outputTokens,
                responseOutput,
                thinkingOutput,
                cacheRead,
                observedAtUtc,
                UsageTimeAttribution.HistoricalUnknown,
                UsageAccountAttributionBasis.Unattributed,
                accountId: null,
                "CascadeConversationCall");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            // Negative or otherwise invalid provider counters: skip the individual call rather
            // than fabricating counters or failing the whole batch.
            return false;
        }
    }

    private string? LastVerifiedAccountFor(string endpointKey)
    {
        lock (_stateLock)
        {
            return _continuity.TryGetValue(endpointKey, out var continuity)
                ? continuity.LastVerifiedAccountId
                : null;
        }
    }

    private static string EndpointKey(UsageInstanceEndpoint endpoint) =>
        $"{endpoint.ProcessId}:{endpoint.Port}";

    private static string Sanitize(Exception ex) =>
        AG2Security.SanitizeError(ex);

    private void PublishDiagnostics(UsageCollectorDiagnostics diagnostics)
    {
        lock (_stateLock)
        {
            if (_disposeRequested)
                return;
        }
        Volatile.Write(ref _currentDiagnostics, diagnostics);
    }
}
