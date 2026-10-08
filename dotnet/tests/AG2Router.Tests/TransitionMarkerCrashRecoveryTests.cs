using System.IO;
using System.Text;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Switching;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// PROMPT #032 crash-boundary and recovery-matrix tests for the durable transition marker:
/// every frozen durable boundary (predecessor present, predecessor removed, successor
/// published, marker removed) reconciles from a fresh coordinator without ever publishing
/// Clean from the D1 crash shape, admission and resolution classify canonical+marker
/// together, and foreign/corrupt evidence always fails closed with bytes preserved.
/// Frozen states are written through the production journal and marker stores.
/// </summary>
public sealed partial class TargetQuotaVerificationSwitchTests
{
    private string MarkerPath => _journal.JournalFilePath + ".transition";

    private SwitchJournalStore CreateProductionJournalStore() => new(_journal.JournalFilePath);

    private SwitchTransitionMarkerStore CreateProductionMarkerStore() =>
        new(MarkerPath);

    private static SwitchJournalEntry FrozenEntry(
        string transactionId,
        SwitchJournalState state,
        string sourceId,
        string targetId,
        SwitchTargetActivationProvenance provenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED,
        string? quarantineReason = null) =>
        new()
        {
            TransactionId = transactionId,
            State = state,
            UpdatedAt = new DateTimeOffset(2026, 10, 7, 8, 30, 0, TimeSpan.Zero),
            SourceAccountId = sourceId,
            TargetAccountId = targetId,
            QuarantineReasonCode = quarantineReason,
            TargetActivationProvenance = provenance
        };

    private async Task<SwitchJournalEntry> WriteCanonicalAsync(SwitchJournalEntry entry)
    {
        await CreateProductionJournalStore().WriteEntryAsync(entry);
        return entry;
    }

    private async Task<SwitchTransitionMarkerEntry> WriteFrozenMarkerAsync(SwitchJournalEntry expected, SwitchJournalEntry next)
    {
        var marker = new SwitchTransitionMarkerEntry
        {
            TransactionId = expected.TransactionId,
            CreatedAt = new DateTimeOffset(2026, 10, 7, 8, 30, 30, TimeSpan.Zero),
            Expected = expected,
            Next = next
        };
        var created = await CreateProductionMarkerStore().CreateTransitionMarkerIfAbsentAsync(marker);
        Assert.Equal(SwitchTransitionMarkerWriteStatus.Created, created.Status);
        return created.Marker!;
    }

    private async Task<string> WriteRawMarkerAsync(string json)
    {
        await File.WriteAllTextAsync(MarkerPath, json);
        return json;
    }

    // =========================================================================
    // PROMPT #030 D1 kill-shot (PART 25)
    // =========================================================================

    [Fact]
    public async Task TransitionGap_D1_CredentialApplyingToPrecommit_AbsentCanonical_FailsClosed()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var applying = FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        var precommit = FrozenEntry(txId, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);

        // The D1 crash shape: predecessor deleted, successor never published, marker durable.
        await WriteFrozenMarkerAsync(applying, precommit);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.True(File.Exists(MarkerPath));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // Never Clean: recovery blocked, admission closed, target-attempted uncertainty
        // preserved, and the marker survives until correct resolution.
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True(_vault.IsQuarantined);
        Assert.True(File.Exists(MarkerPath));
        Assert.False(File.Exists(_journal.JournalFilePath));

        var blocked = await coordinator.SwitchAsync(_target!.Id);
        Assert.True(blocked.ManualRecoveryRequired);

