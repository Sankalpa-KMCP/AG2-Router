using System.Globalization;
using System.Security.Cryptography;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Security;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Switching;

public sealed class NativeAccountSwitchCoordinator : INativeAccountSwitchCoordinator
{
    private const string WinCredTarget = "gemini:antigravity";
    private const int MaxCredentialBlobBytes = 2560;
    private static readonly SemaphoreSlim SwitchGate = new(1, 1);

    private readonly IAccountStore _accountStore;
    private readonly SessionVault _sessionVault;
    private readonly IWinCredReader _winCredReader;
    private readonly IWinCredWriter _winCredWriter;
    private readonly IAG2Adapter _adapter;
    private readonly IAG2ProcessLifecycle _processLifecycle;
    private readonly TimeSpan _processTimeout;
    private readonly TimeSpan _verificationTimeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _rollbackTimeout;
    private readonly TimeSpan _transactionTimeout;
    private readonly IQuotaObservationStore? _quotaObservationStore;
    private readonly TimeProvider _timeProvider;
    private readonly RecoveryQuarantine _recoveryQuarantine;
    private readonly ISwitchJournalStore _journalStore;
    private readonly object _statusLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private volatile bool _isShuttingDown;
    internal Func<Task>? BeforeLeaseReleaseAsync { get; set; }
    // Internal fault-injection seam; production always acquires the real file lease.
    internal Func<string, CancellationToken, Task<IAsyncDisposable>> AcquireRecoveryLeaseAsync { get; set; } =
        async (resource, token) => await CrossProcessFileLease.AcquireAsync(resource, token).ConfigureAwait(false);
    internal void SetJournalRecoveryStateForTest(string state) { lock (_statusLock) { _journalRecoveryState = state; } }
    internal void SetActiveTransactionForTest(string? txId, string state = NativeSwitchStates.Idle) { lock (_statusLock) { _activeTransactionId = txId; _currentState = state; } }
    internal static SemaphoreSlim SwitchGateForTest => SwitchGate;
    internal void SetShuttingDownForTest(bool isShuttingDown) { lock (_statusLock) { _isShuttingDown = isShuttingDown; } }

    private string _currentState = NativeSwitchStates.Idle;
    private string? _activeTransactionId;
    private NativeSwitchResult? _lastResult;
    private string _journalRecoveryState = JournalRecoveryStates.None;

    public NativeAccountSwitchCoordinator(
        IAccountStore accountStore,
        SessionVault sessionVault,
        IWinCredReader winCredReader,
        IWinCredWriter winCredWriter,
        IAG2Adapter adapter,
        IAG2ProcessLifecycle processLifecycle,
        ISwitchJournalStore switchJournalStore,
        TimeSpan? processTimeout = null,
        TimeSpan? verificationTimeout = null,
        TimeSpan? pollInterval = null,
        TimeSpan? rollbackTimeout = null,
        TimeSpan? transactionTimeout = null,
        IQuotaObservationStore? quotaObservationStore = null,
        TimeProvider? timeProvider = null)
    {
        _accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        _sessionVault = sessionVault ?? throw new ArgumentNullException(nameof(sessionVault));
        _winCredReader = winCredReader ?? throw new ArgumentNullException(nameof(winCredReader));
        _winCredWriter = winCredWriter ?? throw new ArgumentNullException(nameof(winCredWriter));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _processLifecycle = processLifecycle ?? throw new ArgumentNullException(nameof(processLifecycle));
        _journalStore = switchJournalStore ?? throw new ArgumentNullException(nameof(switchJournalStore));
        _processTimeout = processTimeout ?? TimeSpan.FromSeconds(10);
        _verificationTimeout = verificationTimeout ?? TimeSpan.FromSeconds(20);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        _rollbackTimeout = rollbackTimeout ?? (_processTimeout + _verificationTimeout + TimeSpan.FromSeconds(10));
        if (_rollbackTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(rollbackTimeout));
        _transactionTimeout = transactionTimeout ??
            (_processTimeout + _verificationTimeout + _rollbackTimeout + TimeSpan.FromSeconds(30));
        if (_transactionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(transactionTimeout));
        _quotaObservationStore = quotaObservationStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _recoveryQuarantine = RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch");
    }

    public NativeSwitchStatus GetStatus()
    {
        lock (_statusLock)
        {
            if (_recoveryQuarantine.IsMarked)
            {
                string now = DateTimeOffset.UtcNow.ToString("O");
                var uncertain = _lastResult?.ManualRecoveryRequired == true
                    ? _lastResult
                    : new NativeSwitchResult(_activeTransactionId, false,
                        SwitchResultCodes.SwitchFailedRollbackFailed, NativeSwitchStates.Failed,
                        string.Empty, null, null, null,
                        "Switch outcome is unresolved; manual recovery is required.",
                        [], now, now, ManualRecoveryRequired: true);
                return new NativeSwitchStatus(
                    _activeTransactionId,
                    NativeSwitchStates.Failed,
                    uncertain,
                    QuarantineActive: true,
                    JournalRecoveryState: _journalRecoveryState);
            }
            return new NativeSwitchStatus(
                _activeTransactionId,
                _currentState,
                _lastResult,
                QuarantineActive: false,
                JournalRecoveryState: _journalRecoveryState);
        }
    }

