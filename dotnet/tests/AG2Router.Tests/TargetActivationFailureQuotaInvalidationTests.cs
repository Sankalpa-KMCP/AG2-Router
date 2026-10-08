using System.IO;
using System.Text.Json;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Switching;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed partial class TargetQuotaVerificationSwitchTests
{
    // =========================================================================
    // Group: Target Activation Failure Quota Invalidation (PROMPT #014)
    // =========================================================================

    [Fact]
    public async Task TargetActivationFailure_01_IdentityTimeout_RollbackRestoresSource_AndInvalidatesTargetQuota()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        // Process starts, but identity never appears (e.g. daemon stalled, network failure, or timeout)
        _process.OnReplacement = () =>
        {
            _adapter.Identity = null;
        };

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(NativeSwitchStates.RolledBack, result.State);
        Assert.Contains("ROLLBACK_STARTED", result.StagesCompleted);
        Assert.Contains("ORIGINAL_CREDENTIAL_RESTORED", result.StagesCompleted);
        Assert.Contains("SOURCE_PROCESS_RESTORED", result.StagesCompleted);
        Assert.Contains("SOURCE_IDENTITY_VERIFIED", result.StagesCompleted);
        Assert.Contains("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);

        // Source account remained active
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal(0, _accounts.FinalizeCalls);

        // Target quota observation is invalidated
        var targetObs = await store.GetObservationAsync(_target.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Null(targetObs.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", targetObs.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_02_IdentityMismatch_RollbackRestoresSource_AndInvalidatesTargetQuota()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        // Process starts with mismatched identity
        _process.OnReplacement = () =>
        {
            _adapter.Identity = new AccountIdentityDto("unexpected@example.com");
        };

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(NativeSwitchStates.RolledBack, result.State);
        Assert.Contains("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);

        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());

        var targetObs = await store.GetObservationAsync(_target.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Null(targetObs.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", targetObs.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_03_AfterInvalidation_CandidateSelectorRejectsTarget()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        var accounts = await _accounts.ListAccountsAsync();
        var durableObs = await store.GetObservationsForAccountAsync(_target!.Id);
        var targetObsMap = durableObs.ToDictionary(o => o.AccountId, StringComparer.Ordinal);

        var config = new RouterConfigDto(
            AutoSwitchEnabled: true,
            LowQuotaThresholdPercent: 15,
            MinimumCandidateQuotaPercent: 30,
            WorkloadModelKey: EvidenceModel);

        var selection = CandidateSelector.SelectBestCandidate(
            _source!.Id,
            0.05,
            accounts,
            new Dictionary<string, double>(),
            config,
            vaultedAccountIds: new HashSet<string>([_source.Id, _target.Id]),
            relevantModelKeys: [EvidenceModel],
            candidateModelObservations: targetObsMap,
            evaluationTimeUtc: clock.Now);

        Assert.False(selection.ShouldSwitch);
        var candidateEval = Assert.Single(selection.Candidates, c => c.Account.Id == _target.Id);
        Assert.False(candidateEval.IsEligible);
        Assert.Contains("unknown", candidateEval.IneligibilityReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(selection.PoolStatus);
        Assert.Equal(CandidatePoolReasonCodes.EvidenceStaleOrUnknown, selection.PoolStatus.ReasonCode);
    }

    [Fact]
    public async Task TargetActivationFailure_04_AutoRouterCooldownExpiry_DoesNotReselectFailedTarget()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = new NativeAutoRouter(
            _accounts,
            _vault,
            _adapter,
            CreateCoordinator(verificationTimeout: TimeSpan.FromMilliseconds(50), quotaObservationStore: store, timeProvider: clock),
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        // First cycle: triggers switch, identity fails, rolls back, invalidates target evidence
        await router.EvaluateCycleAsync();
        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(1, _process.RestoreCount);
        Assert.Equal(0, _accounts.FinalizeCalls);

        // Advance past the 60s candidate cooldown
        clock.Advance(TimeSpan.FromSeconds(65));

        // Second cycle: target is no longer in candidate cooldown, but quota is invalidated/unknown
        var decision = await router.EvaluateCycleAsync();
        Assert.False(decision.ShouldSwitch);
        Assert.Equal(1, _process.LaunchCount); // Zero new switch attempts!
        Assert.Equal(0, _accounts.FinalizeCalls);
    }

    [Fact]
    public async Task TargetActivationFailure_05_PreservesSourceQuotaEvidenceIntact()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var sourceObsTime = clock.Now.AddMinutes(-5);
        await store.RecordObservationsAsync(_source!.Id,
            [new(_source.Id, EvidenceModel, 0.05, null, sourceObsTime, "ActiveGetUserStatus")]);

        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // Source observation is completely untouched
        var sourceObs = await store.GetObservationAsync(_source.Id, EvidenceModel);
        Assert.NotNull(sourceObs);
        Assert.Equal(0.05, sourceObs.RemainingFraction);
        Assert.Equal(sourceObsTime, sourceObs.ObservedAtUtc);
        Assert.Equal("ActiveGetUserStatus", sourceObs.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_06_PreservesUnrelatedCandidateEvidenceIntact()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var thirdAccount = await _accounts.AddAccountAsync(new CreateAccountInput(
            Email: "third@example.com", HasVaultedSession: true));
        thirdAccount = await _accounts.UpdateAccountAsync(thirdAccount.Id,
            new UpdateAccountInput(ValidationStatus: AccountValidationStatus.Valid)) ?? thirdAccount;
        await _vault.SaveSessionAsync(thirdAccount.Id, System.Text.Encoding.UTF8.GetBytes("third-secret"));

        var thirdObsTime = clock.Now.AddMinutes(-10);
        await store.RecordObservationsAsync(thirdAccount.Id,
            [new(thirdAccount.Id, EvidenceModel, 0.75, null, thirdObsTime, "ActiveGetUserStatus")]);

        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // Unrelated candidate observation is completely untouched
        var thirdObs = await store.GetObservationAsync(thirdAccount.Id, EvidenceModel);
        Assert.NotNull(thirdObs);
        Assert.Equal(0.75, thirdObs.RemainingFraction);
        Assert.Equal(thirdObsTime, thirdObs.ObservedAtUtc);
        Assert.Equal("ActiveGetUserStatus", thirdObs.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_07_SuccessfulTargetVerification_DoesNotInvalidateTargetEvidence()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            quotaObservationStore: store,
            timeProvider: clock);

        // Normal successful target identity and quota
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.DoesNotContain("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);

        var targetObs = await store.GetObservationAsync(_target.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Equal(0.8, targetObs.RemainingFraction);
        Assert.Equal("LiveTargetVerification", targetObs.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_08_WhenRollbackFails_QuarantinesAndPreservesJournal()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };
        _process.RestoreError = new IOException("Hardware fault during source process restore");

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.Contains("ROLLBACK_FAILED", result.StagesCompleted);
        Assert.True(coordinator.GetStatus().QuarantineActive);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task TargetActivationFailure_09_DoesNotMarkAccountValidationStatusExpiredOrFailed()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // Target metadata MUST NOT be marked EXPIRED or FAILED on ambiguous timeout
        var targetMetadata = await _accounts.GetAccountAsync(_target!.Id);
        Assert.NotNull(targetMetadata);
        Assert.Equal(AccountValidationStatus.Valid, targetMetadata.ValidationStatus);
    }

    [Fact]
    public async Task TargetActivationFailure_10_LaterFreshObservation_RestoresCandidateEligibility()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = new NativeAutoRouter(
            _accounts,
            _vault,
            _adapter,
            CreateCoordinator(verificationTimeout: TimeSpan.FromMilliseconds(50), quotaObservationStore: store, timeProvider: clock),
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        // Cycle 1: fails and invalidates target evidence
        await router.EvaluateCycleAsync();
        Assert.Equal(1, _process.LaunchCount);

        clock.Advance(TimeSpan.FromSeconds(65));
        var decisionStale = await router.EvaluateCycleAsync();
        Assert.False(decisionStale.ShouldSwitch);

        // Later, target receives a fresh observation (e.g. from manual switch or enrollment)
        await store.RecordObservationsAsync(_target!.Id,
            [new(_target.Id, EvidenceModel, 0.85, null, clock.Now, "ActiveGetUserStatus")]);
        _adapter.TargetQuota = EvidenceQuota(0.85);

        // Replacement process identity now works
        _process.OnReplacement = () =>
        {
            _adapter.Identity = new AccountIdentityDto(_target.Email);
        };

        // Cycle 3: target is now eligible again and switch completes!
        var decisionFresh = await router.EvaluateCycleAsync();
        Assert.True(decisionFresh.ShouldSwitch);
        Assert.Equal(2, _process.LaunchCount);
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.Equal(_target.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task TargetActivationFailure_11_InvalidationIsDurableAcrossStoreReload()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // Reload store from disk
        var reloadedStore = new DurableQuotaObservationStore(store.GetFilePath());
        var targetObs = await reloadedStore.GetObservationAsync(_target!.Id, EvidenceModel);

        Assert.NotNull(targetObs);
        Assert.Null(targetObs.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", targetObs.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_12_WhenStoreThrowsDuringInvalidation_QuarantinesCoordinator()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var refusingStore = new RefusingInvalidationStore(store);
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: refusingStore,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Contains("TARGET_QUOTA_INVALIDATION_FAILED", result.StagesCompleted);
        Assert.True(coordinator.GetStatus().QuarantineActive);
        Assert.True(File.Exists(_journal.JournalFilePath)); // Journal retained because quarantine is marked
    }

    [Fact]
    public async Task TargetActivationFailure_13_RecoveryResolution_RetriesTargetQuotaInvalidation_AndDeletesJournal()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var refusingStore = new RefusingInvalidationStore(store);
        var coordinatorWithFailingStore = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: refusingStore,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        // 1. Switch fails, invalidation throws, journal is retained in ROLLING_BACK
        var switchResult = await coordinatorWithFailingStore.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(switchResult.Success);
        Assert.True(switchResult.ManualRecoveryRequired);
        Assert.True(File.Exists(_journal.JournalFilePath));

        // Note: target quota in durable store was NOT invalidated yet because refusingStore threw
        var targetObsBefore = await store.GetObservationAsync(_target.Id, EvidenceModel);
        Assert.NotNull(targetObsBefore);
        Assert.Equal(0.8, targetObsBefore.RemainingFraction);

        // 2. Now operator calls ResolveQuarantinedJournalAsync with a healthy store
        var recoveryCoordinator = CreateCoordinator(
            quotaObservationStore: store,
            timeProvider: clock);

        var resolveResult = await recoveryCoordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolveResult.Status);
        Assert.True(resolveResult.RestartRequired);
        Assert.False(File.Exists(_journal.JournalFilePath)); // Journal successfully removed!

        // Target quota observation was retried and durably invalidated!
        var targetObsAfter = await store.GetObservationAsync(_target.Id, EvidenceModel);
        Assert.NotNull(targetObsAfter);
        Assert.Null(targetObsAfter.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", targetObsAfter.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_14_RecoveryResolution_WhenInvalidationFails_DoesNotDeleteJournal_AndFailsClosed()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var refusingStore = new RefusingInvalidationStore(store);
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: refusingStore,
            timeProvider: clock);

        _process.OnReplacement = () => { _adapter.Identity = null; };

        var switchResult = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.True(File.Exists(_journal.JournalFilePath));

        // When resolving, if invalidation throws, resolution must fail closed
        var resolveResult = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.PersistenceFailure, resolveResult.Status);
        Assert.Equal("TARGET_QUOTA_INVALIDATION_FAILED", resolveResult.ReasonCode);
        Assert.True(resolveResult.RestartRequired);
        Assert.True(File.Exists(_journal.JournalFilePath)); // Journal MUST NOT be deleted!
        Assert.True(coordinator.GetStatus().QuarantineActive);
    }

    [Fact]
    public async Task TargetActivationFailure_15_RecoveryResolution_WhenTargetIsProvenActive_DoesNotInvalidateTargetQuota()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        // Setup target as the active account across all 4 pillars
        await _accounts.SetActiveAccountIdAsync(_target!.Id);
        _adapter.Identity = new AccountIdentityDto(_target!.Email);
        _credentials.Set(new WinCredEntry("gemini:antigravity", 1, _target.Email, 2, System.Text.Encoding.UTF8.GetBytes("target-secret-payload")));

        // Write a TARGET_IDENTITY_VERIFIED_PRECOMMIT journal entry
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            Magic = SwitchJournalEntry.CurrentMagic,
            SchemaVersion = SwitchJournalEntry.CurrentSchemaVersion,
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target.Id,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var resolveResult = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, resolveResult.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));

        // Target quota was NOT invalidated because target is the proven active account
        var targetObs = await store.GetObservationAsync(_target.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Equal(0.8, targetObs.RemainingFraction);
    }

    [Fact]
    public async Task TargetActivationFailure_16_RecoveryResolution_RecordedState_DoesNotInvalidateTargetQuota()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            Magic = SwitchJournalEntry.CurrentMagic,
            SchemaVersion = SwitchJournalEntry.CurrentSchemaVersion,
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var resolveResult = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, resolveResult.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));

        // Target quota was NOT invalidated for clean RECORDED state
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Equal(0.8, targetObs.RemainingFraction);
    }

    [Fact]
    public async Task TargetActivationFailure_17_SourceStopFailure_DoesNotInvalidateTargetQuota()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        // Source process stop fails after onStopAttempted is called
        _process.AfterStopIssued = () => throw new IOException("Source process stop timeout / failure");

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.DoesNotContain("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);

        // Pre-activation failure must NOT invalidate target quota observations!
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Equal(0.8, targetObs.RemainingFraction);
        Assert.Equal("ActiveGetUserStatus", targetObs.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_18_PreTargetSnapshotFailure_DoesNotInvalidateTargetQuota()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        // Simulate failure capturing quiesced credential after source stop
        _process.AfterStopIssued = () =>
        {
            _credentials.Clear();
            return Task.CompletedTask;
        };

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.DoesNotContain("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);

        // Target quota observation is preserved intact
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Equal(0.8, targetObs.RemainingFraction);
    }

    [Fact]
    public async Task TargetActivationFailure_19_TargetActivationBegins_LaunchFailure_DoesInvalidateTargetQuota()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);

        // Process launch fails after target credentials have been written
        _process.ReplacementError = new IOException("Language server crashed on startup with target credentials");

        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Contains("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);

        // Target activation was attempted; quota observation MUST be invalidated
        var targetObs = await store.GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(targetObs);
        Assert.Null(targetObs.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", targetObs.Source);
    }

    [Fact]
    public async Task TargetActivationFailure_20_AutoRouter_SwitchFailureRollback_ResetsPoolStatus_NoStaleReadyStatus()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = new NativeAutoRouter(
            _accounts,
            _vault,
            _adapter,
            CreateCoordinator(verificationTimeout: TimeSpan.FromMilliseconds(50), quotaObservationStore: store, timeProvider: clock),
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store,
            timeProvider: clock);

        // Process starts, but identity fails
        _process.OnReplacement = () => { _adapter.Identity = null; };

        // Evaluate cycle triggers switch (where selection was READY), which fails and rolls back
        var selection = await router.EvaluateCycleAsync();
        Assert.NotNull(selection.PoolStatus);
        Assert.Equal(CandidatePoolReasonCodes.Ready, selection.PoolStatus.ReasonCode);
        Assert.True(selection.PoolStatus.HasUsableCandidate);
        Assert.True(selection.ShouldSwitch);

        // Immediately after rollback, the purged evidence must not be presented as usable:
        // the candidate row is UNKNOWN and no pool status is served for it (neither a stale
        // evaluated READY nor a fabricated assessment).
        var afterRollbackEvidenceStatus = await router.GetCandidateEvidenceStatusAsync();
        var afterRollbackTarget = Assert.Single(afterRollbackEvidenceStatus.Candidates, c => c.AccountId == _target!.Id);
        Assert.Equal("UNKNOWN", afterRollbackTarget.State);
        Assert.Null(afterRollbackTarget.RemainingFraction);
        Assert.Null(afterRollbackEvidenceStatus.PoolStatus);

        // RouterStatusDto also must not expose a stale READY with a usable candidate.
        Assert.Null(router.GetStatus().PoolStatus);
    }
}