        // Only correct operator resolution removes the marker.
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolution.Status);
        Assert.False(File.Exists(MarkerPath));
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task TransitionGap_InFlight_PrecommitTransitionFailure_LeavesCoveredEvidence_AndFreshStartBlocks()
    {
        var (store, clock) = await SeedDurableRoutingAsync();

        // The successor publish of the PRECOMMIT transition never lands: after the owned
        // CREDENTIAL_APPLYING entry was removed, the process dies (hook deletes the
        // canonical journal and reports failure). The durable marker must cover the gap.
        var productionStore = CreateProductionJournalStore();
        int replaceCalls = 0;
        productionStore.AfterOwnedDeleteHookAsync = () =>
        {
            if (Interlocked.Increment(ref replaceCalls) == 2)
            {
                File.Delete(_journal.JournalFilePath);
                return Task.FromException(new IOException("Process death between delete and publish"));
            }
            return Task.CompletedTask;
        };

        // Freeze the original marker-only crash boundary before rollback can restore its
        // predecessor bridge. Successful compensation now legitimately rotates evidence.
        productionStore.BeforeCreatePublishHookAsync = () =>
            File.Exists(MarkerPath)
                ? Task.FromException(new IOException("Synthetic death before predecessor restoration"))
                : Task.CompletedTask;

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // The live transaction fails closed into compensation; the disk ends in the
        // crash shape: canonical absent, transition marker durable.
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.True(File.Exists(MarkerPath));

        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var reconciliation = await fresh.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, reconciliation.Status);
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, reconciliation.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, fresh.GetStatus().JournalRecoveryState);
        Assert.False(fresh.CanAdmitSwitch(out _));
        Assert.True(File.Exists(MarkerPath));
    }

    // =========================================================================
    // RECORDED -> CREDENTIAL_APPLYING gap (PART 26) and boundary B
    // =========================================================================

    [Fact]
    public async Task TransitionGap_RecordedToApplying_AbsentCanonical_RuntimeUnproven_RetainsMarker()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var recorded = FrozenEntry(txId, SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        var applying = FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        await WriteFrozenMarkerAsync(recorded, applying);

        _adapter.Identity = null;
        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // RECORDED-level conservatism: source-runtime coherence is required before any
        // cleanup; the vault is NOT quarantined because no credential boundary was crossed.
        Assert.Equal(StartupJournalReconciliationStatus.Degraded, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.False(_vault.IsQuarantined);
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task TransitionGap_RecordedToApplying_AbsentCanonical_RuntimeProven_CleansMarker()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var recorded = FrozenEntry(txId, SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        var applying = FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        await WriteFrozenMarkerAsync(recorded, applying);

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Equal(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(coordinator.CanAdmitSwitch(out _));
        Assert.False(File.Exists(MarkerPath));
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task TransitionGap_RecordedToApplying_PredecessorPresent_ReconcilesAsRecorded()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var recorded = await WriteCanonicalAsync(
            FrozenEntry(txId, SwitchJournalState.RECORDED, _source!.Id, _target!.Id));
        var applying = FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        await WriteFrozenMarkerAsync(recorded, applying);

        // Boundary B: marker durable, predecessor still present, source runtime proven.
        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.False(File.Exists(MarkerPath));
        Assert.False(File.Exists(_journal.JournalFilePath));

        // Durable-source incoherence quarantines instead of cleaning.
        var tx2 = Guid.NewGuid().ToString("D");
        var recorded2 = await WriteCanonicalAsync(
            FrozenEntry(tx2, SwitchJournalState.RECORDED, _source!.Id, _target!.Id));
        await WriteFrozenMarkerAsync(recorded2,
            FrozenEntry(tx2, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        await _accounts.RemoveAccountAsync(_source!.Id);

        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var quarantined = await fresh.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, quarantined.Status);
        Assert.True(fresh.CanAdmitSwitch(out _) == false);
        Assert.True(File.Exists(MarkerPath));
    }

    // =========================================================================
    // ROLLING_BACK and QUARANTINED gaps (PARTS 27-28)
    // =========================================================================

    [Theory]
    [InlineData(SwitchJournalState.RECORDED, SwitchTargetActivationProvenance.NOT_ATTEMPTED)]
    [InlineData(SwitchJournalState.CREDENTIAL_APPLYING, SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED)]
    public async Task TransitionGap_ForwardFailureToRollingBack_AbsentCanonical_FailsClosed(
        SwitchJournalState originState, SwitchTargetActivationProvenance originProvenance)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var origin = FrozenEntry(txId, originState, _source!.Id, _target!.Id, originProvenance);
        var rollingBack = FrozenEntry(txId, SwitchJournalState.ROLLING_BACK, _source!.Id, _target!.Id, originProvenance);
        await WriteFrozenMarkerAsync(origin, rollingBack);

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // Forward mutation failed and compensation may be incomplete: rollback-required
        // semantics are retained, never Clean.
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True(_vault.IsQuarantined);
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task TransitionGap_RollingBackToQuarantined_AbsentCanonical_FailsClosed()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var rollingBack = FrozenEntry(txId, SwitchJournalState.ROLLING_BACK, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        var quarantined = FrozenEntry(txId, SwitchJournalState.QUARANTINED, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, quarantineReason: "ROLLBACK_FAILED");
        await WriteFrozenMarkerAsync(rollingBack, quarantined);

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // Rollback is already known to have failed: QUARANTINED-level conservatism.
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True(_vault.IsQuarantined);
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task TransitionGap_NonRecordedPair_PredecessorPresent_Quarantines()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var applying = await WriteCanonicalAsync(
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        var precommit = FrozenEntry(txId, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        await WriteFrozenMarkerAsync(applying, precommit);

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // The predecessor window may already include credential side effects: retain both.
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.True(File.Exists(MarkerPath));
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    // =========================================================================
    // Matching successor + stale marker (PART 29)
    // =========================================================================

    [Fact]
    public async Task TransitionGap_SuccessorPublished_StaleMarker_CommittedPrecommit_CleansIdempotently()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _accounts.SetActiveAccountIdAsync(_target!.Id);
        var txId = Guid.NewGuid().ToString("D");
        var applying = FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        var precommit = await WriteCanonicalAsync(
            FrozenEntry(txId, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        await WriteFrozenMarkerAsync(applying, precommit);

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // The exact match proves the marker stale: it is removed and the successor is
        // reconciled per its own state (metadata already committed → conditional cleanup).
        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.False(File.Exists(MarkerPath));
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
    }

    [Fact]
    public async Task TransitionGap_SuccessorPublished_StaleMarker_UncommittedState_QuarantinesWithoutMarker()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var recorded = FrozenEntry(txId, SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        var applying = await WriteCanonicalAsync(
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        await WriteFrozenMarkerAsync(recorded, applying);

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // The stale marker is removed exactly; the CREDENTIAL_APPLYING successor keeps its
        // ordinary quarantine semantics.
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.False(File.Exists(MarkerPath));
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
    }

    // =========================================================================
    // Foreign / corrupt / mismatch matrix (PART 30, Correction 2)
    // =========================================================================

    [Fact]
    public async Task TransitionGap_ForeignCanonical_WithMarker_FailsClosedAndPreservesBoth()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = await WriteCanonicalAsync(
            FrozenEntry(Guid.NewGuid().ToString("D"), SwitchJournalState.QUARANTINED, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, quarantineReason: "ROLLBACK_FAILED"));
        var txId = Guid.NewGuid().ToString("D");
        await WriteFrozenMarkerAsync(
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED),
            FrozenEntry(txId, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(File.Exists(MarkerPath));
        Assert.True(File.Exists(_journal.JournalFilePath));
        var current = await CreateProductionJournalStore().ReadAsync();
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);
    }

    [Fact]
    public async Task TransitionGap_ForeignMarker_WithCanonical_FailsClosed()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var canonical = await WriteCanonicalAsync(
            FrozenEntry(Guid.NewGuid().ToString("D"), SwitchJournalState.RECORDED, _source!.Id, _target!.Id));

        // A marker bound to another transaction: neither expected nor next matches the
        // canonical journal, so it must block without adoption or deletion.
        var foreignTx = Guid.NewGuid().ToString("D");
        await WriteFrozenMarkerAsync(
            FrozenEntry(foreignTx, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED),
            FrozenEntry(foreignTx, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.True(File.Exists(MarkerPath));
        Assert.True(File.Exists(_journal.JournalFilePath));
        var current = await CreateProductionJournalStore().ReadAsync();
        Assert.Equal(canonical.TransactionId, current.Entry!.TransactionId);
    }

    [Fact]
    public async Task TransitionGap_SameTransactionId_MateriallyDifferentMarker_FailsClosed()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var canonical = await WriteCanonicalAsync(
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));

        // Same transaction ID but the marker describes a RECORDED -> ROLLING_BACK
        // transition: neither of its entries matches the canonical CREDENTIAL_APPLYING
        // journal, so it is materially different evidence and must fail closed.
        await WriteFrozenMarkerAsync(
            FrozenEntry(txId, SwitchJournalState.RECORDED, _source!.Id, _target!.Id),
            FrozenEntry(txId, SwitchJournalState.ROLLING_BACK, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.NOT_ATTEMPTED));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.True(File.Exists(MarkerPath));
        Assert.True(File.Exists(_journal.JournalFilePath));
        var current = await CreateProductionJournalStore().ReadAsync();
        Assert.Equal(canonical.TransactionId, current.Entry!.TransactionId);
    }

    [Fact]
    public async Task TransitionGap_CorruptMarker_WithValidCanonical_FailsClosedAndPreservesMarker()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var canonical = await WriteCanonicalAsync(
            FrozenEntry(Guid.NewGuid().ToString("D"), SwitchJournalState.RECORDED, _source!.Id, _target!.Id));
        const string corruptContent = "{ corrupt transition marker bytes";
        await WriteRawMarkerAsync(corruptContent);

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // Correction 2: a corrupt marker is unprovable recovery evidence — the valid
        // canonical journal must NOT be cleaned up around it.
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.Equal(JournalRecoveryStates.NotResolvable, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(_vault.IsQuarantined);
        Assert.Equal(corruptContent, await File.ReadAllTextAsync(MarkerPath));
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task TransitionGap_UnsupportedMarker_WithValidCanonical_FailsClosed()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await WriteCanonicalAsync(
            FrozenEntry(Guid.NewGuid().ToString("D"), SwitchJournalState.RECORDED, _source!.Id, _target!.Id));
        await WriteRawMarkerAsync("{\"magic\":\"AG2SWITCHTRNS\",\"schemaVersion\":99}");

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.Equal(JournalRecoveryStates.NotResolvable, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task TransitionGap_CorruptCanonical_WithValidMarker_FailsClosed()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await File.WriteAllTextAsync(_journal.JournalFilePath, "{ corrupt canonical bytes");
        var txId = Guid.NewGuid().ToString("D");
        await WriteFrozenMarkerAsync(
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED),
            FrozenEntry(txId, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.Equal(JournalRecoveryStates.NotResolvable, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(File.Exists(MarkerPath));
        Assert.Equal("{ corrupt canonical bytes", await File.ReadAllTextAsync(_journal.JournalFilePath));
    }

    [Fact]
    public async Task TransitionGap_AbsentCanonical_CorruptMarker_NotResolvable()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await WriteRawMarkerAsync("{ corrupt transition marker bytes");

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, result.Status);
        Assert.Equal(JournalRecoveryStates.NotResolvable, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(coordinator.CanAdmitSwitch(out _) == false);
        Assert.True(File.Exists(MarkerPath));
    }

    // =========================================================================
    // Admission (PART 10)
    // =========================================================================

    [Fact]
    public async Task TransitionAdmission_MarkerPresent_BlocksMutationEvenWithAbsentCanonical()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        await WriteFrozenMarkerAsync(
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED),
            FrozenEntry(txId, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);

        // In-memory state alone would admit; the durable marker must fail admission closed.
        var result = await coordinator.SwitchAsync(_target!.Id);
        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True(File.Exists(MarkerPath));
        Assert.Equal(0, _process.LaunchCount);
        Assert.DoesNotContain("WINCRED_WRITE:antigravity", _globalTimeline);
    }

    // =========================================================================
    // Operator resolution (PART 18)
    // =========================================================================

    [Fact]
    public async Task TransitionResolution_RecordedGap_RuntimeProven_CleanCleanup()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        await WriteFrozenMarkerAsync(
            FrozenEntry(txId, SwitchJournalState.RECORDED, _source!.Id, _target!.Id),
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, resolution.Status);
        Assert.False(File.Exists(MarkerPath));
        Assert.Equal(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
    }

    [Fact]
    public async Task TransitionResolution_CredentialApplyingGap_ProofResolved_RestartRequired()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        await WriteFrozenMarkerAsync(
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED),
            FrozenEntry(txId, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();

        // The intended successor carries MAY_HAVE_BEEN_ATTEMPTED: the operator proof path
        // resolves the gap and the restart requirement keeps switching blocked this process.
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolution.Status);
        Assert.True(resolution.RestartRequired);
        Assert.False(File.Exists(MarkerPath));
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task TransitionResolution_MatchingPredecessor_ProofResolved_CleansBoth()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var txId = Guid.NewGuid().ToString("D");
        var applying = await WriteCanonicalAsync(
            FrozenEntry(txId, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        var precommit = FrozenEntry(txId, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        await WriteFrozenMarkerAsync(applying, precommit);

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolution.Status);
        Assert.False(File.Exists(MarkerPath));
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task TransitionResolution_ForeignPair_FailsClosedAndPreservesBoth()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await WriteCanonicalAsync(
            FrozenEntry(Guid.NewGuid().ToString("D"), SwitchJournalState.ROLLING_BACK, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        var foreignTx = Guid.NewGuid().ToString("D");
        await WriteFrozenMarkerAsync(
            FrozenEntry(foreignTx, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED),
            FrozenEntry(foreignTx, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source!.Id, _target!.Id,
                SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.ProofFailed, resolution.Status);
        Assert.True(File.Exists(MarkerPath));
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task TransitionResolution_CorruptMarker_NotResolvable()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await WriteRawMarkerAsync("{ corrupt transition marker bytes");

        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.Equal(JournalResolutionStatus.NotResolvable, resolution.Status);
        Assert.True(File.Exists(MarkerPath));
    }

    // =========================================================================
    // In-transaction marker protocol behavior
    // =========================================================================

    [Fact]
    public async Task TransitionProtocol_MarkerPersists_BeforePredecessorDeletion()
    {
        var (store, clock) = await SeedDurableRoutingAsync();

        // At the moment the transition marker is durably published, the canonical
        // predecessor must still exist: the marker is never written after deletion.
        var productionStore = CreateProductionJournalStore();
        var observed = new List<(bool CanonicalPresent, string CanonicalState)>();
        productionStore.MarkerStore.AfterPublishHookAsync = () =>
        {
            bool canonicalPresent = File.Exists(_journal.JournalFilePath);
            string canonicalState = "-";
            if (canonicalPresent)
            {
                var read = productionStore.ReadAsync().GetAwaiter().GetResult();
                canonicalState = read.Entry?.State.ToString() ?? read.Status.ToString();
            }

            observed.Add((canonicalPresent, canonicalState));
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.True(result.Success);
        Assert.NotEmpty(observed);
        Assert.All(observed, o => Assert.True(o.CanonicalPresent,
            "The transition marker was published while the canonical predecessor was absent."));
        Assert.Contains(observed, o => o.CanonicalState == SwitchJournalState.RECORDED.ToString());
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task TransitionProtocol_OwnStaleMarker_IsReplacedExactly_RollbackIntentJournaled()
    {
        var (store, clock) = await SeedDurableRoutingAsync();

        // The CREDENTIAL_APPLYING replace fails with the predecessor intact AFTER the
        // transition marker was created (fault at the first disposition boundary only).
        // The rollback transition must replace the transaction's own stale marker exactly
        // with the ROLLING_BACK intent BEFORE deleting the predecessor, and compensation
        // proceeds — recovery ends clean after the verified rollback.
        var productionStore = CreateProductionJournalStore();
        int dispositions = 0;
        var markerStatesAtDispositions = new List<string>();
        productionStore.BeforeDispositionHookAsync = () =>
        {
            if (Interlocked.Increment(ref dispositions) == 1)
            {
                return Task.FromException(new IOException("Simulated crash between marker and delete"));
            }

            // Second disposition = the rollback transition deleting its predecessor: the
            // durable marker must already carry the ROLLING_BACK intent. Any later
            // disposition is terminal cleanup, after the marker was proven removed.
            var markerRead = productionStore.ReadTransitionMarkerAsync().GetAwaiter().GetResult();
            markerStatesAtDispositions.Add(markerRead.Marker?.Next.State.ToString() ?? $"({markerRead.Status})");
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // A ROLLING_FAILED result would mean the rollback journal write failed; the
        // verified-rollback result proves the ROLLING_BACK intent was journaled and the
        // rollback completed under it.
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.NotEmpty(markerStatesAtDispositions);
        Assert.Equal(SwitchJournalState.ROLLING_BACK.ToString(), markerStatesAtDispositions[0]);
        Assert.Equal(1, _process.RestoreCount);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.False(File.Exists(MarkerPath));
        Assert.Equal(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task TransitionProtocol_MarkerCleanupFailure_AtSuccess_KeepsSuccessAndBlocksRecovery()
    {
        var (store, clock) = await SeedDurableRoutingAsync();

        // After the PRECOMMIT transition completed (its own marker removed at STEP 6), a
        // different marker occupies the marker path before terminal cleanup. The exact
        // owned-marker removal must then fail closed: business success stands, the
        // surviving unresolved marker keeps recovery blocking, and no clean state is
        // published while it remains.
        var productionStore = CreateProductionJournalStore();
        var interferingAccounts = new FinalizeInterferenceStore(_accounts, onFinalize: () =>
        {
            File.Delete(MarkerPath);
            var replacementTxId = Guid.NewGuid().ToString("D");
            var replacement = new SwitchTransitionMarkerEntry
            {
                TransactionId = replacementTxId,
                Expected = FrozenEntry(replacementTxId, SwitchJournalState.RECORDED,
                    _source!.Id, _target!.Id),
                Next = FrozenEntry(replacementTxId, SwitchJournalState.CREDENTIAL_APPLYING,
                    _source!.Id, _target!.Id, SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED)
            };
            productionStore.MarkerStore.CreateTransitionMarkerIfAbsentAsync(replacement).GetAwaiter().GetResult();
        });

        var coordinator = CreateCoordinatorWithStore(productionStore, accountStore: interferingAccounts, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.True(result.Success);
        Assert.Contains("JOURNAL_CLEANUP_REQUIRES_RECONCILIATION", result.StagesCompleted);
        Assert.True(File.Exists(MarkerPath));
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));

        // A fresh reconciliation of the surviving foreign marker fails closed (the source
        // runtime now reports the target identity, so the RECORDED-origin gap cannot prove
        // source coherence).
        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var reconciliation = await fresh.ReconcileStartupJournalAsync();
        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, reconciliation.Status);
    }

    [Fact]
    public async Task R1_NormalMarkerCleanup_GuardedCanonical_DeniesExternalDeletion_EvidencePreserved()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();
        bool deletionAttempted = false;
        bool deletionDenied = false;

        productionStore.MarkerStore.BeforeDispositionHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && !deletionAttempted)
            {
                deletionAttempted = true;
                try
                {
                    File.Delete(_journal.JournalFilePath);
                }
                catch (IOException)
                {
                    deletionDenied = true;
                }
            }
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.True(result.Success);
        Assert.True(deletionAttempted, "Expected hook to attempt deletion during marker cleanup");
        Assert.True(deletionDenied, "Expected File.Delete to fail with IOException due to exact canonical companion guard");
        Assert.False(File.Exists(MarkerPath), "Marker should be cleaned up successfully");
        Assert.False(File.Exists(_journal.JournalFilePath), "Terminal cleanup cleans up at switch finalization");
    }

    [Fact]
    public async Task R1_CanonicalLostBeforeMarkerCleanup_PreservesMarker_CredentialMutationAborted_FreshStartupBlocks()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();
        bool canonicalDeletedBeforeGuard = false;

        productionStore.BeforeGuardAcquireHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && !canonicalDeletedBeforeGuard)
            {
                canonicalDeletedBeforeGuard = true;
                File.Delete(_journal.JournalFilePath);
            }
            return Task.CompletedTask;
        };

        // Freeze the crash boundary: process terminates before rollback can restore anything
        productionStore.BeforeCreatePublishHookAsync = () =>
            canonicalDeletedBeforeGuard && File.Exists(MarkerPath)
                ? Task.FromException(new IOException("Synthetic crash before rollback restoration"))
                : Task.CompletedTask;

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.True(canonicalDeletedBeforeGuard, "Expected canonical to be deleted before guard acquisition");
        Assert.Equal(0, _credentials.WriteCount);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.True(File.Exists(MarkerPath), "Marker must be preserved when canonical companion guard fails");

        // Fresh startup when runtime is unproven blocks admission with Degraded/ActionRequired
        _adapter.Identity = null;
        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var startupResult = await fresh.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Degraded, startupResult.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, fresh.GetStatus().JournalRecoveryState);
        Assert.False(fresh.CanAdmitSwitch(out _));

        // Operator resolution cannot succeed without proven runtime
        var resolutionFailed = await fresh.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ProofFailed, resolutionFailed.Status);
        Assert.True(File.Exists(MarkerPath));

        // When runtime coherence is restored, operator resolution resolves the surviving marker
        _adapter.Identity = new AccountIdentityDto(_source!.Email);
        var resolution = await fresh.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, resolution.Status);
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task PrecommitTransition_CanonicalLossBeforeMarkerCleanup_PreservesMarker_BlocksAdmission()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();
        bool precommitCanonicalDeleted = false;

        productionStore.BeforeGuardAcquireHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && !precommitCanonicalDeleted)
            {
                var current = productionStore.ReadAsync().GetAwaiter().GetResult();
                if (current.Entry?.State == SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT)
                {
                    precommitCanonicalDeleted = true;
                    File.Delete(_journal.JournalFilePath);
                }
            }
            return Task.CompletedTask;
        };

        // Freeze crash boundary before rollback can restore anything
        productionStore.BeforeCreatePublishHookAsync = () =>
            precommitCanonicalDeleted && File.Exists(MarkerPath)
                ? Task.FromException(new IOException("Synthetic crash before rollback restoration"))
                : Task.CompletedTask;

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.True(precommitCanonicalDeleted, "Expected precommit canonical to be deleted before guard acquisition");
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.True(File.Exists(MarkerPath), "Marker must be preserved when precommit guard fails");

        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var startupResult = await fresh.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, startupResult.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, fresh.GetStatus().JournalRecoveryState);
        Assert.False(fresh.CanAdmitSwitch(out _));

        // Operator resolution fails while credential and metadata are incoherent from the crash
        var resolution = await fresh.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ProofFailed, resolution.Status);
        Assert.True(File.Exists(MarkerPath), "Marker survives failed resolution");

        // Once credential and live identity match active account metadata, resolution resolves the surviving marker
        _adapter.Identity = new AccountIdentityDto(_source!.Email);
        _credentials.Set(new WinCredEntry("gemini:antigravity", 1, _source!.Email, 2, (await _vault.GetSessionForRecoveryAsync(_source!.Id, CancellationToken.None))!));
        var resolutionSuccess = await fresh.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolutionSuccess.Status);
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task StartupStaleMarkerCleanup_Interference_PreservesMarker_BlocksAdmission()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var tx = Guid.NewGuid().ToString("D");
        var recorded = FrozenEntry(tx, SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        var applying = FrozenEntry(tx, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);

        await WriteCanonicalAsync(applying);
        await WriteFrozenMarkerAsync(recorded, applying);

        var productionStore = CreateProductionJournalStore();
        bool canonicalDeletedInStartupCleanup = false;
        productionStore.BeforeGuardAcquireHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && !canonicalDeletedInStartupCleanup)
            {
                canonicalDeletedInStartupCleanup = true;
                File.Delete(_journal.JournalFilePath);
            }
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var startupResult = await coordinator.ReconcileStartupJournalAsync();

        Assert.True(canonicalDeletedInStartupCleanup);
        Assert.Equal(StartupJournalReconciliationStatus.Degraded, startupResult.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(File.Exists(MarkerPath), "Transition marker must survive when startup canonical guard fails");
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task ResolutionStaleMarkerCleanup_Interference_PreservesMarker_BlocksAdmission()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var tx = Guid.NewGuid().ToString("D");
        var recorded = FrozenEntry(tx, SwitchJournalState.RECORDED, _source!.Id, _target!.Id);
        var applying = FrozenEntry(tx, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);

        await WriteCanonicalAsync(applying);
        await WriteFrozenMarkerAsync(recorded, applying);

        var productionStore = CreateProductionJournalStore();
        bool canonicalDeletedInResolution = false;
        productionStore.BeforeGuardAcquireHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && !canonicalDeletedInResolution)
            {
                canonicalDeletedInResolution = true;
                File.Delete(_journal.JournalFilePath);
            }
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();

        Assert.True(canonicalDeletedInResolution);
        Assert.Equal(JournalResolutionStatus.PersistenceFailure, resolution.Status);
        Assert.Equal("DELETE_FAILED", resolution.ReasonCode);
        Assert.True(File.Exists(MarkerPath), "Transition marker must survive when resolution canonical guard fails");
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task RollbackTransition_CanonicalLossBeforeMarkerCleanup_PreservesMarker_BlocksAdmission()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();
        bool rollbackCanonicalDeleted = false;

        // Force a failure after credential apply to trigger rollback
        _adapter.QuotaObservationBehavior = _ =>
            throw new InvalidOperationException("Simulated target quota verification failure");

        productionStore.BeforeGuardAcquireHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && !rollbackCanonicalDeleted)
            {
                var current = productionStore.ReadAsync().GetAwaiter().GetResult();
                if (current.Entry?.State == SwitchJournalState.ROLLING_BACK)
                {
                    rollbackCanonicalDeleted = true;
                    File.Delete(_journal.JournalFilePath);
                }
            }
            return Task.CompletedTask;
        };

        // Freeze crash boundary before any further rollback writes
        productionStore.BeforeCreatePublishHookAsync = () =>
            rollbackCanonicalDeleted && File.Exists(MarkerPath)
                ? Task.FromException(new IOException("Synthetic crash after rollback canonical loss"))
                : Task.CompletedTask;

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.True(rollbackCanonicalDeleted, "Expected ROLLING_BACK canonical to be deleted before guard acquisition");
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.True(File.Exists(MarkerPath), "Marker must be preserved when rollback canonical guard fails");

        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var startupResult = await fresh.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, startupResult.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, fresh.GetStatus().JournalRecoveryState);
        Assert.False(fresh.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task NormalMarkerCleanup_InterferenceRenameOrTruncate_DeniedByGuard_EvidenceProtected()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();
        bool moveAttempted = false;
        bool moveDenied = false;
        bool writeAttempted = false;
        bool writeDenied = false;

        productionStore.MarkerStore.BeforeDispositionHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && !moveAttempted)
            {
                moveAttempted = true;
                try
                {
                    File.Move(_journal.JournalFilePath, _journal.JournalFilePath + ".moved");
                }
                catch (IOException)
                {
                    moveDenied = true;
                }

                writeAttempted = true;
                try
                {
                    using var fs = new FileStream(_journal.JournalFilePath, FileMode.Open, FileAccess.Write, FileShare.None);
                    fs.WriteByte(0xFF);
                }
                catch (IOException)
                {
                    writeDenied = true;
                }
            }
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.True(result.Success);
        Assert.True(moveAttempted, "File.Move should have been attempted");
        Assert.True(moveDenied, "File.Move must be denied by Windows FileShare.Read guard");
        Assert.True(writeAttempted, "Write should have been attempted");
        Assert.True(writeDenied, "Write must be denied by Windows FileShare.Read guard");
        Assert.False(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task Prompt038_F1_Reproduce_CredentialApplyingGuardConflict_AbortsBeforeCredentialWrite_ZeroWrites()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();
        FileStream? competingHandle = null;

        productionStore.BeforeGuardAcquireHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && competingHandle == null)
            {
                var current = productionStore.ReadAsync().GetAwaiter().GetResult();
                if (current.Entry?.State == SwitchJournalState.CREDENTIAL_APPLYING)
                {
                    // Competing write handle opened immediately before canonical guard acquisition
                    competingHandle = new FileStream(_journal.JournalFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            }
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        NativeSwitchResult result;
        try
        {
            result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);
        }
        finally
        {
            competingHandle?.Dispose();
        }

        Assert.False(result.Success);
        Assert.NotNull(competingHandle); // Proves the competing handle was actively opened at the CREDENTIAL_APPLYING boundary
        // CRITICAL PROMPT #038 INVARIANT: Zero forward credential writes when marker cleanup completion is failed/uncertain
        Assert.Equal(0, _credentials.WriteCount);

        // Both recovery artifacts remain durable on disk
        Assert.True(File.Exists(_journal.JournalFilePath), "Canonical journal must remain on disk");
        Assert.True(File.Exists(MarkerPath), "Transition marker must remain on disk");
        var canonical = await productionStore.ReadAsync();
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, canonical.Entry?.State);
        var marker = await productionStore.ReadTransitionMarkerAsync();
        Assert.Equal(SwitchJournalState.RECORDED, marker.Marker?.Expected.State);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, marker.Marker?.Next.State);

        // Recovery state is blocking
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));

        // Fresh coordinator detects the retained un-reconciled evidence and blocks admission
        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: store, clock: clock);
        var startupResult = await fresh.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, startupResult.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, fresh.GetStatus().JournalRecoveryState);
        Assert.False(fresh.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task Prompt038_GuardedCleanupException_AbortsBeforeCredentialWrite_ZeroWrites()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();

        // Injected exception during marker deletion under canonical guard
        productionStore.MarkerStore.BeforeDispositionHookAsync = () =>
            throw new IOException("Synthetic disk error during marker disposition");

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        // Zero forward credential writes
        Assert.Equal(0, _credentials.WriteCount);

        // Recovery evidence preserved
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.True(File.Exists(MarkerPath));
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task Prompt038_PrecommitGuardConflict_AbortsBeforeMetadataCommit_PreservesEvidence()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();
        FileStream? competingHandle = null;

        productionStore.BeforeGuardAcquireHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && competingHandle == null)
            {
                var current = productionStore.ReadAsync().GetAwaiter().GetResult();
                if (current.Entry?.State == SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT)
                {
                    competingHandle = new FileStream(_journal.JournalFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            }
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        NativeSwitchResult result;
        try
        {
            result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);
        }
        finally
        {
            competingHandle?.Dispose();
        }

        Assert.False(result.Success);
        Assert.NotNull(competingHandle);

        // METADATA_COMMITTED must never be reached
        Assert.DoesNotContain("METADATA_COMMITTED", result.StagesCompleted);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());

        // Recovery remains blocking and quarantined
        Assert.True(result.ManualRecoveryRequired || coordinator.GetStatus().JournalRecoveryState != JournalRecoveryStates.None);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task Prompt038_PrecommitGuardedCleanupException_AbortsBeforeMetadataCommit_PreventsCommitAndRollsBack()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();

        // Injected exception during PRECOMMIT marker deletion under canonical guard
        productionStore.MarkerStore.BeforeDispositionHookAsync = () =>
        {
            var current = productionStore.ReadAsync().GetAwaiter().GetResult();
            if (current.Entry?.State == SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT)
            {
                throw new IOException("Synthetic marker disposition failure at PRECOMMIT boundary.");
            }
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);

        // METADATA_COMMITTED must never be reached
        Assert.DoesNotContain("METADATA_COMMITTED", result.StagesCompleted);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());

        // Target credential was applied, but rollback safely restored original credential and process
        Assert.Contains("ORIGINAL_CREDENTIAL_RESTORED", result.StagesCompleted);
        Assert.Contains("SOURCE_PROCESS_RESTORED", result.StagesCompleted);

        // Recovery remains blocking
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task Prompt038_RollbackGuardConflict_ProceedsWithCompensation_RecoveryBlocked()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var productionStore = CreateProductionJournalStore();
        FileStream? competingHandle = null;

        // Force a failure after credential apply to trigger rollback
        _adapter.QuotaObservationBehavior = _ =>
            throw new InvalidOperationException("Simulated target quota verification failure");

        productionStore.BeforeGuardAcquireHookAsync = () =>
        {
            if (File.Exists(_journal.JournalFilePath) && competingHandle == null)
            {
                var current = productionStore.ReadAsync().GetAwaiter().GetResult();
                if (current.Entry?.State == SwitchJournalState.ROLLING_BACK)
                {
                    competingHandle = new FileStream(_journal.JournalFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            }
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinatorWithStore(productionStore, quotaStore: store, clock: clock);
        NativeSwitchResult result;
        try
        {
            result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);
        }
        finally
        {
            competingHandle?.Dispose();
        }

        Assert.False(result.Success);
        Assert.NotNull(competingHandle);

        // Compensation completed: original credential and source process restored
        Assert.Contains("ORIGINAL_CREDENTIAL_RESTORED", result.StagesCompleted);
        Assert.Contains("SOURCE_PROCESS_RESTORED", result.StagesCompleted);
        Assert.Contains("SOURCE_IDENTITY_VERIFIED", result.StagesCompleted);
        Assert.Contains("JOURNAL_CLEANUP_REQUIRES_RECONCILIATION", result.StagesCompleted);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);

        // Recovery is blocking
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True(File.Exists(MarkerPath), "Marker must be retained when rollback marker cleanup encounters conflict");
    }
}
