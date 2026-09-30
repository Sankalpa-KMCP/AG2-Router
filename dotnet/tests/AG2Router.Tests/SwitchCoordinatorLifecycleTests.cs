using System.IO;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Switching;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed partial class TargetQuotaVerificationSwitchTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lifecycle_SuccessThenSecondSuccess_SameCoordinator(bool firstAutomatic, bool secondAutomatic)
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = EvidenceQuota(0.8);
        var coordinator = CreateCoordinator();
        var first = firstAutomatic
            ? await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30)
            : await coordinator.SwitchAsync(_target!.Id);
        Assert.True(first.Success);
        Assert.Equal(NativeSwitchStates.Idle, coordinator.GetStatus().CurrentState);
        Assert.Equal(NativeSwitchStates.Complete, coordinator.GetStatus().LastResult!.State);
        Assert.Null(coordinator.GetStatus().ActiveTransactionId);
        Assert.True(coordinator.CanAdmitSwitch(out _));
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto(_source!.Email);
        var second = secondAutomatic
            ? await coordinator.SwitchAutomaticallyAsync(_source!.Id, _target.Id, () => true, EvidenceModel, 30)
            : await coordinator.SwitchAsync(_source!.Id);
        Assert.True(second.Success);
        Assert.NotEqual(first.TransactionId, second.TransactionId);
        Assert.Equal(_source.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal(2, _accounts.FinalizeCalls);
        Assert.Equal(2, _process.LaunchCount);
        Assert.Equal(second, coordinator.GetStatus().LastResult);
        Assert.True(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task Lifecycle_VerifiedRollbackThenValidSwitch_SameCoordinator()
    {
        await SeedAccountsAsync();
        _process.ReplacementError = new IOException("Synthetic launch verification failure");
        var coordinator = CreateCoordinator();
        var failed = await coordinator.SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, failed.Code);
        Assert.Equal(NativeSwitchStates.RolledBack, coordinator.GetStatus().LastResult!.State);
        Assert.Equal(NativeSwitchStates.Idle, coordinator.GetStatus().CurrentState);
        Assert.True(coordinator.CanAdmitSwitch(out _));
        _process.ReplacementError = null;
        var next = await coordinator.SwitchAsync(_target.Id);
        Assert.True(next.Success);
        Assert.Equal(1, _process.RestoreCount);
        Assert.Equal(1, _accounts.FinalizeCalls);
    }

    [Fact]
    public async Task Lifecycle_PreMutationRejection_IsReusableWithoutResettingRecovery()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();
        _adapter.Activity = new ActivityStatusDto("BUSY", 1, 1, "synthetic");
        var failed = await coordinator.SwitchAsync(_target!.Id);
        Assert.Equal(SwitchResultCodes.Ag2Busy, failed.Code);
        Assert.Equal(NativeSwitchStates.Failed, coordinator.GetStatus().LastResult!.State);
        Assert.True(coordinator.CanAdmitSwitch(out _));
        _adapter.Activity = new ActivityStatusDto("IDLE", 0, 0, "synthetic");
        Assert.True((await coordinator.SwitchAsync(_target.Id)).Success);
    }

    [Fact]
    public async Task Lifecycle_QuotaRollbackThenDifferentValidCandidate_SameCoordinator()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var other = await _accounts.AddAccountAsync(new CreateAccountInput(Email: "other@example.com", HasVaultedSession: true));
        await _accounts.UpdateAccountAsync(other.Id, new UpdateAccountInput(ValidationStatus: AccountValidationStatus.Valid));
        await _vault.SaveSessionAsync(other.Id, [1, 2, 3]);
        await store.RecordObservationsAsync(other.Id, [new(other.Id, EvidenceModel, 0.9, null, clock.Now, "ActiveGetUserStatus")]);
        _adapter.TargetQuota = EvidenceQuota(0.1);
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        var failed = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, failed.Code);
        Assert.Null((await store.GetObservationAsync(_target.Id, EvidenceModel))!.RemainingFraction);
        Assert.True(coordinator.CanAdmitSwitch(out _));
        _adapter.TargetQuota = EvidenceQuota(0.9);
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto(other.Email);
        var next = await coordinator.SwitchAutomaticallyAsync(other.Id, _source.Id, () => true, EvidenceModel, 30);
        Assert.True(next.Success);
        Assert.Equal(other.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal(2, _process.LaunchCount);
        Assert.Equal(1, _process.RestoreCount);
    }

    [Theory]
    [InlineData(JournalRecoveryStates.ActionRequired)]
    [InlineData(JournalRecoveryStates.NotResolvable)]
    [InlineData(JournalRecoveryStates.Unknown)]
    [InlineData(JournalRecoveryStates.RestartRequired)]
    public async Task Lifecycle_RecoveryEstablishedDuringCompletion_IsNeverReset(string state)
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();
        coordinator.BeforeLeaseReleaseAsync = () => { coordinator.SetJournalRecoveryStateForTest(state); return Task.CompletedTask; };
        await coordinator.SwitchAsync(_target!.Id);
        Assert.Equal(state, coordinator.GetStatus().JournalRecoveryState);
        Assert.NotEqual(NativeSwitchStates.Idle, coordinator.GetStatus().CurrentState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True((await coordinator.SwitchAsync(_source!.Id)).ManualRecoveryRequired);
        Assert.Equal(1, _process.LaunchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_RetainedJournal_RemainsBlocking(bool rolledBack)
    {
        await SeedAccountsAsync();
        _journal.DeleteError = new IOException("Synthetic deletion failure");
        _adapter.TargetQuota = EvidenceQuota(rolledBack ? 0.1 : 0.8);
        var coordinator = CreateCoordinator();
        var first = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.Equal(rolledBack ? SwitchResultCodes.SwitchFailedRolledBack : SwitchResultCodes.Success, first.Code);
        Assert.True(File.Exists(_journal.JournalFilePath));
        Assert.Equal(JournalRecoveryStates.ActionRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.NotEqual(NativeSwitchStates.Idle, coordinator.GetStatus().CurrentState);
        var blocked = await coordinator.SwitchAsync(rolledBack ? _target.Id : _source.Id);
        Assert.True(blocked.ManualRecoveryRequired);
        Assert.Equal(1, _process.LaunchCount);
        if (!rolledBack)
        {
            // A proven committed journal can be legitimately cleaned, without
            // clearing quarantine or restart-required state by assertion.
            _journal.DeleteError = null;
            var resolution = await coordinator.ResolveQuarantinedJournalAsync();
            Assert.Equal(JournalResolutionStatus.CleanCleanupCompleted, resolution.Status);
            Assert.True(coordinator.CanAdmitSwitch(out _));
            _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto(_source!.Email);
            Assert.True((await coordinator.SwitchAsync(_source!.Id)).Success);
        }
    }

    [Theory]
    [InlineData(SwitchJournalReadStatus.Corrupt, JournalRecoveryStates.NotResolvable)]
    [InlineData(SwitchJournalReadStatus.UnsupportedVersion, JournalRecoveryStates.NotResolvable)]
    [InlineData(SwitchJournalReadStatus.IoError, JournalRecoveryStates.Unknown)]
    public async Task Lifecycle_UncertainCleanupRead_CannotBecomeReusable(SwitchJournalReadStatus readStatus, string recoveryState)
    {
        await SeedAccountsAsync();
        _journal.ReadBehavior = () => Task.FromResult(new SwitchJournalReadResult(readStatus));
        var coordinator = CreateCoordinator();
        Assert.True((await coordinator.SwitchAsync(_target!.Id)).Success);
        Assert.Equal(recoveryState, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True((await coordinator.SwitchAsync(_source!.Id)).ManualRecoveryRequired);
        Assert.Equal(1, _process.LaunchCount);
    }

    [Fact]
    public async Task Lifecycle_UnresolvedRollback_AndJournalResolutionRemainBlocking()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = EvidenceQuota(null);
        _process.RestoreError = new IOException("Synthetic restoration failure");
        var coordinator = CreateCoordinator();
        var failed = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.True(failed.ManualRecoveryRequired);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True((await coordinator.SwitchAsync(_target.Id)).ManualRecoveryRequired);
        Assert.Equal(1, _process.LaunchCount);
        // Resolution with a coherent source is legitimate, but requires restart;
        // it must never turn a quarantined process into a reusable coordinator.
        _adapter.Identity = new AccountIdentityDto(_source.Email);
        var session = await _vault.GetSessionAsync(_source.Id);
        Assert.NotNull(session);
        _credentials.Set(new WinCredEntry("gemini:antigravity", 1, "antigravity", 2, session));
        var resolution = await coordinator.ResolveQuarantinedJournalAsync();
        Assert.Equal(JournalResolutionStatus.ResolvedRestartRequired, resolution.Status);
        Assert.Equal(JournalRecoveryStates.RestartRequired, coordinator.GetStatus().JournalRecoveryState);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True((await coordinator.SwitchAsync(_target.Id)).ManualRecoveryRequired);
    }

    [Fact]
    public async Task Lifecycle_IndependentQuarantineAfterSuccess_IsNeverCleared()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();
        Assert.True((await coordinator.SwitchAsync(_target!.Id)).Success);
        RecoveryQuarantineRegistry.Get(_vault.GetVaultPath() + ".switch").Mark();
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.True((await coordinator.SwitchAsync(_source!.Id)).ManualRecoveryRequired);
        await coordinator.ResolveQuarantinedJournalAsync();
        Assert.True(coordinator.GetStatus().QuarantineActive);
        Assert.False(coordinator.CanAdmitSwitch(out _));
    }

    [Fact]
    public async Task Lifecycle_TerminalResultWhileCleanupOwnsTransaction_BlocksOverlap()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();
        var reachedCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.BeforeLeaseReleaseAsync = async () => { reachedCleanup.SetResult(); await releaseCleanup.Task; };
        var first = coordinator.SwitchAsync(_target!.Id);
        try
        {
            await reachedCleanup.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(NativeSwitchStates.Complete, coordinator.GetStatus().CurrentState);
            Assert.NotNull(coordinator.GetStatus().ActiveTransactionId);
            Assert.False(coordinator.CanAdmitSwitch(out _));
            Assert.Equal(SwitchResultCodes.SwitchInProgress, (await coordinator.SwitchAsync(_source!.Id)).Code);
            Assert.Equal(1, _process.LaunchCount);
        }
        finally { releaseCleanup.TrySetResult(); }
        Assert.True((await first).Success);
        Assert.True(coordinator.CanAdmitSwitch(out _));
        coordinator.BeforeLeaseReleaseAsync = null;
        _process.OnReplacement = () => _adapter.Identity = new AccountIdentityDto(_source!.Email);
        Assert.True((await coordinator.SwitchAsync(_source!.Id)).Success);
    }
}
