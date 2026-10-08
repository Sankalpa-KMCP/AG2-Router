using System.IO;
using System.Text;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Switching;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Decisive regressions for the PROMPT #026 HIGH findings. F1: deleting the proved entry is
/// not proof of path clearance — a journal written at the canonical path during the
/// comparison/disposition window must survive and keep recovery blocked. F2: a switch
/// transaction deletes only the exact journal entry it successfully wrote; before its first
/// journal write it performs no cleanup at all, so an unrelated journal always survives.
/// Interference uses the production journal store's own comparison/disposition seam.
/// </summary>
public sealed partial class TargetQuotaVerificationSwitchTests
{
    private NativeAccountSwitchCoordinator CreateCoordinatorWithStore(
        ISwitchJournalStore journalStore, IAccountStore? accountStore = null,
        IQuotaObservationStore? quotaStore = null, EvidenceTestClock? clock = null) =>
        new(
            accountStore ?? _accounts,
            _vault,
            _credentials,
            _credentials,
            _adapter,
            _process,
            journalStore,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: quotaStore,
            timeProvider: clock);

    [Fact]
    public async Task JournalOwnership_01_PostComparisonQuarantinedReplacement_DuringRecordedCleanup_IsNotCleanAndBlocksAdmission()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var replacement = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };
        var replacementStore = new SwitchJournalStore(_journal.JournalFilePath);
        var hookedStore = new SwitchJournalStore(_journal.JournalFilePath);
        hookedStore.BeforeDispositionHookAsync = async () =>
        {
            // Move the compared entry away and occupy the canonical path with a different,
            // valid transaction: the opened handle still deletes the ORIGINAL file object.
            File.Move(_journal.JournalFilePath, _journal.JournalFilePath + ".superseded");
            await replacementStore.WriteEntryAsync(replacement).ConfigureAwait(false);
        };

        await hookedStore.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        });

        var coordinator = CreateCoordinatorWithStore(hookedStore, quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        // The proved RECORDED entry was deleted, but the replacement occupies the canonical
        // path: reconciliation must NOT be clean and admission must stay blocked.
        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        var blocked = await coordinator.SwitchAsync(_target!.Id);
        Assert.True(blocked.ManualRecoveryRequired);

        var current = await replacementStore.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, current.Status);
        Assert.Equal(replacement.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(SwitchJournalState.QUARANTINED, current.Entry.State);

        // A subsequent fresh reconciliation handles the replacement: no permanent blocking.
        _adapter.Identity = new AccountIdentityDto(_source!.Email);
        var resolutionCoordinator = CreateCoordinatorWithStore(replacementStore, quotaStore: store, clock: clock);
        var resolution = await resolutionCoordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolution.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task JournalOwnership_02_PostComparisonReplacementDuringPrecommitCleanup_BlocksRecovery()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await _accounts.SetActiveAccountIdAsync(_target!.Id);
        _adapter.Identity = new AccountIdentityDto(_target!.Email);

        var replacement = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };
        var replacementStore = new SwitchJournalStore(_journal.JournalFilePath);
        var hookedStore = new SwitchJournalStore(_journal.JournalFilePath);
        hookedStore.BeforeDispositionHookAsync = async () =>
        {
            File.Move(_journal.JournalFilePath, _journal.JournalFilePath + ".superseded");
            await replacementStore.WriteEntryAsync(replacement).ConfigureAwait(false);
        };

        await hookedStore.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        });

        var coordinator = CreateCoordinatorWithStore(hookedStore, quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        var current = await replacementStore.ReadAsync();
        Assert.Equal(replacement.TransactionId, current.Entry!.TransactionId);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task JournalOwnership_03_PostComparisonCorruptReplacement_DuringStartupCleanup_FailsClosedAndIsPreserved()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        const string corruptContent = "{ corrupt replacement bytes";
        var hookedStore = new SwitchJournalStore(_journal.JournalFilePath);
        hookedStore.BeforeDispositionHookAsync = () =>
        {
            File.Move(_journal.JournalFilePath, _journal.JournalFilePath + ".superseded");
            return File.WriteAllTextAsync(_journal.JournalFilePath, corruptContent);
        };

        await hookedStore.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        });

        var coordinator = CreateCoordinatorWithStore(hookedStore, quotaStore: store, clock: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(corruptContent, await File.ReadAllTextAsync(_journal.JournalFilePath));
    }

    [Fact]
    public async Task JournalOwnership_04_PreJournalPlanRejection_LeavesForeignJournalUntouchedAndBlocks()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };

        // The foreign journal appears during awaited plan work, after this transaction's
        // admission proof passed and before the transaction writes its own journal.
        int planCalls = 0;
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id,
            () =>
            {
                if (Interlocked.Increment(ref planCalls) == 2)
                {
                    _journal.WriteEntryAsync(foreign).GetAwaiter().GetResult();
                    return false;
                }

                return true;
            },
            EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.Cancelled, result.Code);

        // No journal cleanup ran: the transaction never wrote a journal, and the unrelated
        // entry survives byte-for-byte as its own recovery problem.
        var current = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, current.Status);
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);

        // Recovery is blocking; admission cannot rely on the pre-transaction NONE state.
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task JournalOwnership_05_PreJournalCancellation_ForeignJournalSurvives_AndNextAdmissionIsBlocked()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };
        await _journal.WriteEntryAsync(foreign);

        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var cancelled = await coordinator.SwitchAsync(_target!.Id, cancelledCts.Token);

        Assert.False(cancelled.Success);
        Assert.Equal(SwitchResultCodes.Cancelled, cancelled.Code);
        var current = await _journal.ReadAsync();
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);
        Assert.True(File.Exists(_journal.JournalFilePath));

        // The next admission revalidates the canonical path and fails closed instead of
        // trusting the stale in-memory recovery state.
        var next = await coordinator.SwitchAsync(_target.Id);
        Assert.False(next.Success);
        Assert.True(next.ManualRecoveryRequired);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
    }

    [Fact]
    public async Task JournalOwnership_06_PreJournalTargetException_ForeignJournalUntouched()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        };

        // The foreign journal lands while the transaction resolves the target account,
        // before the transaction writes any journal of its own.
        var interferingAccounts = new AccountProbeInterferenceStore(_accounts,
            onFirstGetAccount: () => _journal.WriteEntryAsync(foreign).GetAwaiter().GetResult());
        var coordinator = CreateCoordinatorWithStore(_journal, accountStore: interferingAccounts, quotaStore: store, clock: clock);

        var result = await coordinator.SwitchAsync("missing-account-id");

        Assert.False(result.Success);
        var current = await _journal.ReadAsync();
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task JournalOwnership_07_SuccessCommitCleanup_WithReplacement_KeepsSuccessAndBlocksRecovery()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var replacement = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };
        var interferingAccounts = new FinalizeInterferenceStore(_accounts,
            onFinalize: () => _journal.WriteEntryAsync(replacement).GetAwaiter().GetResult());
        var coordinator = CreateCoordinatorWithStore(_journal, accountStore: interferingAccounts, quotaStore: store, clock: clock);

        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // Business success stands; cleanup/recovery is explicitly uncertain.
        Assert.True(result.Success);
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.Contains("JOURNAL_CLEANUP_REQUIRES_RECONCILIATION", result.StagesCompleted);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));

        var current = await _journal.ReadAsync();
        Assert.Equal(replacement.TransactionId, current.Entry!.TransactionId);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task JournalOwnership_08_VerifiedRollbackCleanup_WithReplacement_BlocksRecovery()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var replacement = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };
        var originalOnRestore = _process.OnRestore;
        _process.OnRestore = () =>
        {
            originalOnRestore?.Invoke();
            _journal.WriteEntryAsync(replacement).GetAwaiter().GetResult();
        };

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        _process.OnReplacement = () => { _adapter.Identity = null; };
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // Verified rollback remains the business outcome; the surviving replacement keeps
        // recovery blocking instead of an ordinary rollback-success clean state.
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Contains("Journal cleanup requires reconciliation", result.Message);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        var current = await _journal.ReadAsync();
        Assert.Equal(replacement.TransactionId, current.Entry!.TransactionId);
    }

    [Fact]
    public async Task JournalOwnership_09_AmbiguousPublishWithProvenNextEntry_RecoversOwnership()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        // The atomic publish lands, THEN the outcome is reported as failure: the ownership
        // rule classifies by reading the canonical path back instead of guessing.
        var ambiguousStore = new SwitchJournalStore(_journal.JournalFilePath);
        ambiguousStore.AfterPublishHookAsync = () =>
            Task.FromException(new IOException("Ambiguous journal publish: landed but reported as failure"));
        var coordinator = CreateCoordinatorWithStore(ambiguousStore, quotaStore: store, clock: clock);

        var result = await coordinator.SwitchAsync(_target!.Id);

        // The proven next entry is on the canonical path, so ownership is recovered and the
        // transaction completes normally rather than fabricating a failure.
        Assert.True(result.Success);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
    }

    [Fact]
    public async Task JournalOwnership_09b_AmbiguousPublishWithUnprovableOutcome_FailsClosedWithoutCleanup()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        // The publish is reported ambiguous AND the canonical path cannot be proven to hold
        // the next entry (hook moves it away): ownership must not be guessed or advanced.
        var ambiguousStore = new SwitchJournalStore(_journal.JournalFilePath);
        ambiguousStore.AfterPublishHookAsync = async () =>
        {
            await Task.Yield();
            File.Move(_journal.JournalFilePath, _journal.JournalFilePath + ".orphaned");
        };
        var coordinator = CreateCoordinatorWithStore(ambiguousStore, quotaStore: store, clock: clock);

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.NotEqual(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
        // The journal evidence was not destroyed by a cleanup that ran without ownership.
        Assert.True(File.Exists(_journal.JournalFilePath + ".orphaned"));
    }

    [Fact]
    public async Task JournalOwnership_10_NormalSuccessAndRollbackCleanup_StillSucceedAndRestoreAdmission()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var successCoordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            quotaObservationStore: store,
            timeProvider: clock);
        var success = await successCoordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);
        Assert.True(success.Success);
        Assert.DoesNotContain("JOURNAL_CLEANUP_REQUIRES_RECONCILIATION", success.StagesCompleted);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.None, successCoordinator.GetStatus().JournalRecoveryState);
        Assert.True(successCoordinator.CanAdmitSwitch(out _));

        // Reset to source and verify the rollback path also cleans its owned entry normally.
        await _accounts.SetActiveAccountIdAsync(_source!.Id);
        _adapter.Identity = new AccountIdentityDto(_source!.Email);
        _credentials.Set(new WinCredEntry("gemini:antigravity", 1, _source!.Email, 2,
            Encoding.UTF8.GetBytes("source-secret-payload")));
        _process.OnReplacement = () => { _adapter.Identity = null; };
        var rollbackCoordinator = CreateCoordinator(
            verificationTimeout: TimeSpan.FromMilliseconds(50),
            quotaObservationStore: store,
            timeProvider: clock);
        var rolledBack = await rollbackCoordinator.SwitchAutomaticallyAsync(_target.Id, _source.Id, () => true, EvidenceModel, 30.0);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, rolledBack.Code);
        Assert.DoesNotContain("Journal cleanup requires reconciliation", rolledBack.Message);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.None, rollbackCoordinator.GetStatus().JournalRecoveryState);
    }

    // =========================================================================
    // PROMPT #028 R1: write-side ownership (continuing-preflight + transitions)
    // =========================================================================

    [Fact]
    public async Task JournalOwnership_11_ContinuingPreflightForeignJournal_IsNotOverwrittenAndNoDestructiveSideEffects()
    {
        // The exact PROMPT #028 R1 reproduction: admission proves the path absent, awaited
        // plan work writes a foreign QUARANTINED journal, the callback returns TRUE, and the
        // transaction's first RECORDED create must fail its ownership check.
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };

        int planCalls = 0;
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id,
            () =>
            {
                if (Interlocked.Increment(ref planCalls) == 2)
                {
                    _journal.WriteEntryAsync(foreign).GetAwaiter().GetResult();
                }

                return true;
            },
            EvidenceModel, 30.0);

        // The create failed its ownership check: recovery-blocked result, no destructive
        // switch side effects of any kind.
        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.DoesNotContain("PROCESS_STOP", _globalTimeline);
        Assert.DoesNotContain(_globalTimeline, t => t.StartsWith("WINCRED_WRITE:", StringComparison.Ordinal));
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(0, _process.LaunchCount);

        // The foreign journal survives byte-for-byte and blocks admission.
        var current = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, current.Status);
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(SwitchJournalState.QUARANTINED, current.Entry.State);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task JournalOwnership_12_ForeignReplacementBeforeCredentialApplying_BlocksForwardMutation()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };

        // The source process stop has already happened when the foreign journal appears;
        // the CREDENTIAL_APPLYING transition then fails its ownership check.
        var originalAfterStopIssued = _process.AfterStopIssued;
        _process.AfterStopIssued = () =>
        {
            originalAfterStopIssued?.Invoke();
            _journal.WriteEntryAsync(foreign).GetAwaiter().GetResult();
            return Task.CompletedTask;
        };

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // The forward mutation is blocked: no target credential write, no target launch.
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.DoesNotContain("WINCRED_WRITE:antigravity", _globalTimeline);
        Assert.Equal(0, _process.LaunchCount);

        // The source runtime was still restored (compensation remains mandatory) and the
        // foreign journal was never overwritten.
        Assert.Equal(1, _process.RestoreCount);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        var current = await _journal.ReadAsync();
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task JournalOwnership_13_ForeignReplacementBeforePrecommit_CompensatesAndPreservesForeign()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };

        // The target credential was already applied and the target verified when the
        // foreign journal appears; the PRECOMMIT transition then loses ownership.
        var originalOnReplacement = _process.OnReplacement;
        _process.OnReplacement = () =>
        {
            originalOnReplacement?.Invoke();
            _journal.WriteEntryAsync(foreign).GetAwaiter().GetResult();
        };

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // Business progression stops (no metadata commit) and the verified rollback runs:
        // target evidence invalidated, source credential and process restored.
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(1, _process.RestoreCount);
        Assert.Contains("TARGET_QUOTA_EVIDENCE_INVALIDATED", result.StagesCompleted);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Contains("WINCRED_WRITE:antigravity", _globalTimeline);
        Assert.Equal(_source!.Email, _credentials.Current!.UserName);

        // The foreign journal was never overwritten and blocks recovery.
        var current = await _journal.ReadAsync();
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task JournalOwnership_14_ForeignReplacementBeforeRollingBack_CompensatesWithoutJournaling()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };

        // Target activation times out AND the foreign journal appears during the same
        // replacement callback; the ROLLING_BACK transition then loses ownership.
        _process.OnReplacement = () =>
        {
            _adapter.Identity = null;
            _journal.WriteEntryAsync(foreign).GetAwaiter().GetResult();
        };

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        // Compensation still ran to completion (safe source restoration) even though the
        // rollback state could not be journaled; the foreign journal was never overwritten.
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(1, _process.RestoreCount);
        Assert.Contains("JOURNAL_OWNERSHIP_LOST", result.StagesCompleted);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        var current = await _journal.ReadAsync();
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task JournalOwnership_15_ForeignReplacementBeforeQuarantined_ForeignPreservedAndBlocked()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var foreign = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };

        // The target activation times out, the foreign journal appears during that callback,
        // and the rollback itself then fails (source restore fault): the QUARANTINED
        // transition must not overwrite the foreign journal.
        _process.RestoreError = new IOException("Synthetic source restore failure");
        _process.OnReplacement = () =>
        {
            _adapter.Identity = null;
            _journal.WriteEntryAsync(foreign).GetAwaiter().GetResult();
        };

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30.0);

        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.True(coordinator.GetStatus().QuarantineActive);
        var current = await _journal.ReadAsync();
        Assert.Equal(foreign.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(SwitchJournalState.ROLLING_BACK, current.Entry!.State);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    /// <summary>Intercepts the first target-account lookup to run an action before the
    /// lookup result is returned, modeling interference during preflight work.</summary>
    private sealed class AccountProbeInterferenceStore(IAccountStore inner, Action onFirstGetAccount) : IAccountStore
    {
        private int _getAccountCalls;

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
            inner.ListAccountsAsync(cancellationToken);

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _getAccountCalls, 1, 0) == 0)
            {
                onFirstGetAccount();
            }

            return inner.GetAccountAsync(id, cancellationToken);
        }

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            inner.GetAccountByEmailAsync(email, cancellationToken);

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            inner.GetActiveAccountIdAsync(cancellationToken);

        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) =>
            inner.SetActiveAccountIdAsync(id, cancellationToken);

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) =>
            inner.AddAccountAsync(input, cancellationToken);

        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default) =>
            inner.UpdateAccountAsync(id, updates, cancellationToken);

        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.RemoveAccountAsync(id, cancellationToken);

        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default) =>
            inner.RemoveAccountIfUnchangedAsync(expected, cancellationToken);

        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default) =>
            inner.CompareExchangeActiveAccountIdAsync(expectedId, newId, cancellationToken);

        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default) =>
            inner.TryFinalizeSwitchAsync(expectedActiveId, targetId, updates, cancellationToken);

        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) =>
            inner.RestoreAccountIfUnchangedAsync(expectedCurrent, previous, cancellationToken);
    }

    /// <summary>Runs an action inside the guarded metadata finalization, i.e. between the
    /// transaction's PRECOMMIT journal write and its success cleanup.</summary>
    private sealed class FinalizeInterferenceStore(IAccountStore inner, Action onFinalize) : IAccountStore
    {
        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
            inner.ListAccountsAsync(cancellationToken);

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.GetAccountAsync(id, cancellationToken);

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            inner.GetAccountByEmailAsync(email, cancellationToken);

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            inner.GetActiveAccountIdAsync(cancellationToken);

        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) =>
            inner.SetActiveAccountIdAsync(id, cancellationToken);

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) =>
            inner.AddAccountAsync(input, cancellationToken);

        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default) =>
            inner.UpdateAccountAsync(id, updates, cancellationToken);

        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.RemoveAccountAsync(id, cancellationToken);

        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default) =>
            inner.RemoveAccountIfUnchangedAsync(expected, cancellationToken);

        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default) =>
            inner.CompareExchangeActiveAccountIdAsync(expectedId, newId, cancellationToken);

        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default)
        {
            onFinalize();
            return inner.TryFinalizeSwitchAsync(expectedActiveId, targetId, updates, cancellationToken);
        }

        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) =>
            inner.RestoreAccountIfUnchangedAsync(expectedCurrent, previous, cancellationToken);
    }
}