    public async Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_statusLock) { _isShuttingDown = true; }
        _shutdownCts.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await SwitchGate.WaitAsync(cts.Token).ConfigureAwait(false);
            SwitchGate.Release();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("An account switch is still completing or recovering; shutdown was not completed.");
        }
    }

    private StartupJournalReconciliationResult RecordStartupReconciliationResult(
        StartupJournalReconciliationResult result, string recoveryState)
    {
        lock (_statusLock)
        {
            if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
            {
                _journalRecoveryState = recoveryState;
            }
        }
        return result;
    }

    /// <summary>
    /// Maps a conditional startup-cleanup deletion to its reconciliation outcome. Cleanup
    /// ownership is bound to the proved entry: a successful proof authorizes removing only
    /// that exact journal entry. Absence under recovery ownership has no provable owner and
    /// fails closed; a mismatch means a replacement was written during the proof and must
    /// survive for its own reconciliation — recovery stays blocked rather than reporting a
    /// clean result derived from the old proof. Deleting the proved entry is additionally
    /// not by itself proof of path clearance (the comparison/disposition window admits a
    /// replacement), so a Deleted outcome re-reads the canonical path and only a genuinely
    /// absent journal publishes clean.
    /// </summary>
    private async Task<StartupJournalReconciliationResult> RecordStartupConditionalCleanup(
        SwitchJournalEntry provedEntry,
        SwitchJournalDeleteResult deleteResult,
        string cleanMessage,
        SwitchTransitionMarkerEntry? cleanupMarker = null)
    {
        if (deleteResult.Status == SwitchJournalDeleteStatus.Deleted)
        {
            if (cleanupMarker != null)
            {
                var markerDelete = await _journalStore
                    .DeleteTransitionMarkerIfUnchangedAsync(cleanupMarker, CancellationToken.None)
                    .ConfigureAwait(false);
                if (markerDelete.Status != SwitchTransitionMarkerDeleteStatus.Deleted &&
                    markerDelete.Status != SwitchTransitionMarkerDeleteStatus.Absent)
                {
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                        StartupJournalReconciliationStatus.Degraded,
                        "The exact transition marker could not be removed after the journal cleanup; it must be reconciled before switching resumes.",
                        RetainedEntry: provedEntry),
                        JournalRecoveryStates.ActionRequired);
                }
            }

            SwitchJournalReadResult postCleanup;
            try
            {
                postCleanup = await _journalStore.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                postCleanup = SwitchJournalReadResult.IoError(ex);
            }

            if (postCleanup.Status == SwitchJournalReadStatus.Absent)
            {
                if (await IsTransitionMarkerAbsentAfterCleanupAsync().ConfigureAwait(false))
                {
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                        StartupJournalReconciliationStatus.Clean, cleanMessage, RetainedEntry: null),
                        JournalRecoveryStates.None);
                }

                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                    StartupJournalReconciliationStatus.Degraded,
                    "An unresolved transition marker remains after the journal cleanup; it must be reconciled before switching resumes.",
                    RetainedEntry: provedEntry),
                    JournalRecoveryStates.ActionRequired);
            }

            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                StartupJournalReconciliationStatus.Degraded,
                "A replacement or unreadable journal appeared at the canonical journal path during cleanup; it must be reconciled before switching resumes.",
                RetainedEntry: provedEntry),
                JournalRecoveryStates.ActionRequired);
        }

        return deleteResult.Status switch
        {
            SwitchJournalDeleteStatus.Absent => RecordStartupReconciliationResult(
                new StartupJournalReconciliationResult(
                    StartupJournalReconciliationStatus.Degraded,
                    "Switch journal disappeared during recovery proof; the removal could not be attributed to this recovery, so manual reconciliation is required.",
                    RetainedEntry: provedEntry),
                JournalRecoveryStates.ActionRequired),
            SwitchJournalDeleteStatus.NotMatched => RecordStartupReconciliationResult(
                new StartupJournalReconciliationResult(
                    StartupJournalReconciliationStatus.Degraded,
                    "Switch journal changed during recovery proof; cleanup is bound to the proved entry, so the current journal must be reconciled (restart or resolve) before switching resumes.",
                    RetainedEntry: provedEntry),
                JournalRecoveryStates.ActionRequired),
            SwitchJournalDeleteStatus.UnsupportedPlatform => RecordStartupReconciliationResult(
                new StartupJournalReconciliationResult(
                    StartupJournalReconciliationStatus.Degraded,
                    "Conditional switch journal deletion is not supported on this platform.",
                    RetainedEntry: provedEntry),
                JournalRecoveryStates.ActionRequired),
            _ => RecordStartupReconciliationResult(
                new StartupJournalReconciliationResult(
                    StartupJournalReconciliationStatus.Degraded,
                    $"Failed to delete the switch journal conditionally: {deleteResult.Message}",
                    RetainedEntry: provedEntry),
                JournalRecoveryStates.ActionRequired),
        };
    }

    /// <summary>
    /// Startup recovery for the absent-canonical marker gap of the RECORDED ->
    /// CREDENTIAL_APPLYING pair — the one gap that can ever publish clean. The durable
    /// marker proves a transaction was in flight and the credential writer, which is
    /// invoked strictly after the CREDENTIAL_APPLYING journal lands, was never reached;
    /// the only uncertainty is the source runtime, which the ADR-006 RECORDED proof
    /// resolves. Proven coherence authorizes removing the exact marker and publishing
    /// clean only when both recovery paths are genuinely clear; every failure retains the
    /// marker and blocks. Never called for any other pair.
    /// </summary>
    private async Task<StartupJournalReconciliationResult> ReconcileRecordedMarkerGapAsync(
        SwitchTransitionMarkerEntry marker,
        CancellationToken cancellationToken)
    {
        var durableSource = await VerifyDurableSourceCoherenceAsync(
            marker.Expected.SourceAccountId, cancellationToken).ConfigureAwait(false);
        if (!durableSource.Proven)
        {
            // The source metadata state is incoherent, which RECORDED transactions cannot
            // have caused themselves; treat the environment as uncertain and quarantine.
            _sessionVault.QuarantineUnresolvedMutation();
            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                StartupJournalReconciliationStatus.Quarantined,
                $"Transition marker retained: {durableSource.Message}", RetainedEntry: marker.Expected),
                JournalRecoveryStates.ActionRequired);
        }

        var runtime = await ProveSourceRuntimeCoherenceAsync(
            marker.Expected.SourceAccountId, durableSource.SourceEmail!, cancellationToken).ConfigureAwait(false);
        if (!runtime.Proven)
        {
            // Credentials are provably intact; only the source runtime state is unproven.
            // Retain the marker and block switching until coherence is established.
            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                StartupJournalReconciliationStatus.Degraded,
                $"Transition marker retained: {runtime.Message} Start Antigravity and resolve the journal.",
                RetainedEntry: marker.Expected),
                JournalRecoveryStates.ActionRequired);
        }

        try
        {
            var markerDelete = await _journalStore
                .DeleteTransitionMarkerIfUnchangedAsync(marker, cancellationToken)
                .ConfigureAwait(false);
            if (markerDelete.Status != SwitchTransitionMarkerDeleteStatus.Deleted)
            {
                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                    StartupJournalReconciliationStatus.Degraded,
                    "The exact transition marker could not be removed after the source-runtime proof; recovery must reconcile it before switching resumes.",
                    RetainedEntry: marker.Expected),
                    JournalRecoveryStates.ActionRequired);
            }

            var canonicalAfterCleanup = await _journalStore.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (canonicalAfterCleanup.Status == SwitchJournalReadStatus.Absent &&
                await IsTransitionMarkerAbsentAfterCleanupAsync().ConfigureAwait(false))
            {
                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                    StartupJournalReconciliationStatus.Clean,
                    "RECORDED transition marker cleaned up; source runtime proven coherent.",
                    RetainedEntry: null),
                    JournalRecoveryStates.None);
            }

            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                StartupJournalReconciliationStatus.Degraded,
                "Replacement or unresolved recovery evidence appeared during cleanup; it must be reconciled before switching resumes.",
                RetainedEntry: marker.Expected),
                JournalRecoveryStates.ActionRequired);
        }
        catch (Exception ex)
        {
            // Proven coherent underlying state. Do NOT quarantine!
            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                StartupJournalReconciliationStatus.Degraded,
                $"Failed to delete the transition marker: {ex.Message}", RetainedEntry: marker.Expected),
                JournalRecoveryStates.ActionRequired);
        }
    }

    public async Task<StartupJournalReconciliationResult> ReconcileStartupJournalAsync(CancellationToken cancellationToken = default)
    {
        await SwitchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string switchResource = _sessionVault.GetVaultPath() + ".switch";
            var switchLock = PathLockRegistry.Get(switchResource);
            if (!await switchLock.WaitAsync(_transactionTimeout, cancellationToken).ConfigureAwait(false))
            {
                _sessionVault.QuarantineUnresolvedMutation();
                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, "Lock acquisition timed out during startup reconciliation."), JournalRecoveryStates.Unknown);
            }

            IAsyncDisposable? switchLease = null;
            async Task<StartupJournalReconciliationResult> ReconcileUnderLeaseAsync()
            {
                try
                {
                    switchLease = await AcquireRecoveryLeaseAsync(switchResource, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Cross-process lease acquisition failed: {ex.Message}"), JournalRecoveryStates.Unknown);
                }

                SwitchJournalReadResult readResult;
                try
                {
                    readResult = await _journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"I/O error reading switch journal: {ex.Message}"), JournalRecoveryStates.Unknown);
                }

                // Startup classifies the canonical journal and its transition marker
                // together: throughout every journal transition at least one of the two
                // files must exist, so neither may be read in isolation.
                SwitchTransitionMarkerReadResult markerRead;
                try
                {
                    markerRead = await _journalStore.ReadTransitionMarkerAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    markerRead = SwitchTransitionMarkerReadResult.IoError(ex);
                }

                // A corrupt or unsupported marker is recovery evidence whose staleness
                // cannot be proven: fail closed and preserve it, even when the canonical
                // journal looks valid, and never delete unreadable marker bytes.
                if (markerRead.Status is SwitchTransitionMarkerReadStatus.Corrupt or SwitchTransitionMarkerReadStatus.UnsupportedVersion)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Unresolved transition marker found: {markerRead.ErrorMessage}"), JournalRecoveryStates.NotResolvable);
                }

                if (markerRead.Status == SwitchTransitionMarkerReadStatus.IoError)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"I/O error reading switch transition marker: {markerRead.ErrorMessage}"), JournalRecoveryStates.Unknown);
                }

                if (readResult.Status == SwitchJournalReadStatus.Absent)
                {
                    if (markerRead.Status == SwitchTransitionMarkerReadStatus.Valid)
                    {
                        var gapMarker = markerRead.Marker!;
                        if (gapMarker.Expected.State == SwitchJournalState.RECORDED &&
                            gapMarker.Next.State == SwitchJournalState.CREDENTIAL_APPLYING)
                        {
                            // The D1 crash shape for the one pair that provably crossed no
                            // target boundary: the credential writer is invoked strictly
                            // after the CREDENTIAL_APPLYING journal lands, so an unpublished
                            // successor proves the target credential was never touched.
                            // Recovery is RECORDED-level: source-runtime coherence gates any
                            // cleanup, and absence is never ordinary Clean here.
                            return await ReconcileRecordedMarkerGapAsync(gapMarker, cancellationToken).ConfigureAwait(false);
                        }

                        _sessionVault.QuarantineUnresolvedMutation();
                        return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                            StartupJournalReconciliationStatus.Quarantined,
                            $"The canonical switch journal is absent while transition marker '{gapMarker.Expected.State}' -> '{gapMarker.Next.State}' remains; the interrupted transaction may be incomplete.",
                            RetainedEntry: gapMarker.Expected),
                            JournalRecoveryStates.ActionRequired);
                    }

                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Clean, "No switch journal present."), JournalRecoveryStates.None);
                }

                if (readResult.Status == SwitchJournalReadStatus.Corrupt)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Corrupt switch journal found: {readResult.ErrorMessage}"), JournalRecoveryStates.NotResolvable);
                }

                if (readResult.Status == SwitchJournalReadStatus.UnsupportedVersion)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Unsupported switch journal schema: {readResult.ErrorMessage}"), JournalRecoveryStates.NotResolvable);
                }

                if (readResult.Status == SwitchJournalReadStatus.IoError)
                {
                    _sessionVault.QuarantineUnresolvedMutation();
                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"I/O error reading switch journal: {readResult.ErrorMessage}"), JournalRecoveryStates.Unknown);
                }

                if (readResult.Status == SwitchJournalReadStatus.Valid)
                {
                    var entry = readResult.Entry!;
                    SwitchTransitionMarkerEntry? retainedCleanupMarker = null;

                    // Marker-aware reconciliation for a canonical journal that coexists
                    // with a transition marker. The successor-published and foreign cases
                    // are decided here; the only pair that continues into per-state
                    // cleanup with its marker is the pre-credential RECORDED pair.
                    if (markerRead.Status == SwitchTransitionMarkerReadStatus.Valid)
                    {
                        var marker = markerRead.Marker!;
                        if (SwitchJournalStore.EntriesMatch(entry, marker.Next))
                        {
                            // The successor was published but the marker's exact cleanup
                            // did not complete. The exact match proves the marker stale;
                            // remove it, then reconcile the successor per its own state.
                            var staleDelete = await _journalStore
                                .DeleteTransitionMarkerWhileCanonicalGuardedAsync(entry, marker, cancellationToken)
                                .ConfigureAwait(false);
                            if (staleDelete.Status != GuardedMarkerDeleteStatus.Deleted)
                            {
                                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                                    StartupJournalReconciliationStatus.Degraded,
                                    "The durable successor journal matches its transition marker, but the exact marker could not be removed; recovery must reconcile it before switching resumes.",
                                    RetainedEntry: entry),
                                    JournalRecoveryStates.ActionRequired);
                            }
                        }
                        else if (SwitchJournalStore.EntriesMatch(entry, marker.Expected))
                        {
                            if (marker.Expected.State != SwitchJournalState.RECORDED)
                            {
                                // The predecessor is still present, but the marker's pair
                                // is one whose window may already include target or rollback
                                // side effects: retain both artifacts and quarantine.
                                _sessionVault.QuarantineUnresolvedMutation();
                                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                                    StartupJournalReconciliationStatus.Quarantined,
                                    $"Transition marker '{marker.Expected.State}' -> '{marker.Next.State}' retained with its predecessor journal; the interrupted transaction may be incomplete.",
                                    RetainedEntry: entry),
                                    JournalRecoveryStates.ActionRequired);
                            }

                            // RECORDED-origin marker with its predecessor present: the
                            // durable state is provably pre-credential (the credential
                            // writer is gated on the CREDENTIAL_APPLYING journal landing),
                            // so reconciliation continues with the existing RECORDED proof
                            // and cleans both artifacts only after that proof.
                            retainedCleanupMarker = marker;
                        }
                        else
                        {
                            // A canonical journal unrelated to the marker: fail closed and
                            // preserve both — neither may be adopted or destroyed here.
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                                StartupJournalReconciliationStatus.Quarantined,
                                "A canonical switch journal unrelated to the transition marker occupies the canonical path; manual reconciliation is required.",
                                RetainedEntry: entry),
                                JournalRecoveryStates.ActionRequired);
                        }
                    }

                    switch (entry.State)
                    {
                        case SwitchJournalState.RECORDED:
                            {
                                // NOT_ATTEMPTED provenance proves the target credential boundary
                                // was never crossed, but RECORDED is written BEFORE the source
                                // process stop: the crash may have left the source runtime
                                // stopped. Cleanup therefore requires source coherence, not
                                // merely journal absence.
                                var durableSource = await VerifyDurableSourceCoherenceAsync(
                                    entry.SourceAccountId, cancellationToken).ConfigureAwait(false);
                                if (!durableSource.Proven)
                                {
                                    // The source metadata/credential state is incoherent, which
                                    // RECORDED transactions cannot have caused themselves; treat
                                    // the environment as uncertain and quarantine.
                                    _sessionVault.QuarantineUnresolvedMutation();
                                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                                        StartupJournalReconciliationStatus.Quarantined,
                                        $"RECORDED journal retained: {durableSource.Message}", RetainedEntry: entry),
                                        JournalRecoveryStates.ActionRequired);
                                }

                                var runtime = await ProveSourceRuntimeCoherenceAsync(
                                    entry.SourceAccountId, durableSource.SourceEmail!, cancellationToken).ConfigureAwait(false);
                                if (!runtime.Proven)
                                {
                                    // Credentials are provably intact; only the source runtime
                                    // state is unproven (absent or bound to another identity).
                                    // Retain the journal and block switching until coherence is
                                    // established; never delete it as clean in this state.
                                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                                        StartupJournalReconciliationStatus.Degraded,
                                        $"RECORDED journal retained: {runtime.Message} Start Antigravity and resolve the journal.",
                                        RetainedEntry: entry),
                                        JournalRecoveryStates.ActionRequired);
                                }

                                try
                                {
                                    // Cleanup ownership is bound to the proved entry: only the
                                    // exact journal entry the source-runtime proof authorized
                                    // may be removed. A replacement written during the proof
                                    // must survive for its own reconciliation.
                                    var deleteResult = await _journalStore
                                        .DeleteIfUnchangedAsync(entry, cancellationToken)
                                        .ConfigureAwait(false);
                                    return await RecordStartupConditionalCleanup(
                                        entry,
                                        deleteResult,
                                        "RECORDED journal cleaned up; source runtime proven coherent.",
                                        retainedCleanupMarker).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    // Proven coherent underlying state. Do NOT quarantine!
                                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(
                                        StartupJournalReconciliationStatus.Degraded,
                                        $"Failed to delete RECORDED journal: {ex.Message}", RetainedEntry: entry),
                                        JournalRecoveryStates.ActionRequired);
                                }
                            }

                        case SwitchJournalState.CREDENTIAL_APPLYING:
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, "Unresolved CREDENTIAL_APPLYING journal found.", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);

                        case SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT:
                            string? activeAccountId;
                            try
                            {
                                activeAccountId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _sessionVault.QuarantineUnresolvedMutation();
                                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Failed to read metadata for PRECOMMIT journal: {ex.Message}", RetainedEntry: entry), JournalRecoveryStates.Unknown);
                            }

                            if (string.Equals(activeAccountId, entry.TargetAccountId, StringComparison.Ordinal))
                            {
                                // Metadata commit already completed!
                                try
                                {
                                    // Same cleanup ownership rule as RECORDED: the commit proof
                                    // authorizes deleting only the exact proved PRECOMMIT entry.
                                    var deleteResult = await _journalStore
                                        .DeleteIfUnchangedAsync(entry, cancellationToken)
                                        .ConfigureAwait(false);
                                    return await RecordStartupConditionalCleanup(
                                        entry,
                                        deleteResult,
                                        "PRECOMMIT journal cleaned up; target metadata already active.").ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    // Proven coherent. Do NOT quarantine!
                                    return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Degraded, $"Failed to delete PRECOMMIT journal: {ex.Message}", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);
                                }
                            }
                            else
                            {
                                // Active account is source, other, or null. Quarantine!
                                // NEVER call TryFinalizeSwitchAsync during startup!
                                _sessionVault.QuarantineUnresolvedMutation();
                                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"PRECOMMIT journal found but active account '{activeAccountId}' does not match target '{entry.TargetAccountId}'.", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);
                            }

                        case SwitchJournalState.ROLLING_BACK:
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, "Unresolved ROLLING_BACK journal found.", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);

                        case SwitchJournalState.QUARANTINED:
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, "QUARANTINED journal found.", RetainedEntry: entry), JournalRecoveryStates.ActionRequired);

                        default:
                            _sessionVault.QuarantineUnresolvedMutation();
                            return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Unrecognized journal state '{entry.State}'.", RetainedEntry: entry), JournalRecoveryStates.Unknown);
                    }
                }

                _sessionVault.QuarantineUnresolvedMutation();
                return RecordStartupReconciliationResult(new StartupJournalReconciliationResult(StartupJournalReconciliationStatus.Quarantined, $"Unknown switch journal read status '{readResult.Status}'."), JournalRecoveryStates.Unknown);
            }
            StartupJournalReconciliationResult result;
            Exception? primaryError = null;
            Exception? cleanupError = null;
            try { result = await ReconcileUnderLeaseAsync().ConfigureAwait(false); }
            catch (Exception ex) { primaryError = ex; throw; }
            finally
            {
                try
                {
                    cleanupError = await LeaseCleanup.TryDisposeAsync(switchLease).ConfigureAwait(false);
                    if (cleanupError != null)
                    {
                        _recoveryQuarantine.Mark();
                        _sessionVault.QuarantineUnresolvedMutation();
                    }
                }
                finally { switchLock.Release(); }
                if (cleanupError != null) LeaseCleanup.PreservePrimaryFailure(primaryError, cleanupError);
            }
            if (cleanupError != null)
            {
                result = result with
                {
                    Status = result.Status == StartupJournalReconciliationStatus.Clean
                        ? StartupJournalReconciliationStatus.Quarantined : result.Status,
                    Message = result.Message + " Lease cleanup also failed; manual recovery is required."
                };
                return RecordStartupReconciliationResult(result,
                    _journalRecoveryState == JournalRecoveryStates.None ? JournalRecoveryStates.Unknown : _journalRecoveryState);
            }
            return result;

        }
        finally
        {
            SwitchGate.Release();
        }
    }

    private sealed record CoherenceProofOutcome(
        bool IsProven,
        string? ActiveAccountId,
        string SafeMessage,
        string? ReasonCode = null);

    private async Task<CoherenceProofOutcome> VerifyCoherenceProofAsync(CancellationToken cancellationToken)
    {
        // 1. Persisted active account ID
        string? activeAccountId;
        try
        {
            activeAccountId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new CoherenceProofOutcome(false, null, "Failed to read active account from metadata.", "NO_ACTIVE_ACCOUNT");
        }

        if (string.IsNullOrWhiteSpace(activeAccountId))
        {
            return new CoherenceProofOutcome(false, null, "No active account is set in metadata.", "NO_ACTIVE_ACCOUNT");
        }

        // 2. Corresponding account metadata still exists
        AccountMetadata? activeAccount;
        try
        {
            activeAccount = await _accountStore.GetAccountAsync(activeAccountId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new CoherenceProofOutcome(false, activeAccountId, "The active account was not found in metadata.", "ACTIVE_ACCOUNT_NOT_FOUND");
        }

        if (activeAccount == null)
        {
            return new CoherenceProofOutcome(false, activeAccountId, "The active account was not found in metadata.", "ACTIVE_ACCOUNT_NOT_FOUND");
        }

        // 3. Current enrollment/validation status is still acceptable
        if (activeAccount.ValidationStatus != AccountValidationStatus.Valid || !activeAccount.HasVaultedSession)
        {
            return new CoherenceProofOutcome(false, activeAccountId, "The active account is not validly enrolled or has no vaulted session.", "ACCOUNT_NOT_ENROLLED");
        }

        // 4. Fresh live Antigravity identity still corresponds to the same active account
        AccountIdentityDto? liveIdentity;
        try
        {
            liveIdentity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new CoherenceProofOutcome(false, activeAccountId, "Live Antigravity identity is unavailable; start Antigravity and ensure an account is active before resolving.", "LIVE_IDENTITY_UNAVAILABLE");
        }

        if (liveIdentity == null || string.IsNullOrWhiteSpace(liveIdentity.Email))
        {
            return new CoherenceProofOutcome(false, activeAccountId, "Live Antigravity identity is unavailable; start Antigravity and ensure an account is active before resolving.", "LIVE_IDENTITY_UNAVAILABLE");
        }

        if (!string.Equals(activeAccount.Email?.Trim(), liveIdentity.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new CoherenceProofOutcome(false, activeAccountId, "Live Antigravity identity does not match the active account metadata.", "LIVE_IDENTITY_MISMATCH");
        }

        // 5. Fresh WinCred & Recovery Vault Payload
        WinCredEntry? winCred = null;
        byte[]? vaultedSession = null;
        try
        {
            try
            {
                winCred = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Windows Credential Manager entry is missing or empty.", "CREDENTIAL_MISSING");
            }

            if (winCred?.Blob == null || winCred.Blob.Length == 0)
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Windows Credential Manager entry is missing or empty.", "CREDENTIAL_MISSING");
            }

            try
            {
                vaultedSession = await _sessionVault.GetSessionForRecoveryAsync(activeAccountId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Vaulted session for the active account is missing or empty.", "VAULT_SESSION_MISSING");
            }

            if (vaultedSession == null || vaultedSession.Length == 0)
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Vaulted session for the active account is missing or empty.", "VAULT_SESSION_MISSING");
            }

            // 6. WinCred payload equals vaulted session using constant-time byte comparison
            bool credsMatch = winCred.Blob.Length == vaultedSession.Length
                           && CryptographicOperations.FixedTimeEquals(winCred.Blob, vaultedSession);

            if (!credsMatch)
            {
                return new CoherenceProofOutcome(false, activeAccountId, "Windows Credential Manager payload does not match the vaulted session.", "CREDENTIAL_MISMATCH");
            }

            return new CoherenceProofOutcome(true, activeAccountId, "Coherence verified.");
        }
        finally
        {
            if (winCred?.Blob != null) CryptographicOperations.ZeroMemory(winCred.Blob);
            if (vaultedSession != null) CryptographicOperations.ZeroMemory(vaultedSession);
        }
    }

    private sealed record SourceCoherenceOutcome(bool Proven, string? SourceEmail, string Message);

    /// <summary>
    /// Establishes the durable facts RECORDED recovery needs: the journal's source account
    /// still exists in metadata and carries the email the runtime identity proof compares
    /// against. RECORDED transactions performed no credential or metadata mutation, so there
    /// is nothing to compensate and no active-selection or validation requirement; the
    /// runtime proof below is what decides cleanup safety. This deliberately does not read
    /// the live process, so recovery can also decide retention when the runtime is absent.
    /// </summary>
    private async Task<SourceCoherenceOutcome> VerifyDurableSourceCoherenceAsync(string sourceAccountId, CancellationToken cancellationToken)
    {
        AccountMetadata? sourceAccount;
        try
        {
            sourceAccount = await _accountStore.GetAccountAsync(sourceAccountId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return new SourceCoherenceOutcome(false, null, "Failed to read account metadata for source coherence.");
        }

        if (sourceAccount == null || string.IsNullOrWhiteSpace(sourceAccount.Email))
        {
            return new SourceCoherenceOutcome(false, null,
                "The journal's source account is missing from metadata.");
        }

        return new SourceCoherenceOutcome(true, sourceAccount.Email, "Source account metadata is present.");
    }

    private sealed record SourceRuntimeProofOutcome(bool Proven, string Message);

    /// <summary>
    /// Proves the source Antigravity runtime is alive and bound to the source identity.
    /// RECORDED is journaled before the source process stop, so a crash after that write can
    /// leave the source stopped; the runtime is only declared coherent when live telemetry
    /// reports a connected server whose identity matches the source account. The whole proof
    /// (including each telemetry probe) is bounded by one deadline, so Antigravity's own
    /// client supervision — the only launcher that owns the session-bound launch flags — can
    /// restore the process within the window, while a hanging RPC can never retain recovery
    /// ownership past it. A runtime that never appears keeps the journal retained and blocked.
    /// </summary>
    private async Task<SourceRuntimeProofOutcome> ProveSourceRuntimeCoherenceAsync(
        string sourceAccountId, string sourceEmail, CancellationToken cancellationToken)
    {
        string trimmedEmail = sourceEmail.Trim();
        using var proofCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        proofCts.CancelAfter(_verificationTimeout);
        var proofToken = proofCts.Token;
        try
        {
            while (true)
            {
                proofToken.ThrowIfCancellationRequested();
                try
                {
                    var status = await _adapter.GetStatusAsync(proofToken).ConfigureAwait(false);
                    if (status.Connected && status.Status is not ("DEGRADED" or "OFFLINE" or "ERROR"))
                    {
                        var identity = await _adapter.GetCurrentAccountAsync(proofToken).ConfigureAwait(false);
                        if (identity != null && !string.IsNullOrWhiteSpace(identity.Email))
                        {
                            if (string.Equals(identity.Email.Trim(), trimmedEmail, StringComparison.OrdinalIgnoreCase))
                            {
                                return new SourceRuntimeProofOutcome(true,
                                    "Source Antigravity runtime is alive and bound to the source identity.");
                            }

                            return new SourceRuntimeProofOutcome(false,
                                "Live Antigravity identity does not match the journal's source account.");
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Probe exceeded the proof deadline; fall through to the retention outcome.
                    break;
                }
                catch
                {
                    // Transient telemetry unavailability; keep probing until the deadline.
                }

                await Task.Delay(_pollInterval, proofToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Proof deadline reached.
        }

        return new SourceRuntimeProofOutcome(false,
            "Source Antigravity runtime coherence could not be proven before the recovery deadline.");
    }

    private JournalResolutionResult RecordResolutionResult(JournalResolutionResult result)
    {
        lock (_statusLock)
        {
            if (_journalRecoveryState == JournalRecoveryStates.RestartRequired)
            {
                return result.Status is JournalResolutionStatus.CleanCleanupCompleted or JournalResolutionStatus.NoJournal
                    ? result with { RestartRequired = true } : result;
            }

            switch (result.Status)
            {
                case JournalResolutionStatus.ResolvedRestartRequired:
                    _journalRecoveryState = JournalRecoveryStates.RestartRequired;
                    break;

                case JournalResolutionStatus.CleanCleanupCompleted:
                case JournalResolutionStatus.NoJournal:
                    _journalRecoveryState = JournalRecoveryStates.None;
                    if (IsQuarantinedOrRecoveryUnresolved())
                        result = result with { RestartRequired = true, Message = "Journal cleanup completed, but switching remains quarantined. Restart AG2 Router before switching can resume." };
                    break;

                case JournalResolutionStatus.NotResolvable:
                    _recoveryQuarantine.Mark();
                    _journalRecoveryState = JournalRecoveryStates.NotResolvable;
                    break;

                case JournalResolutionStatus.ProofFailed:
                    _recoveryQuarantine.Mark();
                    _journalRecoveryState = JournalRecoveryStates.ActionRequired;
                    break;

                case JournalResolutionStatus.PersistenceFailure:
                    _recoveryQuarantine.Mark();
                    if (result.ReasonCode == "DELETE_FAILED" || result.ReasonCode == "UNSUPPORTED_PLATFORM" || result.ReasonCode == "TARGET_QUOTA_INVALIDATION_FAILED")
                    {
                        _journalRecoveryState = JournalRecoveryStates.ActionRequired;
                    }
                    else
                    {
                        _journalRecoveryState = JournalRecoveryStates.Unknown;
                    }
                    break;

                default:
                    _recoveryQuarantine.Mark();
                    _journalRecoveryState = JournalRecoveryStates.Unknown;
                    break;
            }
        }
        return result;
    }

    public async Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default)
    {
        await SwitchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string switchResource = _sessionVault.GetVaultPath() + ".switch";
            var switchLock = PathLockRegistry.Get(switchResource);
            if (!await switchLock.WaitAsync(_transactionTimeout, cancellationToken).ConfigureAwait(false))
            {
                return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Lock acquisition timed out during journal resolution.", ReasonCode: "LOCK_TIMEOUT"));
            }

            IAsyncDisposable? switchLease = null;
            async Task<JournalResolutionResult> ResolveUnderLeaseAsync()
            {
                try
                {
                    switchLease = await AcquireRecoveryLeaseAsync(switchResource, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Cross-process lease acquisition failed.", ReasonCode: "LEASE_ACQUISITION_FAILED"));
                }

                SwitchJournalReadResult readResult;
                try
                {
                    readResult = await _journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "An I/O error occurred while accessing the switch journal.", ReasonCode: "IO_ERROR"));
                }

                // Resolution classifies the canonical journal and its transition marker
                // together; neither file may be ignored while resolving recovery evidence.
                SwitchTransitionMarkerReadResult markerRead;
                try
                {
                    markerRead = await _journalStore.ReadTransitionMarkerAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    markerRead = SwitchTransitionMarkerReadResult.IoError(ex);
                }

                if (markerRead.Status is SwitchTransitionMarkerReadStatus.Corrupt or SwitchTransitionMarkerReadStatus.UnsupportedVersion)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.NotResolvable, "The switch transition marker is corrupted and cannot be resolved automatically.", ReasonCode: "CORRUPT_TRANSITION_MARKER"));
                }

                if (markerRead.Status == SwitchTransitionMarkerReadStatus.IoError)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "An I/O error occurred while accessing the switch transition marker.", ReasonCode: "IO_ERROR"));
                }

                if (readResult.Status == SwitchJournalReadStatus.Absent)
                {
                    if (markerRead.Status == SwitchTransitionMarkerReadStatus.Valid)
                    {
                        return RecordResolutionResult(await ResolveTransitionMarkerGapAsync(
                            markerRead.Marker!, cancellationToken).ConfigureAwait(false));
                    }

                    var clearanceFailure = await GetRecoveryEvidenceClearanceFailureAsync().ConfigureAwait(false);
                    if (clearanceFailure != null)
                        return RecordResolutionResult(clearanceFailure);
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present.", RestartRequired: false));
                }

                if (readResult.Status == SwitchJournalReadStatus.Corrupt)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.NotResolvable, "The switch journal is corrupted and cannot be resolved automatically.", ReasonCode: "CORRUPT_JOURNAL"));
                }

                if (readResult.Status == SwitchJournalReadStatus.UnsupportedVersion)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.NotResolvable, "The switch journal uses an unsupported schema version and cannot be resolved automatically.", ReasonCode: "UNSUPPORTED_VERSION"));
                }

                if (readResult.Status == SwitchJournalReadStatus.IoError)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "An I/O error occurred while accessing the switch journal.", ReasonCode: "IO_ERROR"));
                }

                if (readResult.Status != SwitchJournalReadStatus.Valid || readResult.Entry == null)
                {
                    return RecordResolutionResult(new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "An I/O error occurred while accessing the switch journal.", ReasonCode: "IO_ERROR"));
                }

                var entry = readResult.Entry;

                // Marker-aware resolution for a canonical journal that coexists with a
                // transition marker. A stale marker (successor already durable) is removed
                // exactly first; a matching predecessor marker either routes the RECORDED
                // pair through the RECORDED branch or travels with the standard proof path
                // as cleanup duty; anything else is foreign evidence that fails closed.
                SwitchTransitionMarkerEntry? cleanupMarker = null;
                if (markerRead.Status == SwitchTransitionMarkerReadStatus.Valid)
                {
                    var marker = markerRead.Marker!;
                    if (SwitchJournalStore.EntriesMatch(entry, marker.Next))
                    {
                        var staleDelete = await _journalStore
                            .DeleteTransitionMarkerWhileCanonicalGuardedAsync(entry, marker, cancellationToken)
                            .ConfigureAwait(false);
                        if (staleDelete.Status != GuardedMarkerDeleteStatus.Deleted)
                        {
                            return RecordResolutionResult(new JournalResolutionResult(
                                JournalResolutionStatus.PersistenceFailure,
                                "The durable successor journal matches its transition marker, but the exact marker could not be removed; recovery remains blocked.",
                                ReasonCode: "DELETE_FAILED"));
                        }
                    }
                    else if (SwitchJournalStore.EntriesMatch(entry, marker.Expected))
                    {
                        cleanupMarker = marker;
                    }
                    else
                    {
                        return RecordResolutionResult(new JournalResolutionResult(
                            JournalResolutionStatus.ProofFailed,
                            "A canonical switch journal unrelated to the transition marker occupies the canonical path; manual reconciliation is required.",
                            ReasonCode: "MISMATCHED_RECOVERY_EVIDENCE"));
                    }
                }

                // F-270-3: Clean-only states alignment with Step 3
                if (entry.State == SwitchJournalState.RECORDED)
                {
                    // RECORDED is written before the source process stop, so it proves the
                    // target credential boundary was never crossed (no target evidence is
                    // ever touched here) but NOT that the source runtime is still running.
                    // Cleanup requires source coherence proof, never blind deletion.
                    var durableSource = await VerifyDurableSourceCoherenceAsync(
                        entry.SourceAccountId, cancellationToken).ConfigureAwait(false);
                    if (!durableSource.Proven)
                    {
                        return RecordResolutionResult(new JournalResolutionResult(
                            JournalResolutionStatus.ProofFailed,
                            durableSource.Message,
                            ReasonCode: "SOURCE_COHERENCE_UNPROVEN"));
                    }

                    var runtime = await ProveSourceRuntimeCoherenceAsync(
                        entry.SourceAccountId, durableSource.SourceEmail!, cancellationToken).ConfigureAwait(false);
                    if (!runtime.Proven)
                    {
                        return RecordResolutionResult(new JournalResolutionResult(
                            JournalResolutionStatus.ProofFailed,
                            runtime.Message,
                            ReasonCode: "SOURCE_RUNTIME_UNPROVEN"));
                    }

                    // Source coherence proven; conditional clean cleanup. Deleting the proved
                    // entry is not by itself proof of path clearance, so a Deleted outcome
                    // re-reads the canonical path before publishing clean.
                    var del = await _journalStore.DeleteIfUnchangedAsync(entry, cancellationToken).ConfigureAwait(false);
                    if (del.Status == SwitchJournalDeleteStatus.Deleted)
                    {
                        var markerCleanupFailure = await CleanupResolutionMarkerAsync(cleanupMarker).ConfigureAwait(false);
                        if (markerCleanupFailure != null)
                        {
                            return RecordResolutionResult(markerCleanupFailure);
                        }
                    }

                    if (del.Status == SwitchJournalDeleteStatus.Deleted)
                    {
                        var clearanceFailure = await GetRecoveryEvidenceClearanceFailureAsync().ConfigureAwait(false);
                        if (clearanceFailure != null)
                            return RecordResolutionResult(clearanceFailure);
                    }

                    return RecordResolutionResult(del.Status switch
                    {
                        SwitchJournalDeleteStatus.Deleted => new JournalResolutionResult(JournalResolutionStatus.CleanCleanupCompleted, "Switch journal cleaned up successfully.", RestartRequired: false, ReasonCode: "CLEAN_RECORDED_REMOVED"),
                        SwitchJournalDeleteStatus.Absent => (await GetRecoveryEvidenceClearanceFailureAsync(unexpectedAbsence: true).ConfigureAwait(false))!,
                        SwitchJournalDeleteStatus.NotMatched => new JournalResolutionResult(JournalResolutionStatus.ProofFailed, "State changed concurrently during resolution proof.", ReasonCode: "CONCURRENT_MUTATION"),
                        SwitchJournalDeleteStatus.UnsupportedPlatform => new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Conditional switch journal deletion is not supported on this platform.", RestartRequired: false, ReasonCode: "UNSUPPORTED_PLATFORM"),
                        _ => new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Failed to delete the switch journal file.", ReasonCode: "DELETE_FAILED")
                    });
                }

                if (entry.State == SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT)
                {
                    string? currentActiveId = null;
                    try
                    {
                        currentActiveId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Fall through to unresolved proof path
                    }

                    if (string.Equals(currentActiveId, entry.TargetAccountId, StringComparison.Ordinal))
                    {
                        // Metadata commit already completed before crash. Clean cleanup; the
                        // Deleted outcome must still prove the canonical path clear.
                        var del = await _journalStore.DeleteIfUnchangedAsync(entry, cancellationToken).ConfigureAwait(false);
                        if (del.Status == SwitchJournalDeleteStatus.Deleted)
                        {
                            var markerCleanupFailure = await CleanupResolutionMarkerAsync(cleanupMarker).ConfigureAwait(false);
                            if (markerCleanupFailure != null)
                            {
                                return RecordResolutionResult(markerCleanupFailure);
                            }
                        }

                        if (del.Status == SwitchJournalDeleteStatus.Deleted)
                        {
                            var clearanceFailure = await GetRecoveryEvidenceClearanceFailureAsync().ConfigureAwait(false);
                            if (clearanceFailure != null)
                                return RecordResolutionResult(clearanceFailure);
                        }

                        return RecordResolutionResult(del.Status switch
                        {
                            SwitchJournalDeleteStatus.Deleted => new JournalResolutionResult(JournalResolutionStatus.CleanCleanupCompleted, "Switch journal cleaned up successfully.", RestartRequired: false, ReasonCode: "CLEAN_COMMITTED_PRECOMMIT_REMOVED"),
                            SwitchJournalDeleteStatus.Absent => (await GetRecoveryEvidenceClearanceFailureAsync(unexpectedAbsence: true).ConfigureAwait(false))!,
                            SwitchJournalDeleteStatus.NotMatched => new JournalResolutionResult(JournalResolutionStatus.ProofFailed, "State changed concurrently during resolution proof.", ReasonCode: "CONCURRENT_MUTATION"),
                            SwitchJournalDeleteStatus.UnsupportedPlatform => new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Conditional switch journal deletion is not supported on this platform.", RestartRequired: false, ReasonCode: "UNSUPPORTED_PLATFORM"),
                            _ => new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure, "Failed to delete the switch journal file.", ReasonCode: "DELETE_FAILED")
                        });
                    }
                }

                // Primary Proof-Resolution Cases (CREDENTIAL_APPLYING, unresolved PRECOMMIT, ROLLING_BACK, QUARANTINED):
                var proofOutcome = await RunResolutionProofsAsync(entry, cancellationToken).ConfigureAwait(false);
                if (!proofOutcome.Proven)
                {
                    return RecordResolutionResult(proofOutcome.Failure!);
                }

                // 3. Conditional Journal Deletion (F-270-2). The Deleted outcome must still
                // prove the canonical path clear before resolution claims success.
                var deleteResult = await _journalStore.DeleteIfUnchangedAsync(entry, cancellationToken).ConfigureAwait(false);
                if (deleteResult.Status == SwitchJournalDeleteStatus.Deleted)
                {
                    var markerCleanupFailure = await CleanupResolutionMarkerAsync(cleanupMarker).ConfigureAwait(false);
                    if (markerCleanupFailure != null)
                    {
                        return RecordResolutionResult(markerCleanupFailure with
                        {
                            CoherentAccountId = proofOutcome.ActiveAccountId
                        });
                    }
                }

                if (deleteResult.Status == SwitchJournalDeleteStatus.Deleted)
                {
                    var clearanceFailure = await GetRecoveryEvidenceClearanceFailureAsync().ConfigureAwait(false);
                    if (clearanceFailure != null)
                        return RecordResolutionResult(clearanceFailure with { CoherentAccountId = proofOutcome.ActiveAccountId });
                }

                return RecordResolutionResult(deleteResult.Status switch
                {
                    SwitchJournalDeleteStatus.Deleted => new JournalResolutionResult(
                        JournalResolutionStatus.ResolvedRestartRequired,
                        "Switch journal successfully resolved and removed. Application restart is required before normal routing resumes.",
                        CoherentAccountId: proofOutcome.ActiveAccountId,
                        RestartRequired: true),
                    SwitchJournalDeleteStatus.NotMatched => new JournalResolutionResult(
                        JournalResolutionStatus.ProofFailed,
                        "State changed concurrently during resolution proof.",
                        ReasonCode: "CONCURRENT_MUTATION"),
                    SwitchJournalDeleteStatus.Absent => (await GetRecoveryEvidenceClearanceFailureAsync(unexpectedAbsence: true).ConfigureAwait(false))!,
                    SwitchJournalDeleteStatus.UnsupportedPlatform => new JournalResolutionResult(
                        JournalResolutionStatus.PersistenceFailure,
                        "Conditional switch journal deletion is not supported on this platform.",
                        CoherentAccountId: proofOutcome.ActiveAccountId,
                        RestartRequired: false,
                        ReasonCode: "UNSUPPORTED_PLATFORM"),
                    _ => new JournalResolutionResult(
                        JournalResolutionStatus.PersistenceFailure,
                        "Failed to delete the switch journal file.",
                        CoherentAccountId: proofOutcome.ActiveAccountId,
                        RestartRequired: true,
                        ReasonCode: "DELETE_FAILED")
                });
            }
            JournalResolutionResult result;
            Exception? primaryError = null;
            Exception? cleanupError = null;
            try { result = await ResolveUnderLeaseAsync().ConfigureAwait(false); }
            catch (Exception ex) { primaryError = ex; throw; }
            finally
            {
                bool reusable = false;
                try
                {
                    if (BeforeLeaseReleaseAsync is { } beforeRelease)
                    {
                        try { await beforeRelease().ConfigureAwait(false); } catch { }
                    }
                    string? completedTransactionId;
                    lock (_statusLock) completedTransactionId = _activeTransactionId == null ? _lastResult?.TransactionId : null;
                    reusable = completedTransactionId != null &&
                        await ProveTransactionReusableAsync(completedTransactionId).ConfigureAwait(false);
                }
                catch (Exception ex) { cleanupError = ex; }
                try
                {
                    cleanupError = await LeaseCleanup.TryDisposeAsync(switchLease, cleanupError).ConfigureAwait(false);
                    lock (_statusLock)
                    {
                        if (cleanupError == null && reusable && _activeTransactionId == null && !IsQuarantinedOrRecoveryUnresolved() &&
                            _currentState is NativeSwitchStates.Complete or NativeSwitchStates.RolledBack or NativeSwitchStates.Failed)
                            _currentState = NativeSwitchStates.Idle;
                    }
                    if (cleanupError != null)
                    {
                        _recoveryQuarantine.Mark();
                        _sessionVault.QuarantineUnresolvedMutation();
                    }
                }
                finally { switchLock.Release(); }
                if (cleanupError != null) LeaseCleanup.PreservePrimaryFailure(primaryError, cleanupError);
            }
            if (cleanupError != null)
            {
                bool failed = result.Status is JournalResolutionStatus.ProofFailed or
                    JournalResolutionStatus.NotResolvable or JournalResolutionStatus.PersistenceFailure;
                return RecordResolutionResult(result with
                {
                    Status = failed ? result.Status : JournalResolutionStatus.PersistenceFailure,
                    ReasonCode = failed ? result.ReasonCode : "LEASE_CLEANUP_FAILED",
                    RestartRequired = true,
                    Message = result.Message + " Lease cleanup also failed; manual recovery is required."
                });
            }
            return result;

        }
        finally
        {
            SwitchGate.Release();
        }
    }

    /// <summary>
    /// The shared operator-resolution proof core: two full coherence proofs with a stable
    /// active-account check between them, then durable target-quota invalidation gated on
    /// the persisted activation provenance of the evidence entry. Used by the retained-
    /// journal path and by the absent-canonical marker gap, whose conservative evidence is
    /// the marker's intended successor.
    /// </summary>
    private async Task<(bool Proven, JournalResolutionResult? Failure, string? ActiveAccountId)> RunResolutionProofsAsync(
        SwitchJournalEntry evidence, CancellationToken cancellationToken)
    {
        // 1. Initial 4-Pillar Proof:
        var initialProof = await VerifyCoherenceProofAsync(cancellationToken).ConfigureAwait(false);
        if (!initialProof.IsProven)
        {
            return (false, new JournalResolutionResult(JournalResolutionStatus.ProofFailed, initialProof.SafeMessage, ReasonCode: initialProof.ReasonCode), null);
        }

        // 2. Final 4-Pillar Proof (F-270-1) immediately before delete:
        var finalProof = await VerifyCoherenceProofAsync(cancellationToken).ConfigureAwait(false);
        if (!finalProof.IsProven)
        {
            return (false, new JournalResolutionResult(JournalResolutionStatus.ProofFailed, finalProof.SafeMessage, ReasonCode: finalProof.ReasonCode), null);
        }

        if (!string.Equals(finalProof.ActiveAccountId, initialProof.ActiveAccountId, StringComparison.Ordinal))
        {
            return (false, new JournalResolutionResult(JournalResolutionStatus.ProofFailed, "State changed concurrently during resolution proof.", ReasonCode: "CONCURRENT_MUTATION"), null);
        }

        // 2.5 Invalidate unverified target quota observations (F1/R1 remediation).
        // The decision uses the PERSISTED activation provenance, not merely the identity
        // comparison:
        // - NOT_ATTEMPTED proves the target credential boundary was never crossed, so the
        //   untouched target's observations remain trustworthy and must be preserved.
        // - MAY_HAVE_BEEN_ATTEMPTED means the target credential mutation may have begun,
        //   so cached target evidence is untrusted and must be durably invalidated.
        // - UNKNOWN is the legacy/ambiguous form (journals written before provenance was
        //   persisted); recovery treats it conservatively like MAY_HAVE_BEEN_ATTEMPTED
        //   rather than silently assuming NOT_ATTEMPTED.
        // When the target is the proven-coherent active account, its evidence describes
        // the account recovery just proved live, so invalidation is unnecessary.
        bool targetProvenanceRequiresInvalidation =
            evidence.TargetActivationProvenance != SwitchTargetActivationProvenance.NOT_ATTEMPTED;
        if (_quotaObservationStore != null &&
            targetProvenanceRequiresInvalidation &&
            !string.IsNullOrWhiteSpace(evidence.TargetAccountId) &&
            !string.Equals(finalProof.ActiveAccountId, evidence.TargetAccountId, StringComparison.Ordinal))
        {
            try
            {
                await _quotaObservationStore.InvalidateObservationsForAccountAsync(
                    evidence.TargetAccountId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return (false, new JournalResolutionResult(
                    JournalResolutionStatus.PersistenceFailure,
                    $"Failed to invalidate unverified target quota observations during recovery resolution: {AG2Security.SanitizeError(ex)}",
                    CoherentAccountId: finalProof.ActiveAccountId,
                    RestartRequired: true,
                    ReasonCode: "TARGET_QUOTA_INVALIDATION_FAILED"), null);
            }
        }

        return (true, null, finalProof.ActiveAccountId);
    }

    /// <summary>
    /// Removes the exact resolution cleanup marker after the journal deletion succeeded.
    /// Returns null when nothing blocked; a surviving or unremovable marker fails closed so
    /// resolution never destroys or ignores marker evidence.
    /// </summary>
    private async Task<JournalResolutionResult?> CleanupResolutionMarkerAsync(SwitchTransitionMarkerEntry? cleanupMarker)
    {
        if (cleanupMarker == null)
        {
            return null;
        }

        var markerDelete = await _journalStore
            .DeleteTransitionMarkerIfUnchangedAsync(cleanupMarker, CancellationToken.None)
            .ConfigureAwait(false);
        if (markerDelete.Status is SwitchTransitionMarkerDeleteStatus.Deleted or SwitchTransitionMarkerDeleteStatus.Absent)
        {
            return null;
        }

        return new JournalResolutionResult(
            JournalResolutionStatus.PersistenceFailure,
            "The exact transition marker could not be removed after the journal cleanup; it must be reconciled before switching resumes.",
            ReasonCode: "DELETE_FAILED");
    }

    /// <summary>
    /// Operator resolution for the absent-canonical marker gap. The RECORDED ->
    /// CREDENTIAL_APPLYING pair is the only one that provably crossed no target boundary;
    /// it resolves through the RECORDED source-runtime proof. Every other pair resolves
    /// through the full operator proof path with conservative target-evidence semantics
    /// taken from the marker's intended successor, and only an exact marker deletion after
    /// both recovery paths are proven clear can succeed.
    /// </summary>
    private async Task<JournalResolutionResult> ResolveTransitionMarkerGapAsync(
        SwitchTransitionMarkerEntry marker, CancellationToken cancellationToken)
    {
        if (marker.Expected.State == SwitchJournalState.RECORDED &&
            marker.Next.State == SwitchJournalState.CREDENTIAL_APPLYING)
        {
            var durableSource = await VerifyDurableSourceCoherenceAsync(
                marker.Expected.SourceAccountId, cancellationToken).ConfigureAwait(false);
            if (!durableSource.Proven)
            {
                return new JournalResolutionResult(
                    JournalResolutionStatus.ProofFailed,
                    durableSource.Message,
                    ReasonCode: "SOURCE_COHERENCE_UNPROVEN");
            }

            var runtime = await ProveSourceRuntimeCoherenceAsync(
                marker.Expected.SourceAccountId, durableSource.SourceEmail!, cancellationToken).ConfigureAwait(false);
            if (!runtime.Proven)
            {
                return new JournalResolutionResult(
                    JournalResolutionStatus.ProofFailed,
                    runtime.Message,
                    ReasonCode: "SOURCE_RUNTIME_UNPROVEN");
            }

            var markerDelete = await _journalStore
                .DeleteTransitionMarkerIfUnchangedAsync(marker, cancellationToken)
                .ConfigureAwait(false);
            if (markerDelete.Status == SwitchTransitionMarkerDeleteStatus.Deleted)
            {
                var clearanceFailure = await GetRecoveryEvidenceClearanceFailureAsync().ConfigureAwait(false);
                if (clearanceFailure != null)
                    return clearanceFailure;
            }

            return markerDelete.Status switch
            {
                SwitchTransitionMarkerDeleteStatus.Deleted => new JournalResolutionResult(
                    JournalResolutionStatus.CleanCleanupCompleted,
                    "RECORDED transition marker cleaned up; source runtime proven coherent.",
                    RestartRequired: false,
                    ReasonCode: "CLEAN_RECORDED_TRANSITION_REMOVED"),
                SwitchTransitionMarkerDeleteStatus.Absent => (await GetRecoveryEvidenceClearanceFailureAsync(unexpectedAbsence: true).ConfigureAwait(false))!,
                SwitchTransitionMarkerDeleteStatus.NotMatched => new JournalResolutionResult(
                    JournalResolutionStatus.ProofFailed,
                    "The transition marker changed during resolution; it was preserved untouched.",
                    ReasonCode: "CONCURRENT_MUTATION"),
                SwitchTransitionMarkerDeleteStatus.UnsupportedPlatform => new JournalResolutionResult(
                    JournalResolutionStatus.PersistenceFailure,
                    "Conditional transition marker deletion is not supported on this platform.",
                    RestartRequired: false,
                    ReasonCode: "UNSUPPORTED_PLATFORM"),
                _ => new JournalResolutionResult(
                    JournalResolutionStatus.PersistenceFailure,
                    "Failed to delete the transition marker file.",
                    ReasonCode: "DELETE_FAILED")
            };
        }

        var proofOutcome = await RunResolutionProofsAsync(marker.Next, cancellationToken).ConfigureAwait(false);
        if (!proofOutcome.Proven)
        {
            return proofOutcome.Failure!;
        }

        var delete = await _journalStore
            .DeleteTransitionMarkerIfUnchangedAsync(marker, cancellationToken)
            .ConfigureAwait(false);
        if (delete.Status == SwitchTransitionMarkerDeleteStatus.Deleted)
        {
            var clearanceFailure = await GetRecoveryEvidenceClearanceFailureAsync().ConfigureAwait(false);
            if (clearanceFailure != null)
                return clearanceFailure with { CoherentAccountId = proofOutcome.ActiveAccountId };
        }

        return delete.Status switch
        {
            SwitchTransitionMarkerDeleteStatus.Deleted => new JournalResolutionResult(
                JournalResolutionStatus.ResolvedRestartRequired,
                "Interrupted transition marker successfully resolved and removed. Application restart is required before normal routing resumes.",
                CoherentAccountId: proofOutcome.ActiveAccountId,
                RestartRequired: true),
            SwitchTransitionMarkerDeleteStatus.NotMatched => new JournalResolutionResult(
                JournalResolutionStatus.ProofFailed,
                "The transition marker changed during resolution; it was preserved untouched.",
                ReasonCode: "CONCURRENT_MUTATION"),
            SwitchTransitionMarkerDeleteStatus.Absent => (await GetRecoveryEvidenceClearanceFailureAsync(unexpectedAbsence: true).ConfigureAwait(false))!,
            SwitchTransitionMarkerDeleteStatus.UnsupportedPlatform => new JournalResolutionResult(
                JournalResolutionStatus.PersistenceFailure,
                "Conditional transition marker deletion is not supported on this platform.",
                CoherentAccountId: proofOutcome.ActiveAccountId,
                RestartRequired: false,
                ReasonCode: "UNSUPPORTED_PLATFORM"),
            _ => new JournalResolutionResult(
                JournalResolutionStatus.PersistenceFailure,
                "Failed to delete the transition marker file.",
                CoherentAccountId: proofOutcome.ActiveAccountId,
                RestartRequired: true,
                ReasonCode: "DELETE_FAILED")
        };
    }

    public async Task<NativeSwitchResult> SwitchAsync(
        string targetAccountId,
        CancellationToken cancellationToken = default)
        => await ObserveSwitchAsync(targetAccountId, null, null, null, null, cancellationToken).ConfigureAwait(false);

    public Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
        CancellationToken cancellationToken = default)
        => ObserveSwitchAsync(targetAccountId, expectedActiveAccountId,
            planIsCurrent == null ? null : () => Task.FromResult(planIsCurrent()),
            null, null, cancellationToken);

    public Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        CancellationToken cancellationToken = default)
        => ObserveSwitchAsync(targetAccountId, expectedActiveAccountId,
            planIsCurrent == null ? null : () => Task.FromResult(planIsCurrent()),
            requiredWorkloadModelKey, minimumCandidateQuotaPercent, cancellationToken);

    public Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        Func<CancellationToken, Task<IDisposable>> acquireInterruptionAdmissionAsync,
        CancellationToken cancellationToken = default)
        => ObserveSwitchAsync(targetAccountId, expectedActiveAccountId,
            planIsCurrent == null ? null : () => Task.FromResult(planIsCurrent()),
            requiredWorkloadModelKey, minimumCandidateQuotaPercent, cancellationToken,
            acquireInterruptionAdmissionAsync ?? throw new ArgumentNullException(nameof(acquireInterruptionAdmissionAsync)));

    public Task<NativeSwitchResult> SwitchAutomaticallyAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<Task<bool>> planIsCurrentAsync,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        Func<CancellationToken, Task<IDisposable>> acquireInterruptionAdmissionAsync,
        CancellationToken cancellationToken = default)
        => ObserveSwitchAsync(targetAccountId, expectedActiveAccountId, planIsCurrentAsync,
            requiredWorkloadModelKey, minimumCandidateQuotaPercent, cancellationToken,
            acquireInterruptionAdmissionAsync ?? throw new ArgumentNullException(nameof(acquireInterruptionAdmissionAsync)));

    public bool CanAdmitSwitch(out string? blockingReason)
    {
        lock (_statusLock)
        {
            if (_isShuttingDown)
            {
                blockingReason = "Switch blocked: coordinator is shutting down.";
                return false;
            }

            if (!string.Equals(_journalRecoveryState, JournalRecoveryStates.None, StringComparison.Ordinal))
            {
                blockingReason = _journalRecoveryState switch
                {
                    JournalRecoveryStates.ActionRequired => "Switch blocked: journal recovery action is required.",
                    JournalRecoveryStates.NotResolvable => "Switch blocked: switch journal is not resolvable.",
                    JournalRecoveryStates.RestartRequired => "Switch blocked: application restart is required after journal resolution.",
                    _ => "Switch blocked: journal recovery state is unknown."
                };
                return false;
            }

            if (_activeTransactionId != null || !string.Equals(_currentState, NativeSwitchStates.Idle, StringComparison.Ordinal))
            {
                blockingReason = "Switch blocked: another switch transaction is currently in progress.";
                return false;
            }
        }

        if (_recoveryQuarantine.IsMarked || _sessionVault.IsQuarantined ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch").IsMarked ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked)
        {
            blockingReason = "Switch blocked: account lifecycle is quarantined.";
            return false;
        }

        if (SwitchGate.CurrentCount == 0)
        {
            blockingReason = "Switch blocked: another switch transaction is currently in progress.";
            return false;
        }

        blockingReason = null;
        return true;
    }

    private bool IsRecoveryAdmissionBlocked() => !CanAdmitSwitch(out _);

    private bool IsQuarantinedOrRecoveryUnresolved()
    {
        string recoveryState;
        lock (_statusLock) recoveryState = _journalRecoveryState;
        return !string.Equals(recoveryState, JournalRecoveryStates.None, StringComparison.Ordinal) ||
            _recoveryQuarantine.IsMarked ||
            _sessionVault.IsQuarantined ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath() + ".switch").IsMarked ||
            RecoveryQuarantineRegistry.Get(_sessionVault.GetVaultPath()).IsMarked;
    }

    private async Task<NativeSwitchResult> ObserveSwitchAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<Task<bool>>? planIsCurrentAsync,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IDisposable>>? acquireInterruptionAdmissionAsync = null)
    {
        if (!CanAdmitSwitch(out var blockingReason))
        {
            if (_isShuttingDown)
            {
                string now = DateTimeOffset.UtcNow.ToString("O");
                return new NativeSwitchResult(null, false, SwitchResultCodes.Cancelled,
                    NativeSwitchStates.Failed, targetAccountId?.Trim() ?? string.Empty, null, null, null,
                    blockingReason ?? "Switch coordinator is shutting down.", [], now, now);
            }
            if (IsQuarantinedOrRecoveryUnresolved())
            {
                return RecoveryUncertain(targetAccountId, blockingReason);
            }
            if (!string.Equals(_currentState, NativeSwitchStates.Idle, StringComparison.Ordinal) ||
                _activeTransactionId != null ||
                SwitchGate.CurrentCount == 0)
            {
                string now = DateTimeOffset.UtcNow.ToString("O");
                return new NativeSwitchResult(null, false, SwitchResultCodes.SwitchInProgress,
                    GetStatus().CurrentState, targetAccountId?.Trim() ?? string.Empty, null, null, null,
                    blockingReason ?? "Another account switch is already in progress.", [], now, now);
            }
            return RecoveryUncertain(targetAccountId, blockingReason);
        }

        // The inner task owns every switch gate/lease until it actually finishes.
        // Timeout detaches only the caller, never the mutation or its serialization.
        Task<NativeSwitchResult> operation = SwitchCoreAsync(
            targetAccountId, expectedActiveAccountId, planIsCurrentAsync, requiredWorkloadModelKey, minimumCandidateQuotaPercent, cancellationToken,
            acquireInterruptionAdmissionAsync);
        try
        {
            return await operation.WaitAsync(_transactionTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (operation.IsCompleted)
                return await operation.ConfigureAwait(false);
            _recoveryQuarantine.Mark();
            var uncertain = RecoveryUncertain(targetAccountId);
            lock (_statusLock)
            {
                _currentState = NativeSwitchStates.Failed;
                _lastResult = uncertain;
                if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
                {
                    _journalRecoveryState = JournalRecoveryStates.Unknown;
                }
            }
            _ = operation.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return uncertain;
        }
    }

    private NativeSwitchResult RecoveryUncertain(string? targetAccountId, string? message = null)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        string? activeTransactionId;
        lock (_statusLock) activeTransactionId = _activeTransactionId;
        return new NativeSwitchResult(activeTransactionId, false,
            SwitchResultCodes.SwitchFailedRollbackFailed, NativeSwitchStates.Failed,
            targetAccountId?.Trim() ?? string.Empty, null, null, null,
            message ?? "Switch outcome is unresolved; manual recovery is required before another switch.",
            [], now, now, ManualRecoveryRequired: true);
    }

    private async Task<NativeSwitchResult> SwitchCoreAsync(
        string targetAccountId, string? expectedActiveAccountId, Func<Task<bool>>? planIsCurrentAsync,
        string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<IDisposable>>? acquireInterruptionAdmissionAsync = null)
    {
        if (!CanAdmitSwitch(out var blockingReason))
        {
            if (_isShuttingDown)
            {
                string blockedNow = DateTimeOffset.UtcNow.ToString("O");
                return new NativeSwitchResult(null, false, SwitchResultCodes.Cancelled,
                    NativeSwitchStates.Failed, targetAccountId?.Trim() ?? string.Empty, null, null, null,
                    blockingReason ?? "Switch coordinator is shutting down.", [], blockedNow, blockedNow);
            }
            if (IsQuarantinedOrRecoveryUnresolved())
            {
                return RecoveryUncertain(targetAccountId, blockingReason);
            }
            if (!string.Equals(_currentState, NativeSwitchStates.Idle, StringComparison.Ordinal) ||
                _activeTransactionId != null ||
                SwitchGate.CurrentCount == 0)
            {
                string blockedNow = DateTimeOffset.UtcNow.ToString("O");
                return new NativeSwitchResult(null, false, SwitchResultCodes.SwitchInProgress,
                    GetStatus().CurrentState, targetAccountId?.Trim() ?? string.Empty, null, null, null,
                    blockingReason ?? "Another account switch is already in progress.", [], blockedNow, blockedNow);
            }
            return RecoveryUncertain(targetAccountId, blockingReason);
        }
        string requestedId = targetAccountId?.Trim() ?? string.Empty;
        string now = DateTimeOffset.UtcNow.ToString("O");
        bool entered;
        try
        {
            entered = await SwitchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new NativeSwitchResult(null, false, SwitchResultCodes.Cancelled,
                NativeSwitchStates.Failed, requestedId, null, null, null,
                "Switch request was cancelled before mutation.", [], now,
                DateTimeOffset.UtcNow.ToString("O"));
        }

        if (!entered)
        {
            return new NativeSwitchResult(null, false, SwitchResultCodes.SwitchInProgress,
                GetStatus().CurrentState, requestedId, null, null, null,
                "Another account switch is already in progress.", [], now, DateTimeOffset.UtcNow.ToString("O"));
        }

        try
        {
            if (_isShuttingDown)
            {
                return new NativeSwitchResult(null, false, SwitchResultCodes.Cancelled,
                    NativeSwitchStates.Failed, requestedId, null, null, null,
                    "Switch coordinator is shutting down.", [], now, DateTimeOffset.UtcNow.ToString("O"));
            }

            string transactionId = Guid.NewGuid().ToString("D");
            var stages = new List<string>();
            AccountMetadata? target = null;
            AccountMetadata? sourceAccount = null;
            string? previousAccountId = null;
            string? previousEmail = null;
            WinCredEntry? originalCredential = null;
            WinCredEntry? targetCredential = null;
            byte[]? targetSession = null;
            AG2ProcessSnapshot? processSnapshot = null;
            AG2ProcessGeneration? replacementGeneration = null;
            bool processTransitionStarted = false;
            bool targetActivationAttempted = false;
            bool credentialWriteAttempted = false;
            // Mirrors the activation provenance durably persisted for this transaction so the
            // ROLLING_BACK and QUARANTINED journal writes preserve it across crash recovery.
            // UNKNOWN until the RECORDED write persists NOT_ATTEMPTED; advanced to
            // MAY_HAVE_BEEN_ATTEMPTED when the CREDENTIAL_APPLYING write lands, strictly before
            // the target credential writer can be invoked. Never regresses.
            SwitchTargetActivationProvenance journalProvenance = SwitchTargetActivationProvenance.UNKNOWN;
            // Exact transaction journal ownership: null until a journal write successfully
            // commits, then the exact persisted entry. Cleanup is authorized only against
            // this value — a transaction that has not written a journal performs no journal
            // cleanup, and no path deletes an entry the transaction does not still own.
            SwitchJournalEntry? ownedJournalEntry = null;
            // Exact transition-marker ownership, same discipline as the journal entry: null
            // until a marker create-if-absent commits, advanced only from the successful
            // result, and cleared when exact marker cleanup is proven. The marker keeps at
            // least one durable recovery artifact on disk across every journal transition.
            SwitchTransitionMarkerEntry? ownedTransitionMarker = null;
            // Set when the transaction loses journal ownership to a foreign/replacement
            // entry mid-flight: all further journal writes are suppressed, the foreign
            // journal remains canonical recovery evidence, and required compensation still
            // runs without overwriting it.
            bool journalOwnershipLost = false;
            CrossProcessFileLease? switchLease = null;

            string switchResource = _sessionVault.GetVaultPath() + ".switch";
            var switchLock = PathLockRegistry.Get(switchResource);
            bool lockAcquired;
            try
            {
                lockAcquired = await switchLock.WaitAsync(_transactionTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller cancellation during the contended pre-mutation wait is an ordinary
                // cancelled request: no journal, lease, credential, or metadata mutation has
                // begun. Retire the transaction through the same terminal path the post-terminal
                // cleanup uses; an escaping exception here would be misread by callers as an
                // uncertain outcome and would demand manual recovery before any mutation.
                var result = Terminal(transactionId, false, SwitchResultCodes.Cancelled,
                    NativeSwitchStates.Failed, requestedId, null, null, null,
                    "Switch request was cancelled before credential mutation.", stages, now);
                bool journalProvenAbsent = await ProveTransactionReusableAsync(transactionId).ConfigureAwait(false);
                lock (_statusLock)
                {
                    if (journalProvenAbsent &&
                        _lastResult?.TransactionId == transactionId &&
                        !_lastResult.ManualRecoveryRequired &&
                        _journalRecoveryState == JournalRecoveryStates.None &&
                        !IsQuarantinedOrRecoveryUnresolved() &&
                        _currentState is NativeSwitchStates.Failed)
                    {
                        _currentState = NativeSwitchStates.Idle;
                    }
                }
                return result;
            }

            if (!lockAcquired)
            {
                _recoveryQuarantine.Mark();
                var uncertain = RecoveryUncertain(requestedId);
                lock (_statusLock)
                {
                    _currentState = NativeSwitchStates.Failed;
                    _lastResult = uncertain;
                    if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
                    {
                        _journalRecoveryState = JournalRecoveryStates.Unknown;
                    }
                }
                return uncertain;
            }

            SetActive(transactionId, NativeSwitchStates.Preflight);
            try
            {
            if (IsQuarantinedOrRecoveryUnresolved())
                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                    "Account lifecycle is unresolved; manual recovery is required.");
            switchLease = await CrossProcessFileLease
                .AcquireAsync(switchResource, cancellationToken)
                .ConfigureAwait(false);
            if (IsQuarantinedOrRecoveryUnresolved())
                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                    "Account lifecycle is unresolved; manual recovery is required.");

            // Admission freshness: in-memory recovery state can be stale relative to the
            // canonical journal path (a journal can appear after cleanup published clean).
            // Before performing any mutation, prove no unresolved journal exists; any journal
            // here fails closed into blocking recovery instead of opening admission.
            SwitchJournalReadResult admissionJournal;
            try
            {
                admissionJournal = await _journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                admissionJournal = SwitchJournalReadResult.IoError(ex);
            }

            if (admissionJournal.Status != SwitchJournalReadStatus.Absent)
            {
                MarkJournalRecoveryBlocked();
                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                    "An unresolved switch journal exists; recovery must reconcile it before switching can resume.");
            }

            // The transition marker path is part of admission: any valid, corrupt, or
            // unreadable marker is unresolved recovery evidence that forbids mutation.
            SwitchTransitionMarkerReadResult admissionMarker;
            try
            {
                admissionMarker = await _journalStore.ReadTransitionMarkerAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                admissionMarker = SwitchTransitionMarkerReadResult.IoError(ex);
            }

            if (admissionMarker.Status != SwitchTransitionMarkerReadStatus.Absent)
            {
            if (admissionMarker.Status is SwitchTransitionMarkerReadStatus.Corrupt
                or SwitchTransitionMarkerReadStatus.UnsupportedVersion)
            {
                lock (_statusLock)
                {
                    if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
                    {
                        _journalRecoveryState = JournalRecoveryStates.NotResolvable;
                    }
                }
            }
            else if (admissionMarker.Status == SwitchTransitionMarkerReadStatus.IoError)
            {
                lock (_statusLock)
                {
                    if (_journalRecoveryState != JournalRecoveryStates.RestartRequired &&
                        _journalRecoveryState == JournalRecoveryStates.None)
                    {
                        _journalRecoveryState = JournalRecoveryStates.Unknown;
                    }
                }
            }
            else
            {
                MarkJournalRecoveryBlocked();
            }
                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                    "An unresolved switch transition marker exists; recovery must reconcile it before switching can resume.");
            }

            if (planIsCurrentAsync != null && !await planIsCurrentAsync().ConfigureAwait(false))
                throw new SwitchRejectedException(SwitchResultCodes.Cancelled, "Automatic switch plan became stale.");

            if (string.IsNullOrWhiteSpace(requestedId))
            {
                throw new SwitchRejectedException(SwitchResultCodes.TargetNotFound, "Target account was not specified.");
            }

            target = await _accountStore.GetAccountAsync(requestedId, cancellationToken).ConfigureAwait(false)
                ?? throw new SwitchRejectedException(SwitchResultCodes.TargetNotFound, "Target account was not found.");
            if (!await _sessionVault.HasSessionAsync(target.Id, cancellationToken).ConfigureAwait(false))
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TargetNotVaulted,
                    "Target account does not have a valid vaulted session.");
            }

            previousAccountId = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
            if (planIsCurrentAsync != null &&
                (!await planIsCurrentAsync().ConfigureAwait(false) || !string.Equals(previousAccountId, expectedActiveAccountId, StringComparison.Ordinal)))
                throw new SwitchRejectedException(SwitchResultCodes.Cancelled, "Automatic switch plan became stale.");
            sourceAccount = previousAccountId == null ? null :
                await _accountStore.GetAccountAsync(previousAccountId, cancellationToken).ConfigureAwait(false);
            var currentIdentity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            previousEmail = currentIdentity?.Email;
            if (!SourceIdentityMatches(sourceAccount, currentIdentity))
                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                    "Live identity does not match the active account metadata.");
            if (previousAccountId == target.Id ||
                string.Equals(previousEmail, target.Email, StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(SwitchResultCodes.AlreadyActive, "Target account is already active.");
            }

            var status = await _adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.Connected || status.Status is "DEGRADED" or "OFFLINE" or "ERROR" || currentIdentity == null)
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TelemetryUnavailable,
                    "Antigravity telemetry is unavailable or degraded.");
            }
            var activity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
            if (activity.RunningTrajectories > 0 ||
                string.Equals(activity.State, "BUSY", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(SwitchResultCodes.Ag2Busy, "Antigravity must be IDLE before switching.");
            }
            if (!string.Equals(activity.State, "IDLE", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TelemetryUnavailable,
                    "Antigravity activity telemetry is unknown or degraded.");
            }
            stages.Add("PREFLIGHT_VERIFIED");

            SetState(NativeSwitchStates.Snapshotting);
            try
            {
                processSnapshot = await _processLifecycle.CaptureVerifiedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.UnsafeProcess,
                    $"Antigravity process provenance is unsafe: {AG2Security.SanitizeError(ex)}");
            }

            originalCredential = await CaptureCurrentCredentialAsync(cancellationToken)
                .ConfigureAwait(false);
            stages.Add("ROLLBACK_SNAPSHOT_CAPTURED");

            // Re-check both process generation and idle telemetry immediately before mutation.
            try
            {
                await _processLifecycle.RevalidateAsync(processSnapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (AG2ProcessLifecycleException ex)
            {
                throw new SwitchRejectedException(SwitchResultCodes.UnsafeProcess,
                    $"Antigravity process generation became unsafe: {AG2Security.SanitizeError(ex)}");
            }
            var finalActivity = await _adapter.GetActivityStateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _processLifecycle.RevalidateAsync(processSnapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (AG2ProcessLifecycleException ex)
            {
                throw new SwitchRejectedException(SwitchResultCodes.UnsafeProcess,
                    $"Antigravity process generation became unsafe: {AG2Security.SanitizeError(ex)}");
            }
            if (finalActivity.RunningTrajectories > 0 ||
                string.Equals(finalActivity.State, "BUSY", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(SwitchResultCodes.Ag2Busy, "Antigravity became busy before mutation.");
            }
            if (!string.Equals(finalActivity.State, "IDLE", StringComparison.OrdinalIgnoreCase))
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TelemetryUnavailable,
                    "Antigravity activity telemetry became unknown before mutation.");
            }

            var finalIdentity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!SourceIdentityMatches(sourceAccount, finalIdentity))
                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                    "Live identity changed before credential mutation.");

            if (planIsCurrentAsync != null &&
                (!await planIsCurrentAsync().ConfigureAwait(false) || !string.Equals(
                    await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false),
                    expectedActiveAccountId, StringComparison.Ordinal)))
                throw new SwitchRejectedException(SwitchResultCodes.Cancelled, "Automatic switch plan became stale before mutation.");

            targetSession = await _sessionVault.GetSessionAsync(target.Id, cancellationToken).ConfigureAwait(false);
            if (targetSession == null || targetSession.Length == 0 || targetSession.Length > MaxCredentialBlobBytes)
            {
                throw new SwitchRejectedException(
                    SwitchResultCodes.TargetNotVaulted,
                    "Target vaulted session is missing, empty, or outside credential bounds.");
            }
            // Vault loading is awaitable. Its result does not prove the live source
            // identity still matches the metadata used for this transaction.
            var identityAtStop = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            var activeAtStop = await _accountStore.GetActiveAccountIdAsync(cancellationToken).ConfigureAwait(false);
            var identityAfterActiveRead = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!SourceIdentityMatches(sourceAccount, identityAtStop) ||
                !SourceIdentityMatches(sourceAccount, identityAfterActiveRead) ||
                !string.Equals(activeAtStop, previousAccountId, StringComparison.Ordinal))
                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                    "Live or active identity changed before process stop.");
            // Quiesce the verified source before changing its credential. This prevents the
            // old generation from racing the transaction by persisting a newer session.
            _shutdownCts.Token.ThrowIfCancellationRequested();
            SetState(NativeSwitchStates.StoppingProcess);

            // Decouple from caller cancellation token at mutation boundary.
            var mutationTimeout = _processTimeout + _verificationTimeout + TimeSpan.FromSeconds(10);
            using var mutationCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            mutationCts.CancelAfter(mutationTimeout);
            var mutationToken = mutationCts.Token;

            // Phase Transition a: RECORDED — atomic create-if-absent (strictly before
            // StopVerifiedAsync). A foreign journal written after admission can never be
            // overwritten: the create succeeds only while the canonical path is genuinely
            // free at the filesystem commit boundary. No marker is needed for the initial
            // create: before this write commits, no destructive transaction action has
            // begun, and the create itself is atomic.
            var (recordedEntry, recordedMarker, recordedResult) = await TryAdvanceOwnedJournalAsync(
                ownedJournalEntry,
                ownedTransitionMarker,
                transactionId,
                SwitchJournalState.RECORDED,
                sourceAccount!.Id,
                target.Id,
                quarantineReasonCode: null,
                SwitchTargetActivationProvenance.NOT_ATTEMPTED,
                mutationToken).ConfigureAwait(false);
            ownedTransitionMarker = recordedMarker;
            if (recordedEntry == null)
            {
                await ClassifyForeignJournalRecoveryStateAsync().ConfigureAwait(false);
                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                    "A switch journal already occupies the canonical path; recovery must reconcile it before switching can resume.");
            }

            ownedJournalEntry = recordedEntry;
            journalProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED;

            // R02: Interruption admission lifecycle
            // When executing an automatic switch, acquireInterruptionAdmissionAsync is provided.
            // Admission is acquired inside verifyBeforeKillAsync, held across the process Kill() invocation,
            // and explicitly released in onStopIssued before waiting for the process to exit.
            // The finally block ensures that if any check throws, the admission lease is deterministically disposed.
            IDisposable? interruptionAdmission = null;
            try
            {
                await _processLifecycle.StopVerifiedAsync(processSnapshot, _processTimeout, mutationToken,
                        async token =>
                        {
                            if (_recoveryQuarantine.IsMarked || _sessionVault.IsQuarantined)
                                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                                    "Account lifecycle is unresolved at the process-stop boundary.");
                            string? authoritativeId = await _accountStore.GetActiveAccountIdAsync(token)
                                .ConfigureAwait(false);
                            var live = await _adapter.GetCurrentAccountAsync(token).ConfigureAwait(false);
                            if (!string.Equals(authoritativeId, previousAccountId, StringComparison.Ordinal) ||
                                !SourceIdentityMatches(sourceAccount, live))
                                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                                    "Live or active identity changed at the process-stop boundary.");
                            var stopActivity = await _adapter.GetActivityStateAsync(token).ConfigureAwait(false);
                            if (stopActivity.RunningTrajectories > 0 ||
                                string.Equals(stopActivity.State, "BUSY", StringComparison.OrdinalIgnoreCase))
                                throw new SwitchRejectedException(SwitchResultCodes.Ag2Busy,
                                    "Antigravity became busy at the process-stop boundary.");
                            if (!string.Equals(stopActivity.State, "IDLE", StringComparison.OrdinalIgnoreCase))
                                throw new SwitchRejectedException(SwitchResultCodes.TelemetryUnavailable,
                                    "Antigravity activity was not proven idle at the process-stop boundary.");
                            // Caller cancellation still applies while waiting for final admission:
                            // no source mutation has occurred yet. Do not hold router state ownership.
                            if (acquireInterruptionAdmissionAsync != null)
                            {
                                using var admissionCts = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                                interruptionAdmission = await acquireInterruptionAdmissionAsync(admissionCts.Token)
                                    .ConfigureAwait(false)
                                    ?? throw new InvalidOperationException("Automatic interruption admission did not provide ownership.");
                                admissionCts.Token.ThrowIfCancellationRequested();
                            }
                            if (planIsCurrentAsync != null && !await planIsCurrentAsync().ConfigureAwait(false))
                                throw new SwitchRejectedException(SwitchResultCodes.Cancelled,
                                    "Automatic quota evidence changed or expired at the process-stop boundary.");
                            if (_recoveryQuarantine.IsMarked || _sessionVault.IsQuarantined)
                                throw new SwitchRejectedException(SwitchResultCodes.SwitchFailedRollbackFailed,
                                    "Account lifecycle became unresolved at the process-stop boundary.");
                        },
                        onStopAttempted: () => processTransitionStarted = true,
                        onStopIssued: () =>
                        {
                            // Release only AFTER the kill request, never at the pre-kill notification.
                            Interlocked.Exchange(ref interruptionAdmission, null)?.Dispose();
                        })
                    .ConfigureAwait(false);
            }
            finally { Interlocked.Exchange(ref interruptionAdmission, null)?.Dispose(); }
            stages.Add("SOURCE_PROCESS_STOPPED");

            // The source may have refreshed WinCred after the provisional preflight read.
            // Once it is proven stopped, refresh the rollback snapshot before any overwrite.
            var quiescedCredential = await CaptureCurrentCredentialAsync(mutationToken)
                .ConfigureAwait(false);
            ZeroCredential(originalCredential);
            originalCredential = quiescedCredential;
            stages.Add("ROLLBACK_SNAPSHOT_REFRESHED_AFTER_QUIESCE");

            targetCredential = new WinCredEntry(
                WinCredTarget,
                originalCredential.Type,
                VaultConstants.DefaultAg2WinCredUserName,
                originalCredential.Persistence,
                targetSession);
            ValidateCredential(targetCredential, requireConfiguredTarget: true);

            // Phase Transition b: CREDENTIAL_APPLYING — marker-first exact-entry conditional
            // transition (strictly before WriteCredentialAsync). The durable transition
            // marker exists before the predecessor can be removed, so no crash boundary can
            // leave the canonical journal absent without recovery evidence. This same write
            // persists MAY_HAVE_BEEN_ATTEMPTED before the credential writer can be invoked,
            // so a throwing or partially-applied credential write still leaves durable proof
            // that the target activation boundary may have been crossed. If journal
            // ownership was lost to a foreign entry, the forward mutation must NOT run: the
            // source process is already stopped, so the verified rollback below is the safe
            // path.
            var (applyingEntry, applyingMarker, applyingResult) = await TryAdvanceOwnedJournalAsync(
                ownedJournalEntry,
                ownedTransitionMarker,
                transactionId,
                SwitchJournalState.CREDENTIAL_APPLYING,
                sourceAccount!.Id,
                target.Id,
                quarantineReasonCode: null,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED,
                mutationToken).ConfigureAwait(false);
            ownedTransitionMarker = applyingMarker;
            if (applyingEntry == null)
            {
                if (applyingResult.Status is SwitchJournalWriteStatus.PersistenceFailure or SwitchJournalWriteStatus.CleanupIncomplete)
                    throw new IOException("The credential-applying journal transition could not be persisted.", applyingResult.Exception);
                MarkJournalRecoveryBlocked();
                journalOwnershipLost = true;
                throw new SwitchJournalOwnershipLostException(
                    $"Journal transition to {SwitchJournalState.CREDENTIAL_APPLYING} failed ownership validation ({applyingResult.Status}); the canonical journal was preserved untouched.");
            }

            ownedJournalEntry = applyingEntry;
            journalProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED;

            SetState(NativeSwitchStates.ApplyingCredential);
            targetActivationAttempted = true;
            credentialWriteAttempted = true;
            bool written = await _winCredWriter.WriteCredentialAsync(targetCredential, mutationToken)
                .ConfigureAwait(false);
            if (!written) throw new InvalidOperationException("Credential writer reported failure.");
            await RequireCredentialEqualsAsync(targetCredential, mutationToken).ConfigureAwait(false);
            stages.Add("CREDENTIAL_APPLIED_AND_VERIFIED");

            SetState(NativeSwitchStates.Restarting);
            replacementGeneration = await _processLifecycle.LaunchAsync(processSnapshot, mutationToken)
                .ConfigureAwait(false);
            replacementGeneration = await _processLifecycle.WaitForHealthyReplacementAsync(
                processSnapshot,
                replacementGeneration,
                _verificationTimeout,
                mutationToken).ConfigureAwait(false);
            stages.Add("REPLACEMENT_PROCESS_VERIFIED");

            SetState(NativeSwitchStates.Verifying);
            await VerifyIdentityAsync(target.Email, replacementGeneration, mutationToken).ConfigureAwait(false);
            stages.Add("TARGET_IDENTITY_VERIFIED");

            List<AccountModelQuotaObservation>? verifiedTargetObservations = null;
            if (!string.IsNullOrWhiteSpace(requiredWorkloadModelKey))
            {
                AccountModelQuotaObservation? cachedEvidence = null;
                try
                {
                    if (_quotaObservationStore != null)
                        cachedEvidence = await _quotaObservationStore.GetObservationAsync(target.Id,
                            requiredWorkloadModelKey, mutationToken).ConfigureAwait(false);
                    verifiedTargetObservations = await VerifyTargetQuotaAsync(
                        target, replacementGeneration, requiredWorkloadModelKey,
                        minimumCandidateQuotaPercent, stages, mutationToken).ConfigureAwait(false);
                }
                catch (SwitchVerificationException)
                {
                    if (_quotaObservationStore != null && target != null)
                    {
                        await InvalidateTargetQuotaObservationsAsync(target.Id, stages).ConfigureAwait(false);
                    }
                    throw;
                }
            }

            SetState(NativeSwitchStates.Finalizing);
            if (!await _processLifecycle.IsGenerationCurrentAsync(replacementGeneration, mutationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Process generation changed after target verification and before metadata finalization.");
            }

            // Phase Transition c: TARGET_IDENTITY_VERIFIED_PRECOMMIT — marker-first
            // exact-entry conditional transition (strictly before TryFinalizeSwitchAsync).
            // Losing journal ownership here (after the target credential was applied and the
            // target verified) routes into the verified rollback below: target evidence is
            // invalidated and the source is restored, while the foreign journal survives.
            var (precommitEntry, precommitMarker, precommitResult) = await TryAdvanceOwnedJournalAsync(
                ownedJournalEntry,
                ownedTransitionMarker,
                transactionId,
                SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT,
                sourceAccount!.Id,
                target.Id,
                quarantineReasonCode: null,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED,
                mutationToken).ConfigureAwait(false);
            ownedTransitionMarker = precommitMarker;
            if (precommitEntry == null)
            {
                if (precommitResult.Status is SwitchJournalWriteStatus.PersistenceFailure or SwitchJournalWriteStatus.CleanupIncomplete)
                    throw new IOException("The precommit journal transition could not be persisted.", precommitResult.Exception);
                MarkJournalRecoveryBlocked();
                journalOwnershipLost = true;
                throw new SwitchJournalOwnershipLostException(
                    $"Journal transition to {SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT} failed ownership validation ({precommitResult.Status}); the canonical journal was preserved untouched.");
            }

            ownedJournalEntry = precommitEntry;

            var updated = await _accountStore.TryFinalizeSwitchAsync(previousAccountId, target.Id, new UpdateAccountInput(
                ValidationStatus: AccountValidationStatus.Valid,
                HasVaultedSession: true,
                LastActiveAt: DateTimeOffset.UtcNow.ToString("O")), mutationToken).ConfigureAwait(false);
            if (updated == null)
                throw new InvalidOperationException("Account metadata changed during atomic switch finalization.");
            stages.Add("METADATA_COMMITTED");

            if (_quotaObservationStore != null && verifiedTargetObservations != null && verifiedTargetObservations.Count > 0)
            {
                try
                {
                    await _quotaObservationStore.RecordCompleteSnapshotAsync(
                        target.Id, verifiedTargetObservations, verifiedTargetObservations[0].ObservedAtUtc,
                        "LiveTargetVerification", mutationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort derived telemetry persistence; transaction is already committed.
                }
            }

            // Phase Transition d: Success Deletion (only after TryFinalizeSwitchAsync succeeds).
            // Exact-entry conditional cleanup with a post-deletion path proof: the transaction
            // may delete only the PRECOMMIT entry it wrote, and deletion alone does not prove
            // the canonical path clear. The switch result remains a business success; a
            // surviving or mismatched journal keeps recovery blocking for reconciliation.
            bool commitCleanupClear = ownedJournalEntry != null &&
                await CleanupOwnedJournalEntryAsync(ownedJournalEntry, ownedTransitionMarker).ConfigureAwait(false);
            if (!commitCleanupClear)
            {
                stages.Add("JOURNAL_CLEANUP_REQUIRES_RECONCILIATION");
            }

            return Terminal(transactionId, true, SwitchResultCodes.Success, NativeSwitchStates.Complete,
                target.Id, target.Email, previousAccountId, previousEmail,
                "Target account was activated and verified." +
                (commitCleanupClear ? "" : " Journal cleanup requires reconciliation; recovery is blocking."),
                stages, now);
        }
        catch (SwitchRejectedException ex) when (!processTransitionStarted)
        {
            // A transaction that has not written a journal performs no cleanup at all: an
            // unrelated journal on the canonical path belongs to another owner and must
            // survive for its own reconciliation. The transaction-exit proof below publishes
            // blocking recovery when a foreign journal is present.
            if (ownedJournalEntry != null)
                await CleanupOwnedJournalEntryAsync(ownedJournalEntry, ownedTransitionMarker).ConfigureAwait(false);
            return Terminal(transactionId, false, ex.Code, NativeSwitchStates.Failed,
                requestedId, target?.Email, previousAccountId, previousEmail,
                AG2Security.RedactSensitiveText(ex.Message), stages, now);
        }
        catch (OperationCanceledException) when (!processTransitionStarted)
        {
            if (ownedJournalEntry != null)
                await CleanupOwnedJournalEntryAsync(ownedJournalEntry, ownedTransitionMarker).ConfigureAwait(false);
            return Terminal(transactionId, false, SwitchResultCodes.Cancelled, NativeSwitchStates.Failed,
                requestedId, target?.Email, previousAccountId, previousEmail,
                "Switch request was cancelled before credential mutation.", stages, now);
        }
        catch (Exception switchError)
        {
            if (!processTransitionStarted || originalCredential == null || processSnapshot == null)
            {
                if (ownedJournalEntry != null)
                    await CleanupOwnedJournalEntryAsync(ownedJournalEntry, ownedTransitionMarker).ConfigureAwait(false);
                return Terminal(transactionId, false,
                    switchError is OperationCanceledException ? SwitchResultCodes.Cancelled : SwitchResultCodes.TelemetryUnavailable,
                    NativeSwitchStates.Failed, requestedId, target?.Email, previousAccountId, previousEmail,
                    SafeFailure(switchError), stages, now);
            }

            SetState(NativeSwitchStates.RollingBack);
            stages.Add("ROLLBACK_STARTED");

            // If genuine target activation was attempted, invalidate target quota evidence
            // so candidate selector cannot repeatedly retry this target using stale positive
            // observations once rollback and cooldown finish. Pre-activation failures (such as
            // source stop failure or snapshot failure) preserve target observations intact.
            string failedTargetId = target?.Id ?? requestedId;
            if (targetActivationAttempted && _quotaObservationStore != null && !string.IsNullOrWhiteSpace(failedTargetId))
            {
                await InvalidateTargetQuotaObservationsAsync(failedTargetId, stages).ConfigureAwait(false);
            }

            // Phase Transition e: ROLLING_BACK (strictly before QuiesceForRollbackAsync or restoring credentials) —
            // marker-first exact-entry conditional transition. Two distinct failure shapes:
            // ownership loss (a foreign journal or foreign marker replaced/occupies the
            // recovery evidence) still requires the pending compensation and must never
            // overwrite the foreign evidence; a plain persistence failure leaves the prior
            // owned journal — and the durable marker, when one was created — on disk and
            // keeps the critical no-compensation-under-unrecorded-rollback rule.
            try
            {
                var (rollingBackEntry, rollingBackMarker, rollingBackResult) = await TryAdvanceOwnedJournalAsync(
                    ownedJournalEntry,
                    ownedTransitionMarker,
                    transactionId,
                    SwitchJournalState.ROLLING_BACK,
                    sourceAccount?.Id ?? previousAccountId ?? string.Empty,
                    target?.Id ?? requestedId,
                    quarantineReasonCode: null,
                    journalProvenance,
                    CancellationToken.None).ConfigureAwait(false);
                ownedTransitionMarker = rollingBackMarker;
                if (rollingBackEntry != null)
                {
                    ownedJournalEntry = rollingBackEntry;
                    // A covered publication failure can recover exact ownership through
                    // the predecessor bridge. This successful write now owns rollback;
                    // foreign evidence never reaches this branch.
                    journalOwnershipLost = false;
                }
                else if (rollingBackResult.Status == SwitchJournalWriteStatus.CleanupIncomplete)
                {
                    ownedJournalEntry = rollingBackResult.DurableSuccessor;
                    journalOwnershipLost = false;
                    MarkJournalRecoveryBlocked();
                    stages.Add("JOURNAL_CLEANUP_REQUIRES_RECONCILIATION");
                }
                else if (rollingBackResult.Status is SwitchJournalWriteStatus.NotMatched
                    or SwitchJournalWriteStatus.AlreadyExists
                    or SwitchJournalWriteStatus.Absent)
                {
                    journalOwnershipLost = true;
                    MarkJournalRecoveryBlocked();
                    stages.Add("JOURNAL_OWNERSHIP_LOST");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Failed to persist the ROLLING_BACK journal state: {rollingBackResult.Message ?? rollingBackResult.Status.ToString()}");
                }
            }
            catch (Exception rollbackJournalError)
            {
                // CRITICAL SAFETY RULE: If writing ROLLING_BACK fails/throws:
                // Do NOT call QuiesceForRollbackAsync, do NOT restore credentials under unrecorded rollback!
                // Leave prior conservative journal state (CREDENTIAL_APPLYING or RECORDED) on disk.
                _recoveryQuarantine.Mark();
                stages.Add("ROLLBACK_FAILED");
                return Terminal(transactionId, false, SwitchResultCodes.SwitchFailedRollbackFailed,
                    NativeSwitchStates.Failed, requestedId, target?.Email, previousAccountId, previousEmail,
                    $"Switch and rollback failed; manual recovery is required. {SafeFailure(rollbackJournalError)}",
                    stages, now);
            }

            try
            {
                using var rollbackCts = new CancellationTokenSource(_rollbackTimeout);
                var rollbackToken = rollbackCts.Token;
                await _processLifecycle.QuiesceForRollbackAsync(
                    processSnapshot,
                    replacementGeneration,
                    _processTimeout,
                    rollbackToken).ConfigureAwait(false);
                stages.Add("SWITCH_PROCESS_QUIESCED");

                if (credentialWriteAttempted)
                {
                    if (targetCredential == null)
                        throw new InvalidOperationException("Applied credential snapshot was unavailable for rollback.");
                    await RestoreCredentialConditionallyAsync(originalCredential, targetCredential, rollbackToken)
                        .ConfigureAwait(false);
                    stages.Add("ORIGINAL_CREDENTIAL_RESTORED");
                }
                else
                {
                    stages.Add("CREDENTIAL_UNCHANGED");
                }

                var restoredGeneration = await _processLifecycle.RestoreAsync(
                    processSnapshot,
                    _verificationTimeout,
                    rollbackToken).ConfigureAwait(false);
                stages.Add("SOURCE_PROCESS_RESTORED");

                if (string.IsNullOrWhiteSpace(previousEmail))
                {
                    throw new InvalidOperationException("Original identity was unavailable for rollback verification.");
                }
                await VerifyIdentityAsync(previousEmail, restoredGeneration, rollbackToken)
                    .ConfigureAwait(false);
                stages.Add("SOURCE_IDENTITY_VERIFIED");

                // Phase Transition f: Rollback Success Deletion. Exact-entry conditional
                // cleanup of the owned journal with a post-deletion path proof; the verified
                // rollback remains the business outcome, while a surviving or mismatched
                // journal keeps recovery blocking.
                bool rollbackCleanupClear = true;
                if (!_recoveryQuarantine.IsMarked && ownedJournalEntry != null)
                {
                    // If durable evidence rejection failed, retain the recovery
                    // journal so restart cannot clear an in-memory-only quarantine
                    // and route using the same disproven healthy observation.
                    rollbackCleanupClear = await CleanupOwnedJournalEntryAsync(ownedJournalEntry, ownedTransitionMarker).ConfigureAwait(false);
                }

                string failureDetail = SafeFailure(switchError);
                string cleanupNote = rollbackCleanupClear
                    ? ""
                    : " Journal cleanup requires reconciliation; recovery is blocking.";
                string message = string.IsNullOrWhiteSpace(failureDetail)
                    ? $"Switch failed; the original credential and verified source identity were restored.{cleanupNote}"
                    : $"Switch failed; the original credential and verified source identity were restored. {failureDetail}{cleanupNote}";

                return Terminal(transactionId, false, SwitchResultCodes.SwitchFailedRolledBack,
                    NativeSwitchStates.RolledBack, requestedId, target?.Email, previousAccountId, previousEmail,
                    message, stages, now);
            }
            catch (Exception rollbackError)
            {
                // Phase Transition g: Rollback Failure / Uncertain Quarantine
                stages.Add("ROLLBACK_FAILED");
                _recoveryQuarantine.Mark();

                if (journalOwnershipLost)
                {
                    // Journal ownership was already lost to a foreign entry: do not attempt
                    // to overwrite it with the QUARANTINED state. The in-process quarantine
                    // plus the surviving foreign journal keep recovery blocked.
                }
                else
                {
                    try
                    {
                        var (quarantinedEntry, quarantinedMarker, _) = await TryAdvanceOwnedJournalAsync(
                            ownedJournalEntry,
                            ownedTransitionMarker,
                            transactionId,
                            SwitchJournalState.QUARANTINED,
                            sourceAccount?.Id ?? previousAccountId ?? string.Empty,
                            target?.Id ?? requestedId,
                            quarantineReasonCode: "ROLLBACK_FAILED",
                            journalProvenance,
                            CancellationToken.None).ConfigureAwait(false);
                        ownedTransitionMarker = quarantinedMarker;
                        if (quarantinedEntry != null)
                        {
                            ownedJournalEntry = quarantinedEntry;
                        }
                    }
                    catch (Exception)
                    {
                        // If persisting QUARANTINED throws: the in-process quarantine is
                        // already marked and the terminal result stays manual-recovery.
                    }
                }

                return Terminal(transactionId, false, SwitchResultCodes.SwitchFailedRollbackFailed,
                    NativeSwitchStates.Failed, requestedId, target?.Email, previousAccountId, previousEmail,
                    $"Switch and rollback failed; manual recovery is required. {SafeFailure(rollbackError)}",
                    stages, now);
            }
        }
        finally
        {
            ZeroCredential(originalCredential);
            if (targetCredential != null && !ReferenceEquals(targetCredential.Blob, targetSession))
            {
                ZeroCredential(targetCredential);
            }
            if (targetSession != null) CryptographicOperations.ZeroMemory(targetSession);
            Exception? cleanupError = null;
            bool journalProvenAbsent = false;
            try
            {
                if (BeforeLeaseReleaseAsync is { } beforeRelease)
                    await beforeRelease().ConfigureAwait(false);
                // Retain switch ownership while proving that terminal cleanup left
                // no recovery artifact. A terminal result alone is not that proof.
                journalProvenAbsent = await ProveTransactionReusableAsync(transactionId).ConfigureAwait(false);
                if (switchLease != null) await switchLease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupError = ex;
                // Even an injected/diagnostic release failure must still attempt
                // to release the actual lease before lifting in-process ownership.
                if (switchLease != null)
                {
                    try { await switchLease.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception releaseError)
                    {
                        cleanupError = new AggregateException(ex, releaseError);
                    }
                }
            }
            finally
            {
                switchLock.Release();
                lock (_statusLock)
                {
                    _activeTransactionId = null;
                    if (cleanupError == null && journalProvenAbsent &&
                        _lastResult?.TransactionId == transactionId && !_lastResult.ManualRecoveryRequired &&
                        _journalRecoveryState == JournalRecoveryStates.None &&
                        !IsQuarantinedOrRecoveryUnresolved() &&
                        _currentState is NativeSwitchStates.Complete or NativeSwitchStates.RolledBack or NativeSwitchStates.Failed)
                        _currentState = NativeSwitchStates.Idle;
                }
            }
            if (cleanupError != null)
            {
                lock (_statusLock)
                {
                    if (_lastResult?.TransactionId == transactionId && _lastResult.ManualRecoveryRequired)
                    {
                        _lastResult = _lastResult with
                        {
                            Message = _lastResult.Message + " Cleanup also failed; manual recovery remains required."
                        };
                    }
                    else
                    {
                        if (_lastResult?.TransactionId == transactionId)
                        {
                            _lastResult = _lastResult with
                            {
                                Success = false,
                                Code = SwitchResultCodes.SwitchFailedRollbackFailed,
                                State = NativeSwitchStates.Failed,
                                ManualRecoveryRequired = true,
                                Message = "Switch cleanup failed; manual recovery is required."
                            };
                        }
                        _currentState = NativeSwitchStates.Failed;
                        throw new InvalidOperationException(
                            "Switch cleanup failed; outcome requires manual recovery.", cleanupError);
                    }
                }
            }
        }
        }
        finally
        {
            SwitchGate.Release();
        }
    }

    private async Task<bool> ProveTransactionReusableAsync(string transactionId)
    {
        lock (_statusLock)
        {
            if (_lastResult?.TransactionId != transactionId || _lastResult.ManualRecoveryRequired ||
                IsQuarantinedOrRecoveryUnresolved()) return false;
        }
        SwitchJournalReadResult journal;
        SwitchTransitionMarkerReadResult marker;
        try
        {
            using var deadline = new CancellationTokenSource(_transactionTimeout);
            journal = await _journalStore.ReadAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (journal.Status == SwitchJournalReadStatus.Absent)
            {
                // Terminal cleanup is not proven complete while any transition marker
                // remains: the marker path is part of the reusable-admission proof.
                marker = await _journalStore
                    .ReadTransitionMarkerAsync(deadline.Token)
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
                if (marker.Status == SwitchTransitionMarkerReadStatus.Absent) return true;

                lock (_statusLock)
                {
                    if (_journalRecoveryState == JournalRecoveryStates.None)
                        _journalRecoveryState = marker.Status switch
                        {
                            SwitchTransitionMarkerReadStatus.Valid => JournalRecoveryStates.ActionRequired,
                            SwitchTransitionMarkerReadStatus.Corrupt or SwitchTransitionMarkerReadStatus.UnsupportedVersion
                                => JournalRecoveryStates.NotResolvable,
                            _ => JournalRecoveryStates.Unknown
                        };
                }
                return false;
            }

            marker = SwitchTransitionMarkerReadResult.Absent();
        }
        catch
        {
            lock (_statusLock)
            {
                if (_journalRecoveryState == JournalRecoveryStates.None)
                    _journalRecoveryState = JournalRecoveryStates.Unknown;
            }
            return false;
        }
        lock (_statusLock)
        {
            // Never clear established recovery state while retiring a transaction.
            if (_journalRecoveryState == JournalRecoveryStates.None)
                _journalRecoveryState = journal.Status switch
                {
                    SwitchJournalReadStatus.Valid => JournalRecoveryStates.ActionRequired,
                    SwitchJournalReadStatus.Corrupt or SwitchJournalReadStatus.UnsupportedVersion => JournalRecoveryStates.NotResolvable,
                    _ => JournalRecoveryStates.Unknown
                };
        }
        return false;
    }

    private async Task<WinCredEntry> CaptureCurrentCredentialAsync(CancellationToken cancellationToken)
    {
        var readCredential = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SwitchRejectedException(
                SwitchResultCodes.TelemetryUnavailable,
                "Current credential is unavailable for rollback snapshotting.");
        try
        {
            ValidateCredential(readCredential, requireConfiguredTarget: true);
            return CloneCredential(readCredential);
        }
        finally
        {
            ZeroCredential(readCredential);
        }
    }

    private async Task VerifyIdentityAsync(
        string expectedEmail,
        AG2ProcessGeneration expectedGeneration,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _verificationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _processLifecycle.IsGenerationCurrentAsync(expectedGeneration, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("Process generation changed during identity verification.");
            }

            var identity = await _adapter.GetCurrentAccountAsync(cancellationToken).ConfigureAwait(false);
            if (!await _processLifecycle.IsGenerationCurrentAsync(expectedGeneration, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("Telemetry came from a stale process generation.");
            }
            if (identity != null)
            {
                if (string.Equals(identity.Email, expectedEmail, StringComparison.OrdinalIgnoreCase)) return;
                throw new InvalidOperationException("Post-restart identity did not match the requested account.");
            }
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("Timed out waiting for verified account identity telemetry.");
    }

    private async Task RestoreCredentialConditionallyAsync(WinCredEntry original, WinCredEntry applied, CancellationToken cancellationToken)
    {
        var current = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Credential disappeared during rollback.");
        try
        {
            if (CredentialsEqual(current, original)) return;
            if (!CredentialsEqual(current, applied))
            {
                throw new InvalidOperationException(
                    "Credential changed outside this switch attempt; refusing to overwrite newer state.");
            }
        }
        finally
        {
            ZeroCredential(current);
        }

        bool restored = await _winCredWriter.WriteCredentialAsync(original, cancellationToken)
            .ConfigureAwait(false);
        if (!restored) throw new InvalidOperationException("Credential rollback writer reported failure.");
        await RequireCredentialEqualsAsync(original, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireCredentialEqualsAsync(WinCredEntry expected, CancellationToken cancellationToken)
    {
        var actual = await _winCredReader.ReadCredentialAsync(WinCredTarget, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Credential read-back was missing.");
        try
        {
            if (!CredentialsEqual(actual, expected))
            {
                throw new InvalidOperationException("Credential read-back verification failed.");
            }
        }
        finally
        {
            ZeroCredential(actual);
        }
    }

    private static bool CredentialsEqual(WinCredEntry left, WinCredEntry right) =>
        string.Equals(left.Target, right.Target, StringComparison.OrdinalIgnoreCase) &&
        left.Type == right.Type &&
        string.Equals(left.UserName, right.UserName, StringComparison.Ordinal) &&
        left.Persistence == right.Persistence &&
        left.Blob.Length == right.Blob.Length &&
        CryptographicOperations.FixedTimeEquals(left.Blob, right.Blob);

    private static WinCredEntry CloneCredential(WinCredEntry source) =>
        new(source.Target, source.Type, source.UserName, source.Persistence, (byte[])source.Blob.Clone());

    private static void ValidateCredential(WinCredEntry entry, bool requireConfiguredTarget)
    {
        if (requireConfiguredTarget && !string.Equals(entry.Target, WinCredTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Credential target does not match the configured Antigravity target.");
        if (entry.Target.IndexOf('\0') >= 0 || entry.UserName?.IndexOf('\0') >= 0)
            throw new InvalidDataException("Credential text contains an embedded null.");
        if (string.IsNullOrWhiteSpace(entry.UserName) ||
            entry.UserName.Length > VaultConstants.MaxAg2WinCredUserNameLength)
            throw new InvalidDataException("Credential username is structurally invalid.");
        if (entry.Type != 1 || entry.Persistence is < 1 or > 3)
            throw new InvalidDataException("Credential type or persistence is unsupported.");
        if (entry.Blob == null || entry.Blob.Length is < 1 or > MaxCredentialBlobBytes)
            throw new InvalidDataException("Credential blob is empty or outside Windows bounds.");
    }

    private static void ZeroCredential(WinCredEntry? entry)
    {
        if (entry?.Blob is { Length: > 0 }) CryptographicOperations.ZeroMemory(entry.Blob);
    }

    private static bool SourceIdentityMatches(AccountMetadata? source, AccountIdentityDto? live) =>
        source != null && !string.IsNullOrWhiteSpace(source.Email) &&
        !string.IsNullOrWhiteSpace(live?.Email) &&
        string.Equals(source.Email.Trim(), live.Email.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string SafeFailure(Exception error) =>
        error is OperationCanceledException
            ? "Switch was cancelled during a protected phase."
            : AG2Security.SanitizeError(error);

    private async Task InvalidateTargetQuotaObservationsAsync(string targetAccountId, List<string> stages)
    {
        if (_quotaObservationStore == null || string.IsNullOrWhiteSpace(targetAccountId))
            return;

        try
        {
            var now = _timeProvider.GetUtcNow();
            await _quotaObservationStore.InvalidateObservationsForAccountAsync(
                targetAccountId, now, CancellationToken.None).ConfigureAwait(false);
            if (!stages.Contains("TARGET_QUOTA_EVIDENCE_INVALIDATED"))
            {
                stages.Add("TARGET_QUOTA_EVIDENCE_INVALIDATED");
            }
        }
        catch
        {
            _recoveryQuarantine.Mark();
            if (!stages.Contains("TARGET_QUOTA_INVALIDATION_FAILED"))
            {
                stages.Add("TARGET_QUOTA_INVALIDATION_FAILED");
            }
        }
    }

    private void SetActive(string transactionId, string state)
    {
        lock (_statusLock)
        {
            _activeTransactionId = transactionId;
            if (!_recoveryQuarantine.IsMarked) _currentState = state;
        }
    }

    private void SetState(string state)
    {
        lock (_statusLock)
        {
            if (!_recoveryQuarantine.IsMarked) _currentState = state;
        }
    }

    private NativeSwitchResult Terminal(
        string? transactionId,
        bool success,
        string code,
        string state,
        string targetAccountId,
        string? targetEmail,
        string? previousAccountId,
        string? previousEmail,
        string message,
        IReadOnlyList<string> stages,
        string startedAt)
    {
        var result = new NativeSwitchResult(transactionId, success, code, state,
            targetAccountId, targetEmail, previousAccountId, previousEmail,
            AG2Security.RedactSensitiveText(message), stages.ToArray(), startedAt,
            DateTimeOffset.UtcNow.ToString("O"),
            ManualRecoveryRequired: code == SwitchResultCodes.SwitchFailedRollbackFailed);
        lock (_statusLock)
        {
            if (_recoveryQuarantine.IsMarked)
            {
                result = result with
                {
                    Success = false,
                    Code = SwitchResultCodes.SwitchFailedRollbackFailed,
                    State = NativeSwitchStates.Failed,
                    ManualRecoveryRequired = true,
                    Message = "Switch outcome remained quarantined; manual recovery is required."
                };
                state = NativeSwitchStates.Failed;
                if (_journalRecoveryState != JournalRecoveryStates.RestartRequired &&
                    _journalRecoveryState != JournalRecoveryStates.Unknown &&
                    _journalRecoveryState != JournalRecoveryStates.NotResolvable)
                {
                    _journalRecoveryState = JournalRecoveryStates.ActionRequired;
                }
            }
            _currentState = state;
            _lastResult = result;
        }
        return result;
    }

    /// <summary>
    /// Persists the next journal state ownership-conditionally under the durable transition
    /// marker protocol. With no owned entry the state is created atomically if and only if
    /// the canonical path is still absent — no marker is required, because before that
    /// create commits the transaction has performed no destructive action and the create
    /// itself is atomic. With an owned entry the transition is: durably create the exact
    /// marker (expected = owned entry, next = proposed entry) if absent; rotate an existing
    /// marker only when it exactly matches previously acquired marker ownership, retaining
    /// a proved, pinned canonical predecessor across that rotation; then replace the owned entry with
    /// the store's delete-then-publish primitive (whose successor bytes are prepared before
    /// the predecessor is removed); then remove the exact owned marker. At every durable
    /// boundary at least one recovery artifact remains: the canonical journal, or the
    /// marker, or both. A failed publication may leave only the marker; an exact predecessor
    /// restoration may leave both. Foreign recovery evidence is never overwritten. Journal and marker
    /// ownership advance only from successful results — never adopted from disk.
    /// </summary>
    // Freezes the durability boundary after rotating away the old marker, before its
    // replacement is published. Production leaves this fault-injection seam unset.
    internal Func<Task>? AfterOwnedMarkerRotationDeleteAsync { get; set; }

    private async Task<(SwitchJournalEntry? Entry, SwitchTransitionMarkerEntry? OwnedMarker, SwitchJournalWriteResult Result)>
        TryAdvanceOwnedJournalAsync(
            SwitchJournalEntry? ownedJournalEntry,
            SwitchTransitionMarkerEntry? ownedTransitionMarker,
            string transactionId,
            SwitchJournalState state,
            string sourceAccountId,
            string targetAccountId,
            string? quarantineReasonCode,
            SwitchTargetActivationProvenance provenance,
            CancellationToken cancellationToken)
    {
        var next = new SwitchJournalEntry
        {
            Magic = SwitchJournalEntry.CurrentMagic,
            SchemaVersion = SwitchJournalEntry.CurrentSchemaVersion,
            TransactionId = transactionId,
            State = state,
            UpdatedAt = DateTimeOffset.UtcNow,
            SourceAccountId = sourceAccountId,
            TargetAccountId = targetAccountId,
            QuarantineReasonCode = quarantineReasonCode,
            TargetActivationProvenance = provenance
        };

        if (ownedJournalEntry == null)
        {
            var create = await _journalStore.CreateIfAbsentAsync(next, cancellationToken).ConfigureAwait(false);
            return (create.Status == SwitchJournalWriteStatus.Created ? create.Entry : null, ownedTransitionMarker, create);
        }

        // STEP 1-2: the transition marker must be durable before the predecessor can be
        // removed. An occupant is never adopted: only an exact previously-owned marker
        // may be rotated, with its canonical predecessor serving as a durable bridge.
        var marker = new SwitchTransitionMarkerEntry
        {
            Magic = SwitchTransitionMarkerEntry.CurrentMagic,
            SchemaVersion = SwitchTransitionMarkerEntry.CurrentSchemaVersion,
            TransactionId = transactionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Expected = ownedJournalEntry,
            Next = next
        };

        var markerCreate = await _journalStore
            .CreateTransitionMarkerIfAbsentAsync(marker, cancellationToken)
            .ConfigureAwait(false);
        if (markerCreate.Status == SwitchTransitionMarkerWriteStatus.AlreadyExists)
        {
            // Only the marker acquired from a successful persistence result is owned.
            // Matching a transaction ID or predecessor never promotes an occupant to owned.
            SwitchTransitionMarkerReadResult occupant;
            try
            {
                occupant = await _journalStore
                    .ReadTransitionMarkerAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                occupant = SwitchTransitionMarkerReadResult.IoError(ex);
            }

            bool ownsOccupant = occupant.Status == SwitchTransitionMarkerReadStatus.Valid &&
                occupant.Marker != null &&
                ownedTransitionMarker != null &&
                SwitchTransitionMarkerStore.MarkersMatch(occupant.Marker, ownedTransitionMarker) &&
                SwitchJournalStore.EntriesMatch(ownedTransitionMarker.Expected, ownedJournalEntry);
            if (!ownsOccupant)
            {
                await ClassifyForeignMarkerRecoveryStateAsync().ConfigureAwait(false);
                return (null, ownedTransitionMarker, SwitchJournalWriteResult.AlreadyExists(
                    "A transition marker already occupies the marker path; recovery must reconcile it before this transaction can transition its journal."));
            }

            // A failed successor publication may already have deleted the predecessor.
            // Restore only the exact predecessor we persisted, without replacing any
            // occupant, and retain the old marker until its canonical bridge is proven.
            var bridge = await RestoreOwnedMarkerPredecessorAsync(ownedTransitionMarker!, cancellationToken)
                .ConfigureAwait(false);
            if (bridge != null)
            {
                MarkJournalRecoveryBlocked();
                return (null, ownedTransitionMarker, bridge);
            }

            try
            {
                // Unlike the conditional-delete handle, this short-lived read guard does
                // NOT share delete/write access. Revalidate through it and keep A pinned
                // across the rotation so an external rename/delete cannot remove the
                // bridge while it is the only recovery artifact.
                var guardResult = await _journalStore.AcquireExactJournalGuardAsync(ownedTransitionMarker!.Expected, cancellationToken).ConfigureAwait(false);
                if (guardResult.Status != ExactJournalGuardStatus.Acquired || guardResult.Guard == null)
                {
                    MarkJournalRecoveryBlocked();
                    return (null, ownedTransitionMarker, guardResult.Status switch
                    {
                        ExactJournalGuardStatus.Absent => SwitchJournalWriteResult.Absent(
                            "The canonical predecessor disappeared before marker rotation; the marker was preserved."),
                        ExactJournalGuardStatus.NotMatched => SwitchJournalWriteResult.NotMatched(
                            "The canonical predecessor changed before marker rotation; both occupants were preserved."),
                        _ => SwitchJournalWriteResult.PersistenceFailure(
                            $"Failed to guard the canonical predecessor: {guardResult.Message}", guardResult.Exception)
                    });
                }
                using var predecessorGuard = guardResult.Guard;











                var ownDelete = await _journalStore
                    .DeleteTransitionMarkerIfUnchangedAsync(ownedTransitionMarker!, cancellationToken)
                    .ConfigureAwait(false);
                if (ownDelete.Status != SwitchTransitionMarkerDeleteStatus.Deleted)
                {
                    MarkJournalRecoveryBlocked();
                    return (null, ownedTransitionMarker, SwitchJournalWriteResult.AlreadyExists(
                        "The exact owned transition marker could not be removed; recovery remains blocked."));
                }

                ownedTransitionMarker = null;
                if (AfterOwnedMarkerRotationDeleteAsync != null)
                    await AfterOwnedMarkerRotationDeleteAsync().ConfigureAwait(false);

                markerCreate = await _journalStore
                    .CreateTransitionMarkerIfAbsentAsync(marker, cancellationToken)
                    .ConfigureAwait(false);
                if (markerCreate.Status != SwitchTransitionMarkerWriteStatus.Created)
                {
                    MarkJournalRecoveryBlocked();
                    return (null, null, markerCreate.Status == SwitchTransitionMarkerWriteStatus.AlreadyExists
                        ? SwitchJournalWriteResult.AlreadyExists("A replacement marker occupies the path; it was preserved.")
                        : SwitchJournalWriteResult.PersistenceFailure(
                            $"Failed to persist the replacement transition marker: {markerCreate.Message}", markerCreate.Exception));
                }

                ownedTransitionMarker = markerCreate.Marker;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                MarkJournalRecoveryBlocked();
                return (null, ownedTransitionMarker, SwitchJournalWriteResult.PersistenceFailure(
                    "Marker rotation failed; the canonical bridge or the prior marker retains recovery evidence.", ex));
            }
        }
        else if (markerCreate.Status == SwitchTransitionMarkerWriteStatus.Created)
        {
            ownedTransitionMarker = markerCreate.Marker;
        }
        else
        {
            // Marker persistence failed before any destructive step: the predecessor
            // remains durable and owned, and no recovery evidence was lost.
            return (null, ownedTransitionMarker, SwitchJournalWriteResult.PersistenceFailure(
                $"Failed to persist the transition marker: {markerCreate.Message ?? markerCreate.Status.ToString()}",
                markerCreate.Exception));
        }

        // STEP 3-5: replace exactly the owned entry. A NotMatched/Absent outcome or a failed
        // publish leaves the durable marker behind as recovery evidence; ownership of the
        // journal stays where it was.
        SwitchJournalWriteResult replace;
        try
        {
            replace = await _journalStore.ReplaceIfUnchangedAsync(ownedJournalEntry, next, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Return the marker we successfully persisted even when the store throws.
            // The caller must retain that ownership for a covered rollback transition.
            return (null, ownedTransitionMarker, SwitchJournalWriteResult.PersistenceFailure(
                "The journal transition failed while its owned marker remains recovery evidence.", ex));
        }
        if (replace.Status != SwitchJournalWriteStatus.Replaced || replace.Entry == null)
        {
            return (null, ownedTransitionMarker, replace);
        }

        // STEP 6: the successor is proven durable — remove the exact owned marker. If the
        // exact removal fails or the marker was replaced, the successor remains
        // authoritative but recovery stays blocked until the unresolved marker is
        // reconciled.
        GuardedMarkerDeleteResult markerDelete;
        try
        {
            markerDelete = await _journalStore
                .DeleteTransitionMarkerWhileCanonicalGuardedAsync(replace.Entry, ownedTransitionMarker!, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            MarkJournalRecoveryBlocked();
            return (null, ownedTransitionMarker, SwitchJournalWriteResult.CleanupIncomplete(
                replace.Entry,
                $"Guarded transition marker cleanup failed with an exception: {ex.Message}",
                ex));
        }
        if (markerDelete.Status != GuardedMarkerDeleteStatus.Deleted)
        {
            MarkJournalRecoveryBlocked();
            if (markerDelete.Status is GuardedMarkerDeleteStatus.CanonicalAbsent
                or GuardedMarkerDeleteStatus.CanonicalMismatch
                or GuardedMarkerDeleteStatus.CanonicalCorrupt)
            {
                return (null, ownedTransitionMarker, SwitchJournalWriteResult.PersistenceFailure(
                    $"The canonical companion could not be guarded during transition marker cleanup ({markerDelete.Status}): {markerDelete.Message}",
                    markerDelete.Exception));
            }

            if (markerDelete.Status is GuardedMarkerDeleteStatus.UnsupportedPlatform)
            {
                return (null, ownedTransitionMarker, SwitchJournalWriteResult.UnsupportedPlatform(
                    markerDelete.Message ?? "Guarded marker deletion is not supported on this platform."));
            }

            if (markerDelete.Status is GuardedMarkerDeleteStatus.MarkerMismatch)
            {
                return (null, ownedTransitionMarker, SwitchJournalWriteResult.AlreadyExists(
                    $"Transition marker mismatch during guarded cleanup: {markerDelete.Message}"));
            }

            return (null, ownedTransitionMarker, SwitchJournalWriteResult.CleanupIncomplete(
                replace.Entry,
                $"The canonical companion was published, but guarded transition marker cleanup did not complete ({markerDelete.Status}): {markerDelete.Message}",
                markerDelete.Exception));
        }

        return (replace.Entry, null, replace);
    }

    private async Task<SwitchJournalWriteResult?> RestoreOwnedMarkerPredecessorAsync(
        SwitchTransitionMarkerEntry ownedMarker, CancellationToken cancellationToken)
    {
        try
        {
            var canonical = await _journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (canonical.Status == SwitchJournalReadStatus.Absent)
            {
                try
                {
                    var restored = await _journalStore.CreateIfAbsentAsync(ownedMarker.Expected, cancellationToken)
                        .ConfigureAwait(false);
                    if (restored.Status is SwitchJournalWriteStatus.AlreadyExists or SwitchJournalWriteStatus.NotMatched)
                        return SwitchJournalWriteResult.NotMatched("A canonical occupant won predecessor restoration; it and the owned marker were preserved.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A publish may have landed before reporting failure. Only exact
                    // readback of our previously durable predecessor authorizes rotation.
                }

                canonical = await _journalStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (canonical.Status == SwitchJournalReadStatus.Valid && canonical.Entry != null &&
                SwitchJournalStore.EntriesMatch(canonical.Entry, ownedMarker.Expected))
                return null;

            return canonical.Status is SwitchJournalReadStatus.Valid or SwitchJournalReadStatus.Corrupt or SwitchJournalReadStatus.UnsupportedVersion
                ? SwitchJournalWriteResult.NotMatched("The canonical predecessor is not exactly owned; it and the marker were preserved.")
                : SwitchJournalWriteResult.PersistenceFailure("The canonical predecessor could not be proven durable; the owned marker was retained.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return SwitchJournalWriteResult.PersistenceFailure("Predecessor restoration is uncertain; the owned marker was retained.", ex);
        }
    }

    /// <summary>
    /// Classifies the journal occupying the canonical path after a first-create ownership
    /// failure, purely for recovery reporting. The occupying bytes are never deleted,
    /// replaced, or normalized by this transaction.
    /// </summary>
    private async Task ClassifyForeignJournalRecoveryStateAsync()
    {
        SwitchJournalReadResult read;
        try
        {
            read = await _journalStore.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            read = SwitchJournalReadResult.IoError(ex);
        }

        lock (_statusLock)
        {
            if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
            {
                _journalRecoveryState = read.Status switch
                {
                    SwitchJournalReadStatus.Valid => JournalRecoveryStates.ActionRequired,
                    SwitchJournalReadStatus.Corrupt or SwitchJournalReadStatus.UnsupportedVersion => JournalRecoveryStates.NotResolvable,
                    _ => JournalRecoveryStates.Unknown,
                };
            }
        }
    }

    /// <summary>
    /// Classifies the marker occupying the marker path after a marker create-if-absent
    /// ownership failure, purely for recovery reporting. The occupying bytes are never
    /// deleted, replaced, or normalized by this transaction: a valid or unreadable marker
    /// is unresolved recovery evidence that blocks admission.
    /// </summary>
    private async Task ClassifyForeignMarkerRecoveryStateAsync()
    {
        SwitchTransitionMarkerReadResult read;
        try
        {
            read = await _journalStore.ReadTransitionMarkerAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            read = SwitchTransitionMarkerReadResult.IoError(ex);
        }

        lock (_statusLock)
        {
            if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
            {
                _journalRecoveryState = read.Status switch
                {
                    SwitchTransitionMarkerReadStatus.Valid => JournalRecoveryStates.ActionRequired,
                    SwitchTransitionMarkerReadStatus.Corrupt or SwitchTransitionMarkerReadStatus.UnsupportedVersion
                        => JournalRecoveryStates.NotResolvable,
                    _ => JournalRecoveryStates.Unknown,
                };
            }
        }
    }

    /// <summary>
    /// Deletes the transaction's owned journal entry and its owned transition marker
    /// conditionally, then proves the canonical journal path AND the marker path clear.
    /// Ownership starts null and advances only after a journal or marker write successfully
    /// commits, so a transaction that never wrote evidence performs no cleanup at all and
    /// unrelated evidence is never touched. Deleting the owned entry is not by itself proof
    /// of path clearance (a replacement can be written during the comparison/disposition
    /// window), and neither is deleting the owned marker: a surviving replacement, an
    /// unresolved marker, or corrupt bytes at either path publish blocking recovery
    /// immediately; an uncertain readback defers to the transaction-exit proof, which
    /// re-reads the same disk state and applies its established classification (valid
    /// journal or marker → ACTION_REQUIRED, corrupt/unsupported → NOT_RESOLVABLE, I/O error
    /// → UNKNOWN). Terminal RESTART_REQUIRED is never overwritten. Never throws.
    /// </summary>
    private async Task<bool> CleanupOwnedJournalEntryAsync(
        SwitchJournalEntry ownedJournalEntry,
        SwitchTransitionMarkerEntry? ownedTransitionMarker)
    {
        try
        {
            var deleteResult = await _journalStore
                .DeleteIfUnchangedAsync(ownedJournalEntry, CancellationToken.None)
                .ConfigureAwait(false);
            if (deleteResult.Status != SwitchJournalDeleteStatus.Deleted)
            {
                if (deleteResult.Status == SwitchJournalDeleteStatus.Absent)
                {
                    // The owned entry vanished before this cleanup and the remover cannot be
                    // established; the transaction-exit proof would observe a clean path, so
                    // the unexpected disappearance must fail closed here.
                    MarkJournalRecoveryBlocked();
                }

                return false;
            }

            if (ownedTransitionMarker != null)
            {
                var markerDelete = await _journalStore
                    .DeleteTransitionMarkerIfUnchangedAsync(ownedTransitionMarker, CancellationToken.None)
                    .ConfigureAwait(false);
                if (markerDelete.Status != SwitchTransitionMarkerDeleteStatus.Deleted &&
                    markerDelete.Status != SwitchTransitionMarkerDeleteStatus.Absent)
                {
                    // The exact owned marker could not be removed (replaced, corrupt, or
                    // I/O uncertainty): the surviving marker keeps recovery blocked.
                    MarkJournalRecoveryBlocked();
                    return false;
                }
            }

            var postCleanup = await _journalStore.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (postCleanup.Status != SwitchJournalReadStatus.Absent)
            {
                if (postCleanup.Status == SwitchJournalReadStatus.Valid)
                {
                    MarkJournalRecoveryBlocked();
                }

                return false;
            }

            return await IsTransitionMarkerAbsentAfterCleanupAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Proves the marker path clear after cleanup and classifies surviving evidence.
    /// Returns true only when the marker is genuinely absent.
    /// </summary>
    private async Task<bool> IsTransitionMarkerAbsentAfterCleanupAsync()
    {
        try
        {
            var markerRead = await _journalStore
                .ReadTransitionMarkerAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (markerRead.Status == SwitchTransitionMarkerReadStatus.Absent)
            {
                return true;
            }

            if (markerRead.Status is SwitchTransitionMarkerReadStatus.Corrupt
                or SwitchTransitionMarkerReadStatus.UnsupportedVersion)
            {
                lock (_statusLock)
                {
                    if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
                    {
                        _journalRecoveryState = JournalRecoveryStates.NotResolvable;
                    }
                }
            }
            else if (markerRead.Status == SwitchTransitionMarkerReadStatus.Valid)
            {
                MarkJournalRecoveryBlocked();
            }
            else if (markerRead.Status == SwitchTransitionMarkerReadStatus.IoError)
            {
                lock (_statusLock)
                {
                    if (_journalRecoveryState != JournalRecoveryStates.RestartRequired &&
                        _journalRecoveryState == JournalRecoveryStates.None)
                    {
                        _journalRecoveryState = JournalRecoveryStates.Unknown;
                    }
                }
            }

            return false;
        }
        catch (Exception)
        {
            lock (_statusLock)
            {
                if (_journalRecoveryState == JournalRecoveryStates.None)
                {
                    _journalRecoveryState = JournalRecoveryStates.Unknown;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Deleting the proved entry (and any cleanup marker) does not by itself prove the
    /// recovery evidence paths clear: a replacement can be written at either path during
    /// the comparison/disposition window. Resolution may declare success only when the
    /// canonical journal AND the transition marker are genuinely absent afterwards.
    /// </summary>
    private async Task<JournalResolutionResult?> GetRecoveryEvidenceClearanceFailureAsync(bool unexpectedAbsence = false)
    {
        SwitchJournalReadResult canonical;
        SwitchTransitionMarkerReadResult marker;
        using var deadline = new CancellationTokenSource(_transactionTimeout);
        try
        {
            canonical = await _journalStore.ReadAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            canonical = SwitchJournalReadResult.IoError(ex);
        }
        try
        {
            marker = await _journalStore.ReadTransitionMarkerAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            marker = SwitchTransitionMarkerReadResult.IoError(ex);
        }

        if (marker.Status is SwitchTransitionMarkerReadStatus.Corrupt or SwitchTransitionMarkerReadStatus.UnsupportedVersion)
            return new JournalResolutionResult(JournalResolutionStatus.NotResolvable,
                "The transition marker cannot be classified as supported recovery evidence.", ReasonCode: "CORRUPT_TRANSITION_MARKER");
        if (canonical.Status is SwitchJournalReadStatus.Corrupt or SwitchJournalReadStatus.UnsupportedVersion)
            return new JournalResolutionResult(JournalResolutionStatus.NotResolvable,
                "The canonical journal cannot be classified as supported recovery evidence.", ReasonCode: "CORRUPT_JOURNAL");
        if (canonical.Status == SwitchJournalReadStatus.IoError || marker.Status == SwitchTransitionMarkerReadStatus.IoError)
            return new JournalResolutionResult(JournalResolutionStatus.PersistenceFailure,
                "Joint recovery-evidence clearance could not be read reliably.", ReasonCode: "IO_ERROR");
        if (canonical.Status == SwitchJournalReadStatus.Absent && marker.Status == SwitchTransitionMarkerReadStatus.Absent &&
            !unexpectedAbsence)
            return null;

        // Unexpected disappearance has no attributable cleanup owner, even when both
        // names are now absent. A subsequent explicit resolution may prove a clean pair.
        return new JournalResolutionResult(JournalResolutionStatus.ProofFailed,
            "Recovery evidence changed during resolution; both paths must be reconciled before switching resumes.",
            ReasonCode: "CONCURRENT_MUTATION");
    }

    private void MarkJournalRecoveryBlocked()
    {
        lock (_statusLock)
        {
            if (_journalRecoveryState != JournalRecoveryStates.RestartRequired)
            {
                _journalRecoveryState = JournalRecoveryStates.ActionRequired;
            }
        }
    }

    private async Task<List<AccountModelQuotaObservation>> VerifyTargetQuotaAsync(
        AccountMetadata target,
        AG2ProcessGeneration? generation,
        string requiredWorkloadModelKey,
        double? minimumCandidateQuotaPercent,
        List<string> stages,
        CancellationToken cancellationToken)
    {
        string? canonicalKey = CandidateSelector.CanonicalizeModelKey(requiredWorkloadModelKey);
        if (string.IsNullOrWhiteSpace(canonicalKey))
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.InvalidWorkloadModelKey);
        }

        if (generation == null || !await _processLifecycle.IsGenerationCurrentAsync(generation, cancellationToken).ConfigureAwait(false))
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.ProcessGenerationChanged);
        }

        AccountQuotaObservation observation;
        try
        {
            observation = await _adapter.GetAccountQuotaObservationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.TelemetryUnavailable, innerException: ex);
        }

        if (generation == null || !await _processLifecycle.IsGenerationCurrentAsync(generation, cancellationToken).ConfigureAwait(false))
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.ProcessGenerationChanged);
        }

        if (observation == null || observation.Account == null ||
            string.IsNullOrWhiteSpace(observation.Account.Email) ||
            !string.Equals(observation.Account.Email.Trim(), target.Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.TargetIdentityMismatch);
        }

        if (observation.Quota == null || observation.Quota.Models == null || observation.Quota.Models.Count == 0)
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.TelemetryUnavailable);
        }

        var matchingModels = observation.Quota.Models
            .Where(m => string.Equals(CandidateSelector.GetModelKey(m), canonicalKey, StringComparison.Ordinal))
            .ToList();

        if (matchingModels.Count == 0)
        {
            throw new SwitchVerificationException(SwitchVerificationFailureReasons.RequestedModelAbsent);
        }

        double minRequiredFraction = (minimumCandidateQuotaPercent ?? 0.0) / 100.0;
        foreach (var m in matchingModels)
        {
            if (m.IsExhausted)
            {
                throw new SwitchVerificationException(SwitchVerificationFailureReasons.RequestedModelExhausted);
            }

            if (!m.RemainingFraction.HasValue)
            {
                throw new SwitchVerificationException(SwitchVerificationFailureReasons.QuotaUnknown);
            }

            if (!double.IsFinite(m.RemainingFraction.Value) || m.RemainingFraction.Value < 0.0 || m.RemainingFraction.Value > 1.0)
            {
                throw new SwitchVerificationException(SwitchVerificationFailureReasons.QuotaInvalid);
            }

            if (m.RemainingFraction.Value < minRequiredFraction)
            {
                throw new SwitchVerificationException(SwitchVerificationFailureReasons.BelowMinimumThreshold);
            }
        }

        stages.Add("TARGET_QUOTA_VERIFIED");

        return QuotaObservationEvidence.Capture(target.Id, observation.Quota.Models,
            _timeProvider.GetUtcNow(), "LiveTargetVerification").ToList();
    }

    private sealed class SwitchRejectedException : Exception
    {
        public string Code { get; }
        public SwitchRejectedException(string code, string message) : base(message) => Code = code;
    }

    /// <summary>
    /// The transaction lost ownership of its journal (a foreign/replacement entry occupies
    /// the canonical path, or the owned entry vanished). Thrown only from awaited
    /// transaction states; the general failure path routes into verified compensation while
    /// all further journal writes are suppressed and the foreign journal remains canonical.
    /// </summary>
    private sealed class SwitchJournalOwnershipLostException : Exception
    {
        public SwitchJournalOwnershipLostException(string message) : base(message) { }
    }
}

public static class SwitchVerificationFailureReasons
{
    public const string InvalidWorkloadModelKey = "InvalidWorkloadModelKey";
    public const string ProcessGenerationChanged = "ProcessGenerationChanged";
    public const string TelemetryUnavailable = "TelemetryUnavailable";
    public const string TargetIdentityMismatch = "TargetIdentityMismatch";
    public const string RequestedModelAbsent = "RequestedModelAbsent";
    public const string RequestedModelExhausted = "RequestedModelExhausted";
    public const string QuotaUnknown = "QuotaUnknown";
    public const string QuotaInvalid = "QuotaInvalid";
    public const string BelowMinimumThreshold = "BelowMinimumThreshold";
}

public sealed class SwitchVerificationException : Exception
{
    public string Reason { get; }

    public SwitchVerificationException(string reason, string? message = null, Exception? innerException = null)
        : base(message ?? $"Target quota verification failed: {reason}.", innerException)
    {
        Reason = reason;
    }

    public SwitchVerificationException(string reason, Exception? innerException)
        : base($"Target quota verification failed: {reason}.", innerException)
    {
        Reason = reason;
    }
}
