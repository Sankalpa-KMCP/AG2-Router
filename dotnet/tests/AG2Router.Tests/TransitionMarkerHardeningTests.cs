using System.IO;
using System.Text.Json;
using AG2Router.AG2.Switching;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed partial class TargetQuotaVerificationSwitchTests
{
    private async Task<SwitchTransitionMarkerReadResult> ReadMarkerDuringDispositionAsync()
    {
        using var stream = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return SwitchTransitionMarkerStore.ParseMarkerFromText(await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("before_restore")]
    [InlineData("after_restore")]
    [InlineData("restore_ambiguous")]
    [InlineData("foreign_canonical")]
    [InlineData("corrupt_canonical")]
    [InlineData("new_create_failure")]
    [InlineData("new_ambiguous")]
    [InlineData("new_ambiguous_foreign")]
    [InlineData("new_ambiguous_corrupt")]
    [InlineData("new_ambiguous_absent")]
    [InlineData("new_foreign")]
    [InlineData("new_corrupt")]
    [InlineData("normal")]
    [InlineData("quarantine_failure")]
    public async Task MarkerRotation_DurableBoundaryMatrix(string fault)
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var journal = CreateProductionJournalStore();
        SwitchTransitionMarkerEntry? interrupted = null;
        SwitchJournalEntry? foreign = null;
        string? foreignMarkerBytes = null;
        int transitions = 0;
        bool restored = false, faultReached = false;
        journal.AfterOwnedDeleteHookAsync = async () =>
        {
            if (++transitions == 2)
            {
                interrupted = (await journal.ReadTransitionMarkerAsync()).Marker!;
                Assert.False(File.Exists(journal.JournalFilePath));
                Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, interrupted.Expected.State);
                throw new IOException("Synthetic PRECOMMIT publication failure.");
            }
            if (fault == "quarantine_failure" && transitions == 4)
            {
                faultReached = true;
                Assert.Equal(SwitchJournalState.QUARANTINED, (await journal.ReadTransitionMarkerAsync()).Marker!.Next.State);
                throw new IOException("Synthetic quarantine publication failure.");
            }
        };
        journal.BeforeCreatePublishHookAsync = async () =>
        {
            if (interrupted == null) return;
            Assert.False(File.Exists(journal.JournalFilePath));
            Assert.True(SwitchTransitionMarkerStore.MarkersMatch(interrupted, (await journal.ReadTransitionMarkerAsync()).Marker!));
            if (fault == "before_restore")
            {
                faultReached = true;
                throw new IOException("Synthetic bridge publication failure.");
            }
            if (fault == "foreign_canonical")
            {
                faultReached = true;
                foreign = FrozenEntry(Guid.NewGuid().ToString("D"), SwitchJournalState.QUARANTINED,
                    _source!.Id, _target!.Id, SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
                await WriteCanonicalAsync(foreign);
            }
            if (fault == "corrupt_canonical")
            {
                faultReached = true;
                await File.WriteAllTextAsync(journal.JournalFilePath, "{ synthetic corrupt canonical");
            }
        };
        journal.AfterPublishHookAsync = async () =>
        {
            if (interrupted == null) return;
            var canonical = (await journal.ReadAsync()).Entry;
            if (canonical == null || !SwitchJournalStore.EntriesMatch(canonical, interrupted.Expected)) return;
            restored = true;
            Assert.True(File.Exists(MarkerPath));
            if (fault == "restore_ambiguous")
            {
                faultReached = true;
                throw new IOException("Synthetic bridge publish landed before error.");
            }
        };
        journal.MarkerStore.BeforeDispositionHookAsync = async () =>
        {
            if (interrupted == null) return;
            var marker = (await ReadMarkerDuringDispositionAsync()).Marker!;
            if (marker.Next.State != SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT) return;
            Assert.True(SwitchJournalStore.EntriesMatch((await journal.ReadAsync()).Entry!, interrupted.Expected));
            if (fault == "after_restore")
            {
                faultReached = true;
                throw new IOException("Synthetic old marker delete failure after bridge restoration.");
            }
        };
        journal.MarkerStore.BeforePublishHookAsync = async () =>
        {
            if (interrupted == null || File.Exists(MarkerPath)) return;
            var canonical = (await journal.ReadAsync()).Entry!;
            if (canonical.State != SwitchJournalState.CREDENTIAL_APPLYING) return;
            Assert.True(SwitchJournalStore.EntriesMatch(canonical, interrupted.Expected));
            if (fault == "new_create_failure")
            {
                faultReached = true;
                throw new IOException("Synthetic replacement marker publication failure.");
            }
            if (fault == "new_foreign")
            {
                faultReached = true;
                var tx = Guid.NewGuid().ToString("D");
                await WriteFrozenMarkerAsync(FrozenEntry(tx, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
                        SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED),
                    FrozenEntry(tx, SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT, _source.Id, _target.Id,
                        SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
                foreignMarkerBytes = await File.ReadAllTextAsync(MarkerPath);
            }
            if (fault == "new_corrupt")
            {
                faultReached = true;
                foreignMarkerBytes = "{ synthetic corrupt replacement marker";
                await WriteRawMarkerAsync(foreignMarkerBytes);
            }
        };
        journal.MarkerStore.AfterPublishHookAsync = async () =>
        {
            if (interrupted == null || !fault.StartsWith("new_ambiguous", StringComparison.Ordinal)) return;
            var published = (await journal.ReadTransitionMarkerAsync()).Marker!;
            Assert.Equal(SwitchJournalState.ROLLING_BACK, published.Next.State);
            faultReached = true;
            if (fault == "new_ambiguous_foreign")
            {
                foreignMarkerBytes = JsonSerializer.Serialize(published with { CreatedAt = published.CreatedAt.AddSeconds(1) },
                    SwitchTransitionMarkerStore.SerializerOptions);
                await WriteRawMarkerAsync(foreignMarkerBytes);
            }
            if (fault == "new_ambiguous_corrupt")
            {
                foreignMarkerBytes = "{ synthetic ambiguous corrupt marker";
                await WriteRawMarkerAsync(foreignMarkerBytes);
            }
            if (fault == "new_ambiguous_absent") File.Delete(MarkerPath);
            throw new IOException("Synthetic replacement marker publish landed before error.");
        };
        if (fault == "quarantine_failure")
            _process.RestoreError = new IOException("Synthetic source restoration failure.");

        var coordinator = CreateCoordinatorWithStore(journal, quotaStore: quota, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.NotNull(interrupted);
        Assert.True(faultReached || fault == "normal");
        bool completed = fault is "normal" or "restore_ambiguous" or "new_ambiguous";
        if (completed)
        {
            Assert.True(restored);
            Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
            Assert.Equal(1, _process.RestoreCount);
            Assert.False(File.Exists(journal.JournalFilePath));
            Assert.False(File.Exists(MarkerPath));
            return;
        }

        Assert.True(File.Exists(journal.JournalFilePath) || File.Exists(MarkerPath));
        Assert.False(coordinator.CanAdmitSwitch(out _));
        if (fault == "before_restore")
        {
            Assert.False(File.Exists(journal.JournalFilePath));
            Assert.True(SwitchTransitionMarkerStore.MarkersMatch(interrupted, (await journal.ReadTransitionMarkerAsync()).Marker!));
        }
        if (fault == "after_restore")
        {
            Assert.True(SwitchJournalStore.EntriesMatch(interrupted.Expected, (await journal.ReadAsync()).Entry!));
            Assert.True(SwitchTransitionMarkerStore.MarkersMatch(interrupted, (await journal.ReadTransitionMarkerAsync()).Marker!));
        }
        if (fault == "new_create_failure")
        {
            Assert.True(SwitchJournalStore.EntriesMatch(interrupted.Expected, (await journal.ReadAsync()).Entry!));
            Assert.False(File.Exists(MarkerPath));
        }
        if (foreign != null) Assert.True(SwitchJournalStore.EntriesMatch(foreign, (await journal.ReadAsync()).Entry!));
        if (fault == "corrupt_canonical") Assert.Equal("{ synthetic corrupt canonical", await File.ReadAllTextAsync(journal.JournalFilePath));
        if (foreignMarkerBytes != null) Assert.Equal(foreignMarkerBytes, await File.ReadAllTextAsync(MarkerPath));
        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: quota, clock: clock);
        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, (await fresh.ReconcileStartupJournalAsync()).Status);
        Assert.False(fresh.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task MarkerRotation_CanonicalBridgeCannotDisappearWhileMarkerIsAbsent()
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var journal = CreateProductionJournalStore();
        int transitions = 0;
        journal.AfterOwnedDeleteHookAsync = () => ++transitions == 2
            ? Task.FromException(new IOException("Synthetic PRECOMMIT publication failure.")) : Task.CompletedTask;
        var coordinator = CreateCoordinatorWithStore(journal, quotaStore: quota, clock: clock);
        bool frozen = false;
        coordinator.AfterOwnedMarkerRotationDeleteAsync = () =>
        {
            frozen = true;
            Assert.False(File.Exists(MarkerPath));
            Assert.Throws<IOException>(() => File.Delete(journal.JournalFilePath));
            Assert.Throws<IOException>(() => File.Move(journal.JournalFilePath, journal.JournalFilePath + ".foreign"));
            return Task.FromException(new IOException("Synthetic process death after guarded rotation."));
        };
        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.True(frozen);
        Assert.True(File.Exists(journal.JournalFilePath));
    }

    [Fact]
    public async Task MarkerCleanup_ThrowAfterSuccessorPersistence_RetainsOwnedEvidenceAndBlocksReuse()
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var journal = CreateProductionJournalStore();
        bool failureReached = false;
        journal.MarkerStore.BeforeDispositionHookAsync = async () =>
        {
            if ((await ReadMarkerDuringDispositionAsync()).Marker!.Next.State != SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT) return;
            failureReached = true;
            throw new IOException("Synthetic marker cleanup exception.");
        };
        var coordinator = CreateCoordinatorWithStore(journal, quotaStore: quota, clock: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.False(result.Success);
        Assert.True(failureReached);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal(SwitchTransitionMarkerReadStatus.Valid, (await journal.ReadTransitionMarkerAsync()).Status);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: quota, clock: clock);
        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, (await fresh.ReconcileStartupJournalAsync()).Status);
    }

    [Fact]
    public async Task MarkerRotation_PostDeleteCrash_RetainsExactApplyingPredecessor()
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var journal = CreateProductionJournalStore();
        SwitchTransitionMarkerEntry? interrupted = null;
        int transitions = 0;
        journal.AfterOwnedDeleteHookAsync = async () =>
        {
            if (++transitions != 2) return;
            interrupted = (await journal.ReadTransitionMarkerAsync()).Marker;
            Assert.NotNull(interrupted);
            Assert.False(File.Exists(journal.JournalFilePath));
            throw new IOException("Synthetic successor publication failure.");
        };
        var coordinator = CreateCoordinatorWithStore(journal, quotaStore: quota, clock: clock);
        bool frozen = false;
        coordinator.AfterOwnedMarkerRotationDeleteAsync = async () =>
        {
            frozen = true;
            Assert.Equal(SwitchTransitionMarkerReadStatus.Absent, (await journal.ReadTransitionMarkerAsync()).Status);
            var canonical = await journal.ReadAsync();
            Assert.Equal(SwitchJournalReadStatus.Valid, canonical.Status);
            Assert.True(SwitchJournalStore.EntriesMatch(interrupted!.Expected, canonical.Entry!));
            throw new IOException("Synthetic process death before replacement marker.");
        };

        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.True(frozen);
        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: quota, clock: clock);
        var startup = await fresh.ReconcileStartupJournalAsync();
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, startup.Status);
        Assert.Equal(SwitchJournalState.CREDENTIAL_APPLYING, startup.RetainedEntry!.State);
        Assert.Equal(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, startup.RetainedEntry.TargetActivationProvenance);
        Assert.False(fresh.CanAdmitSwitch(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkerRotation_ReplacementWithSameExpected_IsNeverAdopted(bool changeTimestampOnly)
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var journal = CreateProductionJournalStore();
        string? replacementBytes = null;
        int transitions = 0;
        journal.AfterOwnedDeleteHookAsync = async () =>
        {
            if (++transitions != 2) return;
            var owned = (await journal.ReadTransitionMarkerAsync()).Marker!;
            var replacement = changeTimestampOnly
                ? owned with { CreatedAt = owned.CreatedAt.AddSeconds(1) }
                : owned with { Next = owned.Next with { State = SwitchJournalState.ROLLING_BACK } };
            replacementBytes = JsonSerializer.Serialize(replacement, SwitchTransitionMarkerStore.SerializerOptions);
            await File.WriteAllTextAsync(MarkerPath, replacementBytes);
            throw new IOException("Synthetic foreign marker interference.");
        };
        var coordinator = CreateCoordinatorWithStore(journal, quotaStore: quota, clock: clock);
        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.NotNull(replacementBytes);
        Assert.True(File.Exists(MarkerPath));
        Assert.Equal(replacementBytes, await File.ReadAllTextAsync(MarkerPath));
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task MarkerRotation_NoPreviouslyOwnedMarker_NeverRestoresOrAdoptsOccupant()
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var journal = CreateProductionJournalStore();
        string? bytes = null;
        int creates = 0;
        journal.BeforeCreatePublishHookAsync = () => { creates++; return Task.CompletedTask; };
        journal.MarkerStore.BeforePublishHookAsync = async () =>
        {
            if (bytes != null) return;
            var expected = (await journal.ReadAsync()).Entry!;
            await WriteFrozenMarkerAsync(expected, expected with { State = SwitchJournalState.ROLLING_BACK });
            bytes = await File.ReadAllTextAsync(MarkerPath);
        };
        var coordinator = CreateCoordinatorWithStore(journal, quotaStore: quota, clock: clock);
        await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.NotNull(bytes);
        Assert.Equal(bytes, await File.ReadAllTextAsync(MarkerPath));
        Assert.Equal(1, creates);
        Assert.DoesNotContain("WINCRED_WRITE:antigravity", _globalTimeline);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkerRecovery_ReplacementDuringStaleCleanup_SurvivesAndBlocks(bool resolution)
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        await _accounts.SetActiveAccountIdAsync(_target!.Id);
        var tx = Guid.NewGuid().ToString("D");
        var applying = FrozenEntry(tx, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        var next = await WriteCanonicalAsync(applying with { State = SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT });
        await WriteFrozenMarkerAsync(applying, next);
        var journal = CreateProductionJournalStore();
        string? foreignBytes = null;
        journal.MarkerStore.BeforeDispositionHookAsync = async () =>
        {
            File.Move(MarkerPath, MarkerPath + ".superseded");
            var otherTx = Guid.NewGuid().ToString("D");
            await WriteFrozenMarkerAsync(applying with { TransactionId = otherTx }, next with { TransactionId = otherTx });
            foreignBytes = await File.ReadAllTextAsync(MarkerPath);
        };
        var coordinator = CreateCoordinatorWithStore(journal, quotaStore: quota, clock: clock);
        if (resolution)
            Assert.Equal(JournalResolutionStatus.ProofFailed, (await coordinator.ResolveQuarantinedJournalAsync()).Status);
        else
            Assert.NotEqual(StartupJournalReconciliationStatus.Clean, (await coordinator.ReconcileStartupJournalAsync()).Status);
        Assert.NotNull(foreignBytes);
        Assert.Equal(foreignBytes, await File.ReadAllTextAsync(MarkerPath));
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task MarkerResolution_CanonicalDisappearsDuringProof_RetainsMarkerAndRecovery()
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var journal = CreateProductionJournalStore();
        var tx = Guid.NewGuid().ToString("D");
        var recorded = await WriteCanonicalAsync(FrozenEntry(tx, SwitchJournalState.RECORDED, _source!.Id, _target!.Id));
        await WriteFrozenMarkerAsync(recorded, FrozenEntry(tx, SwitchJournalState.CREDENTIAL_APPLYING,
            _source.Id, _target.Id, SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        string bytes = await File.ReadAllTextAsync(MarkerPath);
        var adapter = new InterferingAdapter(_adapter, onFirstIdentityProbe: () => File.Delete(journal.JournalFilePath));
        var coordinator = new NativeAccountSwitchCoordinator(_accounts, _vault, _credentials, _credentials,
            adapter, _process, journal, verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5), quotaObservationStore: quota, timeProvider: clock);

        var result = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.NotEqual(JournalResolutionStatus.NoJournal, result.Status);
        Assert.NotEqual(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.Equal(bytes, await File.ReadAllTextAsync(MarkerPath));
        var retry = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, retry.Status);
        Assert.False(File.Exists(MarkerPath));
    }

    [Theory]
    [InlineData("valid", JournalResolutionStatus.ProofFailed, JournalRecoveryStates.ActionRequired)]
    [InlineData("corrupt", JournalResolutionStatus.NotResolvable, JournalRecoveryStates.NotResolvable)]
    [InlineData("unsupported", JournalResolutionStatus.NotResolvable, JournalRecoveryStates.NotResolvable)]
    [InlineData("io", JournalResolutionStatus.PersistenceFailure, JournalRecoveryStates.Unknown)]
    [InlineData("absent", JournalResolutionStatus.ProofFailed, JournalRecoveryStates.ActionRequired)]
    public async Task MarkerResolution_UnexpectedCanonicalAbsence_ClassifiesBothPaths(
        string remaining, JournalResolutionStatus expected, string recovery)
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var tx = Guid.NewGuid().ToString("D");
        var recorded = await WriteCanonicalAsync(FrozenEntry(tx, SwitchJournalState.RECORDED, _source!.Id, _target!.Id));
        await WriteFrozenMarkerAsync(recorded, FrozenEntry(tx, SwitchJournalState.CREDENTIAL_APPLYING,
            _source.Id, _target.Id, SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        FileStream? exclusiveMarker = null;
        var adapter = new InterferingAdapter(_adapter, onFirstIdentityProbe: () =>
        {
            File.Delete(_journal.JournalFilePath);
            if (remaining == "corrupt") File.WriteAllText(MarkerPath, "{ synthetic corrupt marker");
            if (remaining == "unsupported") File.WriteAllText(MarkerPath, "{\"magic\":\"AG2SWITCHTRNS\",\"schemaVersion\":99}");
            if (remaining == "absent") File.Delete(MarkerPath);
            if (remaining == "io") exclusiveMarker = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None);
        });
        var coordinator = new NativeAccountSwitchCoordinator(_accounts, _vault, _credentials, _credentials,
            adapter, _process, CreateProductionJournalStore(), verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5), quotaObservationStore: quota, timeProvider: clock);
        try
        {
            var result = await coordinator.ResolveQuarantinedJournalAsync();
            Assert.Equal(expected, result.Status);
            Assert.Equal(recovery, coordinator.GetStatus().JournalRecoveryState);
            Assert.False(coordinator.CanAdmitSwitch(out _));
        }
        finally { exclusiveMarker?.Dispose(); }
        Assert.Equal(remaining != "absent", File.Exists(MarkerPath));
    }

    [Theory]
    [InlineData(SwitchJournalState.CREDENTIAL_APPLYING)]
    [InlineData(SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT)]
    public async Task MarkerResolution_SeriousCanonicalDisappears_DoesNotClearRemainingMarker(SwitchJournalState state)
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var tx = Guid.NewGuid().ToString("D");
        var predecessor = await WriteCanonicalAsync(FrozenEntry(tx, state, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED));
        await WriteFrozenMarkerAsync(predecessor, predecessor with { State = SwitchJournalState.ROLLING_BACK });
        var adapter = new InterferingAdapter(_adapter, onFirstIdentityProbe: () => File.Delete(_journal.JournalFilePath));
        var coordinator = new NativeAccountSwitchCoordinator(_accounts, _vault, _credentials, _credentials,
            adapter, _process, CreateProductionJournalStore(), quotaObservationStore: quota, timeProvider: clock);
        Assert.Equal(JournalResolutionStatus.ProofFailed, (await coordinator.ResolveQuarantinedJournalAsync()).Status);
        Assert.True(File.Exists(MarkerPath));
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkerResolution_MarkerOnlyDisappears_DoesNotIgnoreNewCanonical(bool targetAttempted)
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var tx = Guid.NewGuid().ToString("D");
        var predecessor = FrozenEntry(tx,
            targetAttempted ? SwitchJournalState.CREDENTIAL_APPLYING : SwitchJournalState.RECORDED,
            _source!.Id, _target!.Id, targetAttempted
                ? SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED : SwitchTargetActivationProvenance.NOT_ATTEMPTED);
        await WriteFrozenMarkerAsync(predecessor, predecessor with
        {
            State = targetAttempted ? SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT : SwitchJournalState.CREDENTIAL_APPLYING,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        });
        var foreign = FrozenEntry(Guid.NewGuid().ToString("D"), SwitchJournalState.QUARANTINED,
            _source.Id, _target.Id, SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        var adapter = new InterferingAdapter(_adapter, onFirstIdentityProbe: () =>
        {
            File.Delete(MarkerPath);
            WriteCanonicalAsync(foreign).GetAwaiter().GetResult();
        });
        var coordinator = new NativeAccountSwitchCoordinator(_accounts, _vault, _credentials, _credentials,
            adapter, _process, CreateProductionJournalStore(), quotaObservationStore: quota, timeProvider: clock);
        Assert.Equal(JournalResolutionStatus.ProofFailed, (await coordinator.ResolveQuarantinedJournalAsync()).Status);
        Assert.True(SwitchJournalStore.EntriesMatch(foreign, (await CreateProductionJournalStore().ReadAsync()).Entry!));
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("not_attempted")]
    [InlineData("unknown")]
    public async Task MarkerSemantics_ContradictoryEntries_FailClosedBeforeInvalidation(string contradiction)
    {
        var (quota, clock) = await SeedDurableRoutingAsync();
        var tx = Guid.NewGuid().ToString("D");
        var applying = FrozenEntry(tx, SwitchJournalState.CREDENTIAL_APPLYING, _source!.Id, _target!.Id,
            SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED);
        var next = applying with { State = SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT };
        next = contradiction switch
        {
            "source" => next with { SourceAccountId = "other-source" },
            "target" => next with { TargetAccountId = "other-target" },
            "not_attempted" => next with { TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED },
            _ => next with { TargetActivationProvenance = SwitchTargetActivationProvenance.UNKNOWN }
        };
        var marker = new SwitchTransitionMarkerEntry { TransactionId = tx, CreatedAt = clock.Now, Expected = applying, Next = next };
        string bytes = JsonSerializer.Serialize(marker, SwitchTransitionMarkerStore.SerializerOptions);
        await WriteRawMarkerAsync(bytes);
        Assert.Equal(SwitchTransitionMarkerReadStatus.Corrupt, SwitchTransitionMarkerStore.ParseMarkerFromText(bytes).Status);
        var before = await quota.GetObservationAsync(_target.Id, EvidenceModel);
        var coordinator = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: quota, clock: clock);
        Assert.Equal(JournalResolutionStatus.NotResolvable, (await coordinator.ResolveQuarantinedJournalAsync()).Status);
        Assert.Equal(before, await quota.GetObservationAsync(_target.Id, EvidenceModel));
        Assert.Equal(bytes, await File.ReadAllTextAsync(MarkerPath));
        Assert.False(coordinator.CanAdmitSwitch(out _));
        var fresh = CreateCoordinatorWithStore(CreateProductionJournalStore(), quotaStore: quota, clock: clock);
        Assert.Equal(StartupJournalReconciliationStatus.Quarantined, (await fresh.ReconcileStartupJournalAsync()).Status);
        Assert.Equal(JournalRecoveryStates.NotResolvable, fresh.GetStatus().JournalRecoveryState);
        Assert.True((await fresh.SwitchAsync(_target.Id)).ManualRecoveryRequired);
        Assert.Equal(bytes, await File.ReadAllTextAsync(MarkerPath));
    }
}
