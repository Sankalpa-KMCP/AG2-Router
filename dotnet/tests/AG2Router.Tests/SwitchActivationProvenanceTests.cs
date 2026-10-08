using System.IO;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Switching;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Decisive coverage for the durable target-activation provenance (PROMPT #018 R1) and the
/// pool-status publication generation (PROMPT #018 R2): journal compatibility, monotonic
/// provenance transitions through real coordinator flows, provenance-gated recovery
/// resolution, crash-window retention analogues, and stale-pool-status publication races.
/// All scenarios are synthetic; no live Antigravity, credential, or user state is touched.
/// </summary>
public sealed partial class TargetQuotaVerificationSwitchTests
{
    // =========================================================================
    // Group A: Journal serialization / backward compatibility
    // =========================================================================

    [Theory]
    [InlineData(SwitchTargetActivationProvenance.NOT_ATTEMPTED)]
    [InlineData(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED)]
    [InlineData(SwitchTargetActivationProvenance.UNKNOWN)]
    public async Task Provenance_Store_01_RoundTrip_PreservesCanonicalProvenance(
        SwitchTargetActivationProvenance provenance)
    {
        string path = Path.Combine(_tempDir, $"journal_provenance_{provenance}.json");
        var store = new SwitchJournalStore(path);
        var entry = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            SourceAccountId = "source",
            TargetAccountId = "target",
            TargetActivationProvenance = provenance
        };

        await store.WriteEntryAsync(entry);
        string rawJson = await File.ReadAllTextAsync(path);
        Assert.Contains($"\"targetActivationProvenance\": \"{provenance}\"", rawJson, StringComparison.Ordinal);

        var read = await store.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read.Status);
        Assert.NotNull(read.Entry);
        Assert.Equal(provenance, read.Entry!.TargetActivationProvenance);
    }

    [Fact]
    public async Task Provenance_Store_02_LegacyJournalWithoutField_ReadsAsUnknown()
    {
        string path = Path.Combine(_tempDir, "journal_legacy.json");
        var store = new SwitchJournalStore(path);
        await File.WriteAllTextAsync(path, LegacyJournalJson(
            Guid.NewGuid().ToString("D"), "ROLLING_BACK", "source", "target"));

        var read = await store.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read.Status);
        Assert.NotNull(read.Entry);
        // Missing provenance must read as UNKNOWN, never as NOT_ATTEMPTED.
        Assert.Equal(SwitchTargetActivationProvenance.UNKNOWN, read.Entry!.TargetActivationProvenance);
    }

    [Fact]
    public async Task Provenance_Store_03_NullProvenance_ReadsAsUnknown()
    {
        string legacy = LegacyJournalJson(
            Guid.NewGuid().ToString("D"), "ROLLING_BACK", "source", "target");
        string withNull = legacy.Replace(
            "\"quarantineReasonCode\": null",
            "\"quarantineReasonCode\": null,\n  \"targetActivationProvenance\": null",
            StringComparison.Ordinal);

        var parsed = SwitchJournalStore.ParseEntryFromText(withNull);
        Assert.Equal(SwitchJournalReadStatus.Valid, parsed.Status);
        Assert.NotNull(parsed.Entry);
        Assert.Equal(SwitchTargetActivationProvenance.UNKNOWN, parsed.Entry!.TargetActivationProvenance);
    }

    [Theory]
    [InlineData("\"ATTEMPTED\"")]
    [InlineData("\"not_attempted\"")]
    [InlineData("1")]
    [InlineData("\"MAY_HAVE_BEEN_ATTEMPTED \"")]
    public async Task Provenance_Store_04_InvalidProvenance_ReturnsCorrupt(string invalidValue)
    {
        string legacy = LegacyJournalJson(
            Guid.NewGuid().ToString("D"), "ROLLING_BACK", "source", "target");
        string invalid = legacy.Replace(
            "\"quarantineReasonCode\": null",
            $"\"quarantineReasonCode\": null,\n  \"targetActivationProvenance\": {invalidValue}",
            StringComparison.Ordinal);

        var parsed = SwitchJournalStore.ParseEntryFromText(invalid);
        Assert.Equal(SwitchJournalReadStatus.Corrupt, parsed.Status);
    }

    [Fact]
    public async Task Provenance_Store_05_ConditionalDelete_RespectsProvenanceDifference()
    {
        string path = Path.Combine(_tempDir, "journal_conditional.json");
        var store = new SwitchJournalStore(path);
        var written = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            SourceAccountId = "source",
            TargetAccountId = "target",
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        };
        await store.WriteEntryAsync(written);

        // Same provenance: conditional delete matches and deletes.
        var read = await store.ReadAsync();
        Assert.Equal(SwitchJournalDeleteStatus.Deleted,
            (await store.DeleteIfUnchangedAsync(read.Entry!)).Status);

        // Differing provenance on disk: conditional delete must reject as not matched.
        await store.WriteEntryAsync(written with
        {
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        });
        var readAgain = await store.ReadAsync();
        var mismatch = await store.DeleteIfUnchangedAsync(readAgain.Entry! with
        {
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        });
        Assert.Equal(SwitchJournalDeleteStatus.NotMatched, mismatch.Status);
        Assert.True(File.Exists(path));
    }

    private static string LegacyJournalJson(string transactionId, string state, string sourceId, string targetId) =>
        $$"""
        {
          "magic": "AG2SWITCHJRNL",
          "schemaVersion": 1,
          "transactionId": "{{transactionId}}",
          "state": "{{state}}",
          "updatedAt": "2026-01-01T00:00:00.0000000Z",
          "sourceAccountId": "{{sourceId}}",
          "targetAccountId": "{{targetId}}",
          "quarantineReasonCode": null
        }
        """;

    // =========================================================================
    // Group B/C/D: Provenance transitions through real switch flows
    // =========================================================================

    [Fact]
    public async Task Provenance_Transitions_01_SourceOnlyRollingBack_PreservesNotAttempted()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        // Source process stop fails after the stop was issued; the target credential
        // phase is never reached. Retain the journal with a delete fault.
        _process.AfterStopIssued = () => throw new IOException("Synthetic source stop failure");
        _journal.DeleteError = new IOException("Synthetic journal delete failure");

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.DoesNotContain("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);

        var read = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read.Status);
        Assert.Equal(SwitchJournalState.ROLLING_BACK, read.Entry!.State);
        Assert.Equal(SwitchTargetActivationProvenance.NOT_ATTEMPTED, read.Entry.TargetActivationProvenance);

        // Provenance never advanced past NOT_ATTEMPTED in any persisted write.
        Assert.All(_journal.RecordedEntries, e =>
            Assert.Equal(SwitchTargetActivationProvenance.NOT_ATTEMPTED, e.TargetActivationProvenance));

        // Target evidence is untouched.
        var targetObs = await store.GetObservationAsync(_target.Id, EvidenceModel);
        Assert.Equal(0.8, targetObs!.RemainingFraction);
    }

    [Fact]
    public async Task Provenance_Transitions_02_TargetAttemptedRollingBack_PreservesMayHaveBeenAttempted()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        // Target identity never appears (activation attempted, verification times out).
        _process.OnReplacement = () => { _adapter.Identity = null; };
        _journal.DeleteError = new IOException("Synthetic journal delete failure");

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Contains("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);

        var read = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read.Status);
        Assert.Equal(SwitchJournalState.ROLLING_BACK, read.Entry!.State);
        Assert.Equal(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, read.Entry.TargetActivationProvenance);
    }

    [Fact]
    public async Task Provenance_Transitions_03_SuccessfulSwitch_MonotonicProvenanceSequence()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            quotaObservationStore: store,
            timeProvider: clock);

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.True(result.Success);

        Assert.Equal(3, _journal.RecordedEntries.Count);
        Assert.Equal(SwitchJournalState.RECORDED, _journal.RecordedEntries[0].State);
        Assert.Equal(SwitchTargetActivationProvenance.NOT_ATTEMPTED, _journal.RecordedEntries[0].TargetActivationProvenance);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, _journal.RecordedEntries[1].State);
        Assert.Equal(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, _journal.RecordedEntries[1].TargetActivationProvenance);
        Assert.Equal(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _journal.RecordedEntries[2].State);
        Assert.Equal(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, _journal.RecordedEntries[2].TargetActivationProvenance);
    }

    [Fact]
    public async Task Provenance_Transitions_04_MayPersistedBeforeCredentialWriterInvocation()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            quotaObservationStore: store,
            timeProvider: clock);

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.True(result.Success);

        // The CREDENTIAL_APPLYING journal write that persists MAY_HAVE_BEEN_ATTEMPTED must
        // land strictly before the credential writer can be invoked, so a throwing or
        // partially-applied write still leaves durable proof of the crossed boundary.
        int applyingIdx = _globalTimeline.IndexOf("JOURNAL_WRITE:CREDENTIAL_APPLYING");
        int writerIdx = _globalTimeline.FindIndex(t => t.StartsWith("WINCRED_WRITE:", StringComparison.Ordinal));
        Assert.True(applyingIdx >= 0);
        Assert.True(writerIdx > applyingIdx);
    }

    [Fact]
    public async Task Provenance_Transitions_05_CredentialWriterThrows_RetainedJournalStaysMayHaveBeenAttempted()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var faultingWriter = new FaultingCredentialWriter();
        var coordinator = new NativeAccountSwitchCoordinator(
            _accounts,
            _vault,
            _credentials,
            faultingWriter,
            _adapter,
            _process,
            _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: _observationStore,
            timeProvider: clock);

        // The forward credential write throws; rollback restores the source conditionally
        // (credential never changed, so no writer call is needed) and the journal delete
        // fault retains the ROLLING_BACK record.
        _journal.DeleteError = new IOException("Synthetic journal delete failure");

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);

        var read = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read.Status);
        Assert.Equal(SwitchJournalState.ROLLING_BACK, read.Entry!.State);
        Assert.Equal(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, read.Entry.TargetActivationProvenance);
    }

    [Fact]
    public async Task Provenance_Transitions_06_SourceOnlyQuarantined_PreservesNotAttempted_AndTargetEvidence()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        // Source-only failure followed by rollback failure: journal QUARANTINED.
        _process.AfterStopIssued = () => throw new IOException("Synthetic source stop failure");
        _process.RestoreError = new IOException("Synthetic source restore failure");

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);

        var read = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read.Status);
        Assert.Equal(SwitchJournalState.QUARANTINED, read.Entry!.State);
        Assert.Equal(SwitchTargetActivationProvenance.NOT_ATTEMPTED, read.Entry.TargetActivationProvenance);

        // Target evidence was never invalidated.
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.Equal(0.8, targetObs!.RemainingFraction);
        Assert.Equal("ActiveGetUserStatus", targetObs.Source);
    }

    [Fact]
    public async Task Provenance_Transitions_07_TargetAttemptedQuarantined_PreservesMayHaveBeenAttempted()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };
        _process.RestoreError = new IOException("Synthetic source restore failure");

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);

        var read = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, read.Status);
        Assert.Equal(SwitchJournalState.QUARANTINED, read.Entry!.State);
        Assert.Equal(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, read.Entry.TargetActivationProvenance);
    }

    private sealed class FaultingCredentialWriter : IWinCredWriter
    {
        public Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(new IOException("Synthetic credential writer fault"));
    }

    // =========================================================================
    // Group E/F: Provenance-gated recovery resolution and crash analogues
    // =========================================================================

    [Theory]
    [InlineData(SwitchTargetActivationProvenance.NOT_ATTEMPTED, false)]
    [InlineData(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, true)]
    [InlineData(SwitchTargetActivationProvenance.UNKNOWN, true)]
    public async Task Provenance_Recovery_01_RollingBackMatrix_GatesTargetInvalidationOnProvenance(
        SwitchTargetActivationProvenance provenance, bool expectInvalidation)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = provenance
        });

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));

        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        if (expectInvalidation)
        {
            Assert.Null(targetObs!.RemainingFraction);
            Assert.Equal("LiveTargetVerificationRejected", targetObs.Source);
        }
        else
        {
            Assert.Equal(0.8, targetObs!.RemainingFraction);
            Assert.Equal("ActiveGetUserStatus", targetObs.Source);
        }
    }

    [Fact]
    public async Task Provenance_Recovery_02_LegacyQuarantinedUnknown_ReceivesConservativeInvalidation()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await File.WriteAllTextAsync(_journal.JournalFilePath, LegacyJournalJson(
            Guid.NewGuid().ToString("D"), "QUARANTINED", _source!.Id, _target!.Id));

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));

        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Null(targetObs!.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", targetObs.Source);
    }

    [Fact]
    public async Task Provenance_Recovery_03_CredentialApplyingMay_RequiresInvalidationBeforeClearance()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.CREDENTIAL_APPLYING,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        });

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        // Crash-window analogue B/C/D: the credential boundary may have been crossed, so the
        // journal resolves only after durable target invalidation.
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Null(targetObs!.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", targetObs.Source);
    }

    [Fact]
    public async Task Provenance_Recovery_04_PriorInvalidationSuccess_RetryIsIdempotentAndResolves()
    {
        var (store, clock) = await SeedDurableRoutingAsync();

        // Crash-window analogue E/G: invalidation already succeeded before the crash, but the
        // journal was retained. Recovery retry must remain safe, not resurrect old evidence,
        // and resolve the journal.
        await store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);
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
        var result = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, result.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));

        var reloaded = new DurableQuotaObservationStore(store.GetFilePath());
        var targetObs = await reloaded.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Null(targetObs!.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", targetObs.Source);
    }

    [Fact]
    public async Task Provenance_Recovery_05_InvalidationFailureRetainsJournal_HealthyRetryResolves()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        });

        // First attempt: invalidation fails closed and the journal is retained.
        var failingCoordinator = CreateCoordinator(
            quotaObservationStore: new RefusingInvalidationStore(store),
            timeProvider: clock);
        var failedResult = await failingCoordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.PersistenceFailure, failedResult.Status);
        Assert.Equal("TARGET_QUOTA_INVALIDATION_FAILED", failedResult.ReasonCode);
        Assert.True(failedResult.RestartRequired);
        Assert.True(File.Exists(_journal.JournalFilePath));

        // Retry with a healthy store: invalidation succeeds and the journal resolves.
        var healthyCoordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var resolved = await healthyCoordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolved.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Null(targetObs!.RemainingFraction);
    }

    // =========================================================================
    // Group I: Pool-status publication generation (R2)
    // =========================================================================

    /// <summary>
    /// Seeds the durable routing fixture with a fresh target observation carrying future
    /// reset evidence. Invalidation nulls that reset evidence, so EarliestResetTime
    /// distinguishes a pool status computed before an invalidation from one computed after.
    /// </summary>
    private async Task<(DurableQuotaObservationStore Store, EvidenceTestClock Clock)> SeedResetEvidenceRoutingAsync()
    {
        await SeedAccountsAsync();
        var clock = new EvidenceTestClock();
        var path = Path.Combine(_tempDir, "quota-observations-reset.json");
        var store = new DurableQuotaObservationStore(path);
        await store.RecordObservationsAsync(_target!.Id,
            [new(_target.Id, EvidenceModel, 0.8, clock.Now.AddHours(1).ToString("O"), clock.Now.AddMinutes(-5), "ActiveGetUserStatus")]);
        _adapter.RequestedModel = EvidenceModel;
        _adapter.TargetQuota = EvidenceQuota(0.8);
        _adapter.QuotaObservationBehavior = _ => Task.FromResult(new AG2Router.Core.Contracts.AccountQuotaObservation(
            _adapter.Identity, _adapter.Identity?.Email == _source!.Email ? EvidenceQuota(0.05) : _adapter.TargetQuota));
        return (new DurableQuotaObservationStore(path), clock);
    }

    [Fact]
    public async Task PoolStatus_01_ManualSuccess_DoesNotRetainPreviousPoolStatus()
    {
        var (store, clock) = await SeedResetEvidenceRoutingAsync();
        await using var router = NonSwitchingEvidenceRouter(store, clock);

        // Prime an evaluated pool status without executing a switch.
        await router.EvaluateCycleAsync();
        var primed = router.GetStatus().PoolStatus;
        Assert.NotNull(primed);
        Assert.True(primed!.HasUsableCandidate);

        var token = router.NotifyManualSwitchStarted(_target!.Id);
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(
            "synthetic", true, SwitchResultCodes.Success, NativeSwitchStates.Complete,
            _target!.Id, _target.Email, _source!.Id, _source!.Email, "synthetic", [],
            clock.Now.ToString("O"), clock.Now.ToString("O")));

        Assert.Null(router.GetStatus().PoolStatus);
    }

    [Fact]
    public async Task PoolStatus_02_ManualFailure_DoesNotRetainPreviousPoolStatus()
    {
        var (store, clock) = await SeedResetEvidenceRoutingAsync();
        await using var router = NonSwitchingEvidenceRouter(store, clock);

        await router.EvaluateCycleAsync();
        var primed = router.GetStatus().PoolStatus;
        Assert.NotNull(primed);
        Assert.True(primed!.HasUsableCandidate);

        var token = router.NotifyManualSwitchStarted(_target!.Id);
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(
            "synthetic", false, SwitchResultCodes.SwitchFailedRolledBack, NativeSwitchStates.RolledBack,
            _target!.Id, _target!.Email, _source!.Id, _source!.Email, "synthetic", [],
            clock.Now.ToString("O"), clock.Now.ToString("O")));

        Assert.Null(router.GetStatus().PoolStatus);
    }

    [Fact]
    public async Task PoolStatus_03_DurableInvalidationDuringStatusRead_PreventsStaleReadyPublication()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var blocking = new BlockingObservationStore(store);
        await using var router = NonSwitchingEvidenceRouter(blocking, clock);

        // Prime an evaluated READY published against the pre-invalidation evidence revision.
        await router.EvaluateCycleAsync();
        var primed = router.GetStatus().PoolStatus;
        Assert.NotNull(primed);
        Assert.True(primed!.HasUsableCandidate);

        // Suspend the next dashboard read mid-computation on the pre-invalidation snapshot.
        blocking.Arm(await store.GetObservationSnapshotAsync());
        var statusTask = router.GetCandidateEvidenceStatusAsync();
        await blocking.EnteredSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Concurrent durable invalidation performed outside the router (the coordinator or
        // recovery path): no router notification, only the durable evidence revision moves.
        await store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);

        blocking.Release.TrySetResult();
        var status = await statusTask.WaitAsync(TimeSpan.FromSeconds(15));

        // The raced read must not pair the purged target with the old evaluated READY: the
        // revision revalidation forces a fresh computation and a conservative null status.
        var targetRow = Assert.Single(status.Candidates, c => c.AccountId == _target!.Id);
        Assert.Equal("UNKNOWN", targetRow.State);
        Assert.Null(targetRow.RemainingFraction);
        Assert.Null(status.PoolStatus);

        // The stale evaluated cache was lazily invalidated for every consumer.
        Assert.Null(router.GetStatus().PoolStatus);
    }

    private NativeAutoRouter NonSwitchingEvidenceRouter(IQuotaObservationStore store, EvidenceTestClock clock) =>
        new(_accounts, _vault, _adapter, CreateCoordinator(quotaObservationStore: store, timeProvider: clock),
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: false, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store, timeProvider: clock);

    [Fact]
    public async Task PoolStatus_04_FreshSubsequentEvaluation_PublishesCorrectStatus()
    {
        var (store, clock) = await SeedResetEvidenceRoutingAsync();
        // Auto-switch disabled: evaluations publish pool status without executing switches.
        await using var router = new NativeAutoRouter(
            _accounts, _vault, _adapter,
            CreateCoordinator(quotaObservationStore: store, timeProvider: clock),
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: false, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store, timeProvider: clock);

        await router.EvaluateCycleAsync();
        var primed = router.GetStatus().PoolStatus;
        Assert.NotNull(primed);
        Assert.True(primed!.HasUsableCandidate);

        await store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);
        await router.EvaluateCycleAsync();

        var published = router.GetStatus().PoolStatus;
        Assert.NotNull(published);
        Assert.False(published!.HasUsableCandidate);
    }

    [Fact]
    public async Task PoolStatus_05_AutomaticSuccessfulSwitch_DoesNotRetainOldAccountPoolStatus()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouter(store, clock);

        var selection = await router.EvaluateCycleAsync();
        Assert.True(selection.ShouldSwitch);
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.Equal(_target!.Id, await _accounts.GetActiveAccountIdAsync());

        Assert.Null(router.GetStatus().PoolStatus);
    }

    [Fact]
    public async Task PoolStatus_06_ConcurrentStatusReadAndInvalidation_CompletesWithoutDeadlock()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouterWithStore(store, clock);

        for (int round = 0; round < 5; round++)
        {
            var statusTask = router.GetCandidateEvidenceStatusAsync();
            var token = router.NotifyManualSwitchStarted(_target!.Id);
            var invalidateTask = store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);
            var completionTask = router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(
                "synthetic", false, SwitchResultCodes.SwitchFailedRolledBack, NativeSwitchStates.RolledBack,
                _target!.Id, _target!.Email, _source!.Id, _source!.Email, "synthetic", [],
                clock.Now.ToString("O"), clock.Now.ToString("O")));

            await Task.WhenAll(statusTask, invalidateTask, completionTask).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private NativeAutoRouter EvidenceRouterWithStore(IQuotaObservationStore store, EvidenceTestClock clock) =>
        new(_accounts, _vault, _adapter, CreateCoordinator(quotaObservationStore: store, timeProvider: clock),
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store, timeProvider: clock);

    /// <summary>
    /// Suspends the first evidence-snapshot read so a concurrent durable mutation can race a
    /// status computation. The blocked call returns a captured pre-invalidation snapshot and
    /// revision; later calls delegate to the real store so revalidation sees fresh evidence.
    /// </summary>
    private sealed class BlockingObservationStore(IQuotaObservationStore inner) : IQuotaObservationStore
    {
        public long? KnownObservationRevision => inner.KnownObservationRevision;
        public TaskCompletionSource EnteredSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private QuotaObservationSnapshot? _blockedSnapshot;
        private int _snapshotCalls;

        public void Arm(QuotaObservationSnapshot blockedSnapshot) => _blockedSnapshot = blockedSnapshot;

        public async Task<QuotaObservationSnapshot> GetObservationSnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (_blockedSnapshot != null && Interlocked.CompareExchange(ref _snapshotCalls, 1, 0) == 0)
            {
                EnteredSnapshot.TrySetResult();
                await Release.Task.ConfigureAwait(false);
                return _blockedSnapshot;
            }

            return await inner.GetObservationSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<long> GetObservationRevisionAsync(CancellationToken cancellationToken = default) =>
            inner.GetObservationRevisionAsync(cancellationToken);

        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetAllObservationsAsync(CancellationToken cancellationToken = default) =>
            inner.GetAllObservationsAsync(cancellationToken);

        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetObservationsForAccountAsync(
            string accountId, CancellationToken cancellationToken = default) =>
            inner.GetObservationsForAccountAsync(accountId, cancellationToken);

        public Task<AccountModelQuotaObservation?> GetObservationAsync(
            string accountId, string modelKey, CancellationToken cancellationToken = default) =>
            inner.GetObservationAsync(accountId, modelKey, cancellationToken);

        public Task RecordObservationsAsync(
            string accountId, IEnumerable<AccountModelQuotaObservation> observations, CancellationToken cancellationToken = default) =>
            inner.RecordObservationsAsync(accountId, observations, cancellationToken);

        public Task RecordCompleteSnapshotAsync(
            string accountId, IEnumerable<AccountModelQuotaObservation> observations,
            DateTimeOffset observedAtUtc, string source, CancellationToken cancellationToken = default) =>
            inner.RecordCompleteSnapshotAsync(accountId, observations, observedAtUtc, source, cancellationToken);

        public Task InvalidateIfUnchangedAsync(
            AccountModelQuotaObservation expected, DateTimeOffset invalidatedAtUtc, CancellationToken cancellationToken = default) =>
            inner.InvalidateIfUnchangedAsync(expected, invalidatedAtUtc, cancellationToken);

        public Task InvalidateObservationsForAccountAsync(
            string accountId, DateTimeOffset invalidatedAtUtc, CancellationToken cancellationToken = default) =>
            inner.InvalidateObservationsForAccountAsync(accountId, invalidatedAtUtc, cancellationToken);
    }
}
