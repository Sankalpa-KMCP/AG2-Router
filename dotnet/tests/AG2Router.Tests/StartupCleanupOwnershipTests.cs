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
/// Decisive regressions for the B1 startup-recovery ownership defect (PROMPT #025): a
/// recovery proof authorizes cleanup of ONLY the exact journal entry that was proved. A
/// journal replaced while a startup proof is running must survive for its own
/// reconciliation, must never be deleted by the old entry's proof, and must keep switch
/// admission blocked. Conditional deletion, absence, persistence failure, and corrupt
/// interference are each exercised against the production journal store via the shared
/// synthetic harness.
/// </summary>
public sealed partial class TargetQuotaVerificationSwitchTests
{
    [Fact]
    public async Task StartupCleanup_01_QuarantinedReplacementDuringRecordedProof_IsNotDeleted_AndBlocksAdmission()
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

        // While the source-runtime proof runs, another actor replaces the journal with a
        // DIFFERENT transaction in QUARANTINED state.
        var replacement = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.QUARANTINED,
            UpdatedAt = clock.Now.AddSeconds(1),
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            QuarantineReasonCode = "ROLLBACK_FAILED",
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };
        var interferingAdapter = new InterferingAdapter(_adapter,
            onFirstIdentityProbe: () => _journal.WriteEntryAsync(replacement).GetAwaiter().GetResult());

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var reconcilingCoordinator = new NativeAccountSwitchCoordinator(
            _accounts, _vault, _credentials, _credentials, interferingAdapter, _process, _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: store, timeProvider: clock);

        var result = await reconcilingCoordinator.ReconcileStartupJournalAsync();

        // The old proof succeeds, but its cleanup must NOT delete the replacement and must
        // NOT report a clean reconciliation derived from the old proof.
        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.NotNull(result.RetainedEntry);
        Assert.True(File.Exists(_journal.JournalFilePath));

        var current = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, current.Status);
        Assert.Equal(replacement.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(SwitchJournalState.QUARANTINED, current.Entry.State);

        // Switch admission must remain blocked until the replacement is reconciled.
        Assert.Equal(JournalRecoveryStates.ActionRequired, reconcilingCoordinator.GetStatus().JournalRecoveryState);
        Assert.False(reconcilingCoordinator.CanAdmitSwitch(out _));
        var switchAttempt = await reconcilingCoordinator.SwitchAsync(_target!.Id);
        Assert.True(switchAttempt.ManualRecoveryRequired);
    }

    [Fact]
    public async Task StartupCleanup_02_SameStateReplacement_IsNotDeleted()
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

        // Same state, same source, same target: only transaction identity and timing differ.
        var replacement = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            UpdatedAt = clock.Now.AddSeconds(2),
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.NOT_ATTEMPTED
        };
        var interferingAdapter = new InterferingAdapter(_adapter,
            onFirstIdentityProbe: () => _journal.WriteEntryAsync(replacement).GetAwaiter().GetResult());

        var coordinator = new NativeAccountSwitchCoordinator(
            _accounts, _vault, _credentials, _credentials, interferingAdapter, _process, _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: store, timeProvider: clock);

        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        var current = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, current.Status);
        Assert.Equal(replacement.TransactionId, current.Entry!.TransactionId);
        Assert.True(File.Exists(_journal.JournalFilePath));
    }

    [Fact]
    public async Task StartupCleanup_03_ProvenanceOnlyReplacement_IsNotDeleted()
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

        // Same transaction identity fields apart from the provenance value: the repository's
        // exact-entry comparison treats this as a different journal.
        var replacement = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.RECORDED,
            UpdatedAt = clock.Now.AddSeconds(1),
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };
        var interferingAdapter = new InterferingAdapter(_adapter,
            onFirstIdentityProbe: () => _journal.WriteEntryAsync(replacement).GetAwaiter().GetResult());

        var coordinator = new NativeAccountSwitchCoordinator(
            _accounts, _vault, _credentials, _credentials, interferingAdapter, _process, _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: store, timeProvider: clock);

        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        var current = await _journal.ReadAsync();
        Assert.Equal(replacement.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED, current.Entry.TargetActivationProvenance);
    }

    [Fact]
    public async Task StartupCleanup_04_PrecommitReplacementDuringMetadataProof_IsNotDeleted()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        // Committed-safe PRECOMMIT state: the target's metadata commit already completed.
        await _accounts.SetActiveAccountIdAsync(_target!.Id);
        _adapter.Identity = new AccountIdentityDto(_target!.Email);
        await _journal.WriteEntryAsync(new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT,
            UpdatedAt = clock.Now,
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        });

        var replacement = new SwitchJournalEntry
        {
            TransactionId = Guid.NewGuid().ToString("D"),
            State = SwitchJournalState.ROLLING_BACK,
            UpdatedAt = clock.Now.AddSeconds(1),
            SourceAccountId = _source!.Id,
            TargetAccountId = _target!.Id,
            TargetActivationProvenance = SwitchTargetActivationProvenance.MAY_HAVE_BEEN_ATTEMPTED
        };
        // The PRECOMMIT branch reads active metadata between journal load and cleanup; the
        // replacement lands exactly inside that proof window.
        var interferingAccounts = new InterferingAccountStore(_accounts,
            onActiveIdRead: () => _journal.WriteEntryAsync(replacement).GetAwaiter().GetResult());

        var coordinator = new NativeAccountSwitchCoordinator(
            interferingAccounts, _vault, _credentials, _credentials, _adapter, _process, _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: store, timeProvider: clock);

        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        var current = await _journal.ReadAsync();
        Assert.Equal(replacement.TransactionId, current.Entry!.TransactionId);
        Assert.Equal(SwitchJournalState.ROLLING_BACK, current.Entry.State);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
    }

    [Fact]
    public async Task StartupCleanup_05_ConditionalDeletePersistenceFailure_FailsClosedWithoutDestructiveRetry()
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

        var failingStore = new FailingConditionalDeleteStore(_journal);
        var coordinator = new NativeAccountSwitchCoordinator(
            _accounts, _vault, _credentials, _credentials, _adapter, _process, failingStore,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: store, timeProvider: clock);

        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(1, failingStore.ConditionalDeleteCalls);
    }

    [Fact]
    public async Task StartupCleanup_06_JournalAbsentDuringProof_FailsClosed()
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

        // The proved entry disappears under recovery ownership; the remover cannot be
        // established, so the absence must not be reported as successful reconciliation.
        var absentStore = new AbsentConditionalDeleteStore(_journal);
        var coordinator = new NativeAccountSwitchCoordinator(
            _accounts, _vault, _credentials, _credentials, _adapter, _process, absentStore,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: store, timeProvider: clock);

        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task StartupCleanup_07_CorruptReplacementDuringProof_IsPreservedAndFailsClosed()
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

        const string corruptContent = "{ this is not a journal";
        var interferingAdapter = new InterferingAdapter(_adapter,
            onFirstIdentityProbe: () => File.WriteAllTextAsync(_journal.JournalFilePath, corruptContent).GetAwaiter().GetResult());

        var coordinator = new NativeAccountSwitchCoordinator(
            _accounts, _vault, _credentials, _credentials, interferingAdapter, _process, _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: store, timeProvider: clock);

        var result = await coordinator.ReconcileStartupJournalAsync();

        // The prior entry's proof must not erase corrupt replacement data: preservation and
        // fail-closed recovery, never normalization.
        Assert.NotEqual(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(corruptContent, await File.ReadAllTextAsync(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
    }

    [Fact]
    public async Task StartupCleanup_08_UnchangedEntryCleanup_SucceedsAndRestoresAdmission()
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

        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var result = await coordinator.ReconcileStartupJournalAsync();

        Assert.Equal(StartupJournalReconciliationStatus.Clean, result.Status);
        Assert.False(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.None, coordinator.GetStatus().JournalRecoveryState);
        Assert.True(coordinator.CanAdmitSwitch(out _));
    }

    /// <summary>Delegates to the harness adapter and runs one action on the first live
    /// identity probe, modeling a non-cooperating writer acting during the runtime proof.</summary>
    private sealed class InterferingAdapter(IAG2Adapter inner, Action onFirstIdentityProbe) : IAG2Adapter
    {
        private int _identityProbes;

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            inner.GetStatusAsync(cancellationToken);

        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _identityProbes, 1, 0) == 0)
            {
                onFirstIdentityProbe();
            }

            return inner.GetCurrentAccountAsync(cancellationToken);
        }

        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            inner.GetQuotaAsync(cancellationToken);

        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            inner.GetAccountQuotaObservationAsync(cancellationToken);

        public Task<RequestedModelObservation> GetRequestedModelAsync(CancellationToken cancellationToken = default) =>
            inner.GetRequestedModelAsync(cancellationToken);

        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) =>
            inner.GetActivityStateAsync(cancellationToken);
    }

    /// <summary>Delegates to the harness account store and runs one action on the first
    /// active-id read, modeling interference inside the PRECOMMIT proof window.</summary>
    private sealed class InterferingAccountStore(IAccountStore inner, Action onActiveIdRead) : IAccountStore
    {
        private int _activeIdReads;

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
            inner.ListAccountsAsync(cancellationToken);

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            inner.GetAccountAsync(id, cancellationToken);

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            inner.GetAccountByEmailAsync(email, cancellationToken);

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _activeIdReads, 1, 0) == 0)
            {
                onActiveIdRead();
            }

            return inner.GetActiveAccountIdAsync(cancellationToken);
        }

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

    /// <summary>Fails conditional deletion with a persistence error while leaving the file
    /// untouched, to prove fail-closed handling without destructive retries.</summary>
    private sealed class FailingConditionalDeleteStore(ISwitchJournalStore inner) : ISwitchJournalStore
    {
        public int ConditionalDeleteCalls { get; private set; }

        public string JournalFilePath => inner.JournalFilePath;

        public Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default) =>
            inner.WriteEntryAsync(entry, cancellationToken);

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(cancellationToken);

        public Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(SwitchJournalEntry expectedEntry, CancellationToken cancellationToken = default)
        {
            ConditionalDeleteCalls++;
            return Task.FromException<SwitchJournalDeleteResult>(new IOException("Synthetic conditional delete failure"));
        }

        public Task<SwitchJournalWriteResult> CreateIfAbsentAsync(SwitchJournalEntry nextEntry, CancellationToken cancellationToken = default) =>
            inner.CreateIfAbsentAsync(nextEntry, cancellationToken);

        public Task<SwitchJournalWriteResult> ReplaceIfUnchangedAsync(SwitchJournalEntry expectedEntry, SwitchJournalEntry nextEntry, CancellationToken cancellationToken = default) =>
            inner.ReplaceIfUnchangedAsync(expectedEntry, nextEntry, cancellationToken);
    }

    /// <summary>Reports the proved entry as already absent at cleanup time, to prove the
    /// fail-closed policy for ambiguous disappearance under recovery ownership.</summary>
    private sealed class AbsentConditionalDeleteStore(ISwitchJournalStore inner) : ISwitchJournalStore
    {
        public string JournalFilePath => inner.JournalFilePath;

        public Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            inner.ReadAsync(cancellationToken);

        public Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default) =>
            inner.WriteEntryAsync(entry, cancellationToken);

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(cancellationToken);

        public Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(SwitchJournalEntry expectedEntry, CancellationToken cancellationToken = default) =>
            Task.FromResult(SwitchJournalDeleteResult.Absent());

        public Task<SwitchJournalWriteResult> CreateIfAbsentAsync(SwitchJournalEntry nextEntry, CancellationToken cancellationToken = default) =>
            inner.CreateIfAbsentAsync(nextEntry, cancellationToken);

        public Task<SwitchJournalWriteResult> ReplaceIfUnchangedAsync(SwitchJournalEntry expectedEntry, SwitchJournalEntry nextEntry, CancellationToken cancellationToken = default) =>
            inner.ReplaceIfUnchangedAsync(expectedEntry, nextEntry, cancellationToken);
    }
}
