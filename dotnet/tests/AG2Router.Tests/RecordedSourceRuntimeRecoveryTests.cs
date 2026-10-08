using System.IO;
using System.Text;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Switching;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Decisive coverage for the PROMPT #020 findings: A1 (a RECORDED journal proves the target
/// was untouched, never that the source runtime is still running — startup reconciliation
/// and resolution must establish source coherence before cleanup), A2 (the durable evidence
/// revision boundary that lets router pool-status publication observe external durable
/// mutations), and A3 (the dashboard serves no fabricated pool assessment). All scenarios are
/// synthetic; each recovery phase reconstructs fresh coordinator fixtures.
/// </summary>
public sealed partial class TargetQuotaVerificationSwitchTests
{
    // =========================================================================
    // A1: RECORDED source-runtime recovery
    // =========================================================================

    [Fact]
    public async Task Recorded_01_CrashBeforeSourceStop_CoherentRunningSource_CleansWithoutRestart()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        // Source is running and bound to the source identity (harness defaults).
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        });

        // Fresh coordinator fixture models the restarted process.
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Null(result.RetainedEntry);
        Assert.False(File.Exists(_journal.JournalFilePath));

        // No process transition was needed or performed.
        Assert.Equal(0, _process.LaunchCount);
        Assert.Equal(0, _process.RestoreCount);

        // Source account, credential, and target evidence are untouched.
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal("source-secret-payload", Encoding.UTF8.GetString(_credentials.Current!.Blob));
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.Equal(0.8, targetObs!.RemainingFraction);
        Assert.Equal("ActiveGetUserStatus", targetObs.Source);
    }

    [Fact]
    public async Task Recorded_02_SourceStoppedAtStartup_RetainsUntilCoherent_ThenResolves()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        });

        // The source runtime is stopped when AG2 Router restarts: telemetry is offline.
        _adapter.Status = new Ag2StatusDto(false, "OFFLINE",
            new ActivityStatusDto("IDLE", 0, 0, clock.Now.ToString("O")), "synthetic");

        var startupCoordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var reconciliation = await startupCoordinator.ReconcileStartupJournalAsync();

        // The journal must NOT be deleted as clean while the runtime is unproven.
        Assert.Equal(StartupJournalReconciliationStatus.Degraded, reconciliation.Status);
        Assert.NotNull(reconciliation.RetainedEntry);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.ActionRequired, startupCoordinator.GetStatus().JournalRecoveryState);
        // Credentials are provably intact; the vault is not quarantined for a RECORDED journal.
        Assert.False(_vault.IsQuarantined);

        // The source runtime returns (external Antigravity supervision or operator start).
        _adapter.Status = new Ag2StatusDto(true, "HEALTHY",
            new ActivityStatusDto("IDLE", 0, 0, clock.Now.ToString("O")), "synthetic");
        _adapter.Identity = new AccountIdentityDto(_source!.Email);

        var resolutionCoordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var resolved = await resolutionCoordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, resolved.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));

        // Source and target state remain untouched by the whole recovery.
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal("source-secret-payload", Encoding.UTF8.GetString(_credentials.Current!.Blob));
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.Equal(0.8, targetObs!.RemainingFraction);
    }

    [Fact]
    public async Task Recorded_03_SourceRuntimeNeverReturns_StaysBlockedAtStartupAndResolution()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        });

        _adapter.Status = new Ag2StatusDto(false, "OFFLINE",
            new ActivityStatusDto("IDLE", 0, 0, clock.Now.ToString("O")), "synthetic");

        var startupCoordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var reconciliation = await startupCoordinator.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Degraded, reconciliation.Status);
        Assert.True(File.Exists(_journal.JournalFilePath));

        var resolutionCoordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var resolved = await resolutionCoordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, resolved.Status);
        Assert.Equal("SOURCE_RUNTIME_UNPROVEN", resolved.ReasonCode);
        Assert.True(File.Exists(_journal.JournalFilePath));

        // Target evidence is never manipulated for NOT_ATTEMPTED journals.
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.Equal(0.8, targetObs!.RemainingFraction);
        Assert.Equal("ActiveGetUserStatus", targetObs.Source);
    }

    [Fact]
    public async Task Recorded_04_LiveIdentityMismatch_BlocksRecoveryWithoutTargetManipulation()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        });

        // The running server is bound to a different identity than the journal's source.
        _adapter.Identity = new AccountIdentityDto("someone-else@example.com");

        var startupCoordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var reconciliation = await startupCoordinator.ReconcileStartupJournalAsync();
        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, reconciliation.Status);
        Assert.True(File.Exists(_journal.JournalFilePath));

        var resolutionCoordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var resolved = await resolutionCoordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ProofFailed, resolved.Status);
        Assert.True(File.Exists(_journal.JournalFilePath));

        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.Equal(0.8, targetObs!.RemainingFraction);
    }

    [Fact]
    public async Task Recorded_05_LegacyRecordedJournalWithoutFields_FollowsCoherenceGatedCleanup()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await File.WriteAllTextAsync(_journal.JournalFilePath, LegacyJournalJson(
            Guid.NewGuid().ToString("D"), "RECORDED", _source!.Id, _target!.Id));

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // Legacy provenance is UNKNOWN, but RECORDED recovery never manipulates the target;
        // a coherent running source allows the same safe cleanup.
        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.Equal(0.8, targetObs!.RemainingFraction);
    }

    // =========================================================================
    // A2: durable evidence revision boundary
    // =========================================================================

    [Fact]
    public async Task EvidenceRevision_01_MutationsChangeRevision_NonMutatingReadsDoNot()
    {
        var path = Path.Combine(_tempDir, "revision-store.json");
        var store = new DurableQuotaObservationStore(path);
        var accountA = "rev-account-a";
        var accountB = "rev-account-b";

        var emptyRevision = await store.GetObservationRevisionAsync();
        Assert.Equal(emptyRevision, await store.GetObservationRevisionAsync());
        await store.GetAllObservationsAsync();
        await store.GetObservationsForAccountAsync(accountA);
        Assert.Equal(emptyRevision, await store.GetObservationRevisionAsync());

        await store.RecordObservationsAsync(accountA,
            [new(accountA, EvidenceModel, 0.5, null, DateTimeOffset.UtcNow, "synthetic")]);
        var afterFirstWrite = await store.GetObservationRevisionAsync();
        Assert.NotEqual(emptyRevision, afterFirstWrite);

        await store.RecordObservationsAsync(accountB,
            [new(accountB, EvidenceModel, 0.6, null, DateTimeOffset.UtcNow, "synthetic")]);
        var afterSecondWrite = await store.GetObservationRevisionAsync();
        Assert.NotEqual(afterFirstWrite, afterSecondWrite);

        await store.InvalidateObservationsForAccountAsync(accountA, DateTimeOffset.UtcNow);
        var afterInvalidation = await store.GetObservationRevisionAsync();
        Assert.NotEqual(afterSecondWrite, afterInvalidation);
    }

    [Fact]
    public async Task EvidenceRevision_02_SameTimestampMutations_RemainStrictlyMonotonic()
    {
        var path = Path.Combine(_tempDir, "revision-monotonic.json");
        var store = new DurableQuotaObservationStore(path);
        var fixedTime = new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);

        await store.RecordObservationsAsync("rev-a",
            [new("rev-a", EvidenceModel, 0.5, null, fixedTime, "synthetic")]);
        var first = await store.GetObservationRevisionAsync();

        // A second real mutation supplying the SAME caller timestamp must still move the
        // revision, so no concurrent reader can mistake new evidence for old evidence.
        await store.InvalidateObservationsForAccountAsync("rev-a", fixedTime);
        var second = await store.GetObservationRevisionAsync();
        Assert.True(second > first);

        await store.RecordObservationsAsync("rev-b",
            [new("rev-b", EvidenceModel, 0.5, null, fixedTime, "synthetic")]);
        var third = await store.GetObservationRevisionAsync();
        Assert.True(third > second);
    }

    [Fact]
    public async Task EvidenceRevision_03_SnapshotPairsRowsWithRevision_AcrossInstances()
    {
        var path = Path.Combine(_tempDir, "revision-instances.json");
        var writer = new DurableQuotaObservationStore(path);
        await writer.RecordObservationsAsync("rev-inst",
            [new("rev-inst", EvidenceModel, 0.7, null, DateTimeOffset.UtcNow, "synthetic")]);

        // A second store instance over the same durable document (post-restart shape).
        var reader = new DurableQuotaObservationStore(path);
        var snapshot = await reader.GetObservationSnapshotAsync();
        Assert.Equal(await reader.GetObservationRevisionAsync(), snapshot.Revision);
        Assert.Equal(await writer.GetObservationRevisionAsync(), snapshot.Revision);
        Assert.Single(snapshot.Observations);

        // A mutation through the other instance is immediately visible to this reader.
        await writer.InvalidateObservationsForAccountAsync("rev-inst", DateTimeOffset.UtcNow);
        var afterMutation = await reader.GetObservationSnapshotAsync();
        Assert.NotEqual(snapshot.Revision, afterMutation.Revision);
        Assert.Null(afterMutation.Observations.Single().RemainingFraction);
    }

    [Fact]
    public async Task EvidenceRevision_04_RecoveryResolutionInvalidation_InvalidatesStatusCacheBeforeTerminalPublication()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = NonSwitchingEvidenceRouter(store, clock);

        // Prime an evaluated pool status against the pre-invalidation evidence revision.
        await router.EvaluateCycleAsync();
        var primed = router.GetStatus().PoolStatus;
        Assert.NotNull(primed);
        Assert.True(primed!.HasUsableCandidate);
        var revisionBefore = await store.GetObservationRevisionAsync();

        // Recovery resolution invalidates the target durably (MAY_HAVE_BEEN_ATTEMPTED journal),
        // with no switch-terminal result ever published to the router.
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        });
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolution.Status);

        // Check immediately: even a store read could mask a missing post-commit update
        // of the synchronous revision view used by tray and router-status consumers.
        Assert.Null(router.GetStatus().PoolStatus);

        var revisionAfter = await store.GetObservationRevisionAsync();
        Assert.NotEqual(revisionBefore, revisionAfter);

        // The next status read sees UNKNOWN rows and no pool status, and the stale evaluated
        // cache is gone for every consumer.
        var status = await router.GetCandidateEvidenceStatusAsync();
        var targetRow = Assert.Single(status.Candidates, c => c.AccountId == _target!.Id);
        Assert.Equal("UNKNOWN", targetRow.State);
        Assert.Null(status.PoolStatus);
        Assert.Null(router.GetStatus().PoolStatus);
    }

    [Fact]
    public async Task EvidenceRevision_05_ConcurrentSnapshotReadsAndInvalidations_DeadlockFree()
    {
        var path = Path.Combine(_tempDir, "revision-concurrency.json");
        var store = new DurableQuotaObservationStore(path);
        await store.RecordObservationsAsync("rev-conc",
            [new("rev-conc", EvidenceModel, 0.5, null, DateTimeOffset.UtcNow, "synthetic")]);

        var work = new List<Task>();
        for (int round = 0; round < 25; round++)
        {
            var account = $"rev-conc-{round % 4}";
            work.Add(store.GetObservationSnapshotAsync());
            work.Add(store.InvalidateObservationsForAccountAsync(account, DateTimeOffset.UtcNow));
            work.Add(store.GetObservationRevisionAsync());
        }

        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(30));
    }

    // =========================================================================
    // A3: no fabricated dashboard pool assessment
    // =========================================================================

    [Fact]
    public async Task DashboardPool_UsableRowsWithoutEvaluation_ServeNoFabricatedStatus()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = NonSwitchingEvidenceRouter(store, clock);

        var status = await router.GetCandidateEvidenceStatusAsync();

        // The candidate row is healthy, so a fabricated "no usable candidate" pool status
        // would be a contradiction. Before an authoritative evaluation publishes one, the
        // pool status is simply unavailable.
        var targetRow = Assert.Single(status.Candidates, c => c.AccountId == _target!.Id);
        Assert.Equal("USABLE", targetRow.State);
        Assert.Equal(0.8, targetRow.RemainingFraction);
        Assert.Null(status.PoolStatus);
        Assert.Null(router.GetStatus().PoolStatus);
    }
}
