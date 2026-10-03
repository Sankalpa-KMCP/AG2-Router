using System.IO;
using System.Security.Cryptography;
using System.Text;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Security;
using AG2Router.AG2.Switching;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Comprehensive synthetic unit tests for post-switch live target quota verification
/// before metadata commit, with verified rollback on failure (Slice 5 / ADR-001 / Domain Rules).
/// Covers all 27 required synthetic scenarios with zero OS mutation.
/// </summary>
[Collection("SwitchCoordinator")]
public sealed partial class TargetQuotaVerificationSwitchTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_target_quota_test_{Guid.NewGuid():N}");
    private readonly TestAccountStore _accounts;
    private readonly FakeDpapiProvider _dpapi = new();
    private readonly TestCredentialStore _credentials;
    private readonly TestAdapter _adapter;
    private readonly TestProcessLifecycle _process;
    private readonly TestJournalStore _journal;
    private readonly FakeQuotaObservationStore _observationStore = new();
    private readonly SessionVault _vault;
    private readonly List<string> _globalTimeline = [];
    private AccountMetadata? _source;
    private AccountMetadata? _target;

    public TargetQuotaVerificationSwitchTests()
    {
        Directory.CreateDirectory(_tempDir);
        _vault = new SessionVault(_tempDir, _dpapi);
        _accounts = new TestAccountStore(_globalTimeline);
        _credentials = new TestCredentialStore(_globalTimeline);
        _adapter = new TestAdapter(_globalTimeline);
        _process = new TestProcessLifecycle(_globalTimeline);
        _journal = new TestJournalStore(Path.Combine(_tempDir, "switch-journal.json"), _globalTimeline);
    }

    public void Dispose()
    {
        _credentials.Clear();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private NativeAccountSwitchCoordinator CreateCoordinator(
        TimeSpan? verificationTimeout = null,
        IQuotaObservationStore? quotaObservationStore = null,
        TimeProvider? timeProvider = null) =>
        new(
            _accounts,
            _vault,
            _credentials,
            _credentials,
            _adapter,
            _process,
            _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: verificationTimeout ?? TimeSpan.FromMilliseconds(200),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(800),
            transactionTimeout: TimeSpan.FromSeconds(5),
            quotaObservationStore: quotaObservationStore ?? _observationStore, timeProvider: timeProvider);

    private async Task SeedAccountsAsync()
    {
        _source = await _accounts.AddAccountAsync(new CreateAccountInput(
            Email: "source@example.com", HasVaultedSession: true));
        _target = await _accounts.AddAccountAsync(new CreateAccountInput(
            Email: "target@example.com", HasVaultedSession: true));

        _source = await _accounts.UpdateAccountAsync(_source.Id, new UpdateAccountInput(ValidationStatus: AccountValidationStatus.Valid)) ?? _source;
        _target = await _accounts.UpdateAccountAsync(_target.Id, new UpdateAccountInput(ValidationStatus: AccountValidationStatus.Valid)) ?? _target;

        await _accounts.SetActiveAccountIdAsync(_source.Id);
        await _vault.SaveSessionAsync(_source.Id, Encoding.UTF8.GetBytes("source-secret-payload"));
        await _vault.SaveSessionAsync(_target.Id, Encoding.UTF8.GetBytes("target-secret-payload"));

        _credentials.Set(new WinCredEntry("gemini:antigravity", 1, _source.Email, 2, Encoding.UTF8.GetBytes("source-secret-payload")));
        _adapter.Identity = new AccountIdentityDto(_source.Email);

        // When process replacement launches, target identity becomes observable.
        _process.OnReplacement = () =>
        {
            _adapter.Identity = new AccountIdentityDto(_target.Email);
        };

        // When source process restores on rollback, source identity becomes observable.
        _process.OnRestore = () =>
        {
            _adapter.Identity = new AccountIdentityDto(_source.Email);
        };
    }

    // =========================================================================
    // Group 1: Success Cases (1-4)
    // =========================================================================

    [Fact]
    public async Task Success_01_TargetIdentityVerified_ModelPresent_AboveThreshold_CommitsSuccessfully()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.50, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.Equal(NativeSwitchStates.Complete, result.State);
        Assert.Contains("TARGET_IDENTITY_VERIFIED", result.StagesCompleted);
        Assert.Contains("TARGET_QUOTA_VERIFIED", result.StagesCompleted);
        Assert.Contains("METADATA_COMMITTED", result.StagesCompleted);
        Assert.Equal(_target.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Success_02_TargetQuotaExactlyAtThreshold_CommitsSuccessfully()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.30, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.Equal(NativeSwitchStates.Complete, result.State);
        Assert.Contains("TARGET_QUOTA_VERIFIED", result.StagesCompleted);
        Assert.Contains("METADATA_COMMITTED", result.StagesCompleted);
    }

    [Fact]
    public async Task Success_03_VerificationOccursBeforePrecommitAndMetadataCAS()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.60, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.True(result.Success);
        int quotaStageIdx = result.StagesCompleted.ToList().IndexOf("TARGET_QUOTA_VERIFIED");
        int metaStageIdx = result.StagesCompleted.ToList().IndexOf("METADATA_COMMITTED");
        Assert.True(quotaStageIdx >= 0);
        Assert.True(quotaStageIdx < metaStageIdx);

        int verifyIdx = _globalTimeline.IndexOf("VERIFY_QUOTA");
        int precommitIdx = _globalTimeline.IndexOf("JOURNAL_WRITE:TARGET_IDENTITY_VERIFIED_PRECOMMIT");
        int finalizeIdx = _globalTimeline.IndexOf("TRY_FINALIZE");
        Assert.True(verifyIdx >= 0);
        Assert.True(precommitIdx >= 0);
        Assert.True(finalizeIdx >= 0);
        Assert.True(verifyIdx < precommitIdx);
        Assert.True(precommitIdx < finalizeIdx);
    }

    [Fact]
    public async Task Success_04_CorrectConfiguredModelChecked_MultipleModelsPresent()
    {
        await SeedAccountsAsync();
        // Model 1 (gpt-4) is below threshold (0.10 < 0.30), but Model 2 (claude-3-5-sonnet) is healthy (0.70 >= 0.30).
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models:
            [
                new ModelQuotaDto("GPT-4", "gpt-4", 0.10, null, false),
                new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.70, null, false)
            ],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.Contains("TARGET_QUOTA_VERIFIED", result.StagesCompleted);
    }

    // =========================================================================
    // Group 2: Failure & Rollback Cases (5-14)
    // =========================================================================

    [Fact]
    public async Task Failure_05_LiveTelemetryRequestFailure_RollbackSucceeds_NoMetadataCAS()
    {
        await SeedAccountsAsync();
        _adapter.QuotaObservationBehavior = _ => Task.FromException<AccountQuotaObservation>(new IOException("Live RPC channel broken"));

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(NativeSwitchStates.RolledBack, result.State);
        Assert.Contains("TelemetryUnavailable", result.Message);
        Assert.DoesNotContain("TARGET_QUOTA_VERIFIED", result.StagesCompleted);
        Assert.DoesNotContain("METADATA_COMMITTED", result.StagesCompleted);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Failure_06_ModelAbsentFromTelemetry_RollbackSucceeds_NoMetadataCAS()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Other Model", "other-model", 0.90, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Equal(NativeSwitchStates.RolledBack, result.State);
        Assert.Contains("RequestedModelAbsent", result.Message);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Failure_07_QuotaUnknown_NullRemainingFraction_RollbackSucceeds_NoMetadataCAS()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", null, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Contains("QuotaUnknown", result.Message);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Failure_08_InvalidFraction_NaNOrOutOfBounds_RollbackSucceeds_NoMetadataCAS()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", double.NaN, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Contains("QuotaInvalid", result.Message);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Failure_09_TargetModelExhausted_RollbackSucceeds_NoMetadataCAS()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.50, null, true)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Contains("RequestedModelExhausted", result.Message);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Failure_10_TargetQuotaBelowMinimumThreshold_RollbackSucceeds_NoMetadataCAS()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.20, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Contains("BelowMinimumThreshold", result.Message);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Failure_11_TargetLiveIdentityMismatchDuringQuotaCheck_RollbackSucceeds_NoMetadataCAS()
    {
        await SeedAccountsAsync();
        // Quota check returns identity mismatch
        _adapter.QuotaObservationBehavior = _ => Task.FromResult(new AccountQuotaObservation(
            new AccountIdentityDto("imposter@example.com"),
            new QuotaSnapshotDto(
                Timestamp: DateTimeOffset.UtcNow.ToString("O"),
                Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.80, null, false)],
                PromptCredits: null, FlowCredits: null)));

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Contains("TargetIdentityMismatch", result.Message);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Failure_12_SourceMetadataRemainsActiveInAccountStoreWhenVerificationFails()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.15, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Failure_13_SourceCredentialAndProcessRestoredAfterRollback()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.10, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.True(_process.RestoreCount > 0);
        Assert.Equal(_source!.Email, _credentials.Current?.UserName);
        Assert.Contains("ORIGINAL_CREDENTIAL_RESTORED", result.StagesCompleted);
        Assert.Contains("SOURCE_PROCESS_RESTORED", result.StagesCompleted);
        Assert.Contains("SOURCE_IDENTITY_VERIFIED", result.StagesCompleted);
    }

    [Fact]
    public async Task Failure_14_RollbackFailure_PreservesQuarantinedJournal_MarksInProcessQuarantine()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.10, null, false)],
            PromptCredits: null, FlowCredits: null);

        _process.RestoreError = new IOException("Synthetic fatal hardware crash restoring process");

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Contains("ROLLBACK_FAILED", result.StagesCompleted);

        // Journal must be preserved in QUARANTINED state
        Assert.True(File.Exists(_journal.JournalFilePath));
        var readResult = await _journal.ReadAsync();
        Assert.Equal(SwitchJournalReadStatus.Valid, readResult.Status);
        Assert.Equal(SwitchJournalState.QUARANTINED, readResult.Entry!.State);

        // Coordinator status must report QuarantineActive
        Assert.True(coordinator.GetStatus().QuarantineActive);
    }

    // =========================================================================
    // Group 3: Transaction Order (15-18)
    // =========================================================================

    [Fact]
    public async Task Order_15_TargetIdentityVerificationOccursBeforeTargetQuotaVerification()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.60, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.True(result.Success);
        int identityIdx = result.StagesCompleted.ToList().IndexOf("TARGET_IDENTITY_VERIFIED");
        int quotaIdx = result.StagesCompleted.ToList().IndexOf("TARGET_QUOTA_VERIFIED");
        Assert.True(identityIdx >= 0);
        Assert.True(quotaIdx > identityIdx);
    }

    [Fact]
    public async Task Order_16_TargetQuotaVerificationOccursBeforeTargetIdentityVerifiedPrecommit()
    {
        await SeedAccountsAsync();
        // Verification will fail due to low quota
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.10, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        // TARGET_IDENTITY_VERIFIED_PRECOMMIT was NEVER written to journal
        Assert.DoesNotContain(_journal.RecordedEntries, e => e.State == SwitchJournalState.TARGET_IDENTITY_VERIFIED_PRECOMMIT);
        Assert.DoesNotContain("TARGET_IDENTITY_VERIFIED_PRECOMMIT", _journal.Operations);
    }

    [Fact]
    public async Task Order_17_TryFinalizeSwitchAsyncNeverEnteredOnVerificationFailure()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.10, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.Equal(0, _accounts.FinalizeCalls);
    }

    [Fact]
    public async Task Order_18_MetadataCASNeverEnteredOnVerificationFailure()
    {
        await SeedAccountsAsync();
        _adapter.QuotaObservationBehavior = _ => Task.FromException<AccountQuotaObservation>(new TimeoutException("Timeout"));

        var coordinator = CreateCoordinator();
        await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    // =========================================================================
    // Group 4: AutoRouter Integration (19-22)
    // =========================================================================

    [Fact]
    public async Task AutoRouter_19_ConfiguredModelAndMinimumThresholdPassedIntoCoordinator()
    {
        var fakeCoordinator = new FakeSwitchCoordinator();
        var fakeVault = new FakeAutoRouterSessionVault();
        var fakeAccounts = new FakeAutoRouterAccountStore();
        var fakeAdapter = new FakeAutoRouterAdapter();

        var currentAcc = CreateAccount("acc_curr", "curr@example.com");
        var targetAcc = CreateAccount("acc_target", "target@example.com");
        fakeAccounts.Accounts[currentAcc.Id] = currentAcc;
        fakeAccounts.Accounts[targetAcc.Id] = targetAcc;
        fakeAccounts.ActiveAccountId = currentAcc.Id;
        fakeVault.StoredIds.Add(currentAcc.Id);
        fakeVault.StoredIds.Add(targetAcc.Id);

        fakeAdapter.Identity = new AccountIdentityDto(currentAcc.Email);
        fakeAdapter.Activity = new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O"));
        fakeAdapter.RequestedModel = new RequestedModelObservation(fakeAdapter.Identity, "claude-3-5-sonnet");
        fakeAdapter.Quota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.05, null, false)],
            PromptCredits: null, FlowCredits: null);

        var config = new RouterConfigDto(
            AutoSwitchEnabled: true,
            LowQuotaThresholdPercent: 15,
            MinimumCandidateQuotaPercent: 40,
            WorkloadModelKey: "claude-3-5-sonnet");

        string obsStorePath = Path.Combine(Path.GetTempPath(), $"quota_obs_{Guid.NewGuid():N}.json");
        var obsStore = new DurableQuotaObservationStore(obsStorePath);
        await obsStore.RecordObservationsAsync("acc_target", [
            new AccountModelQuotaObservation("acc_target", "claude-3-5-sonnet", 0.80, null, DateTimeOffset.UtcNow, "Synthetic")
        ]);

        var autoRouter = new NativeAutoRouter(
            fakeAccounts, fakeVault, fakeAdapter, fakeCoordinator,
            initialConfig: config,
            quotaObservationStore: obsStore);

        autoRouter.SetObservedQuota(targetAcc.Id, 0.80, [
            new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.80, null, false)
        ]);

        // First cycle: evaluates and executes switch to target
        await autoRouter.EvaluateCycleAsync();

        Assert.Equal("claude-3-5-sonnet", fakeCoordinator.LastRequiredModelKey);
        Assert.Equal(40.0, fakeCoordinator.LastMinimumCandidateQuotaPercent);
    }

    [Fact]
    public async Task AutoRouter_20_UnknownWorkloadModelCausesZeroSwitchInvocation()
    {
        var fakeCoordinator = new FakeSwitchCoordinator();
        var fakeVault = new FakeAutoRouterSessionVault();
        var fakeAccounts = new FakeAutoRouterAccountStore();
        var fakeAdapter = new FakeAutoRouterAdapter();

        var currentAcc = CreateAccount("acc_curr", "curr@example.com");
        var targetAcc = CreateAccount("acc_target", "target@example.com");
        fakeAccounts.Accounts[currentAcc.Id] = currentAcc;
        fakeAccounts.Accounts[targetAcc.Id] = targetAcc;
        fakeAccounts.ActiveAccountId = currentAcc.Id;
        fakeVault.StoredIds.Add(currentAcc.Id);
        fakeVault.StoredIds.Add(targetAcc.Id);

        fakeAdapter.Identity = new AccountIdentityDto(currentAcc.Email);
        fakeAdapter.Activity = new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O"));
        // Requested model is UNKNOWN (null)
        fakeAdapter.RequestedModel = new RequestedModelObservation(fakeAdapter.Identity, null);
        fakeAdapter.Quota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.05, null, false)],
            PromptCredits: null, FlowCredits: null);

        var config = new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: "claude-3-5-sonnet");
        var autoRouter = new NativeAutoRouter(fakeAccounts, fakeVault, fakeAdapter, fakeCoordinator, initialConfig: config);

        var result = await autoRouter.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Equal(0, fakeCoordinator.CallCount);
    }

    [Fact]
    public async Task AutoRouter_21_Slice4AdmissionGateBlocksBeforeAutomaticSwitch()
    {
        var fakeCoordinator = new FakeSwitchCoordinator
        {
            QuarantineActive = true // Quarantined admission state
        };
        var fakeVault = new FakeAutoRouterSessionVault();
        var fakeAccounts = new FakeAutoRouterAccountStore();
        var fakeAdapter = new FakeAutoRouterAdapter();

        var currentAcc = CreateAccount("acc_curr", "curr@example.com");
        var targetAcc = CreateAccount("acc_target", "target@example.com");
        fakeAccounts.Accounts[currentAcc.Id] = currentAcc;
        fakeAccounts.Accounts[targetAcc.Id] = targetAcc;
        fakeAccounts.ActiveAccountId = currentAcc.Id;
        fakeVault.StoredIds.Add(currentAcc.Id);
        fakeVault.StoredIds.Add(targetAcc.Id);

        fakeAdapter.Identity = new AccountIdentityDto(currentAcc.Email);
        fakeAdapter.Activity = new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O"));
        fakeAdapter.RequestedModel = new RequestedModelObservation(fakeAdapter.Identity, "claude-3-5-sonnet");
        fakeAdapter.Quota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.05, null, false)],
            PromptCredits: null, FlowCredits: null);

        var config = new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: "claude-3-5-sonnet");
        var autoRouter = new NativeAutoRouter(fakeAccounts, fakeVault, fakeAdapter, fakeCoordinator, initialConfig: config);

        var result = await autoRouter.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Equal(0, fakeCoordinator.CallCount);
    }

    [Fact]
    public void AutoRouter_22_Slice3CandidateFreshnessAndRankingRemainsIntact()
    {
        var accounts = new List<AccountMetadata>
        {
            CreateAccount("curr", "curr@example.com"),
            CreateAccount("c1_stale", "c1@example.com"),
            CreateAccount("c2_fresh", "c2@example.com")
        };

        var config = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var accountQuotas = new Dictionary<string, double>
        {
            ["curr"] = 0.05,
            ["c1_stale"] = 0.90,
            ["c2_fresh"] = 0.70
        };

        var now = DateTimeOffset.UtcNow;
        var observations = new Dictionary<string, AccountModelQuotaObservation>
        {
            ["c1_stale"] = new("c1_stale", "claude-3-5-sonnet", 0.90, null, now.AddHours(-3), "Test"), // Stale (> 2 hours)
            ["c2_fresh"] = new("c2_fresh", "claude-3-5-sonnet", 0.70, null, now.AddMinutes(-5), "Test") // Fresh
        };

        var result = CandidateSelector.SelectBestCandidate(
            "curr",
            0.05,
            accounts,
            accountQuotas,
            config,
            vaultedAccountIds: new HashSet<string>(["curr", "c1_stale", "c2_fresh"], StringComparer.Ordinal),
            relevantModelKeys: ["claude-3-5-sonnet"],
            candidateModelObservations: observations,
            evaluationTimeUtc: now);

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("c2_fresh", result.BestCandidate.Account.Id);
    }

    // =========================================================================
    // Group 5: Manual Switches (23-25)
    // =========================================================================

    [Fact]
    public async Task Manual_23_ManualSwitchSucceedsUnderHealthyConditionsWithoutWorkloadModel()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.Equal(NativeSwitchStates.Complete, result.State);
        Assert.DoesNotContain("TARGET_QUOTA_VERIFIED", result.StagesCompleted);
        Assert.Contains("METADATA_COMMITTED", result.StagesCompleted);
        Assert.Equal(_target.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Manual_24_ManualSwitchDoesNotPerformTargetQuotaThresholdVerification()
    {
        await SeedAccountsAsync();
        // Target model is 100% exhausted
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.0, null, true)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();

        // Manual switch should succeed despite exhausted target model
        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.True(result.Success);
        Assert.Equal(SwitchResultCodes.Success, result.Code);
        Assert.DoesNotContain("TARGET_QUOTA_VERIFIED", result.StagesCompleted);
        Assert.Equal(_target.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task Manual_25_ManualSwitchRemainsProtectedBySlice4RecoveryQuarantineAdmission()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();

        // Put coordinator in ActionRequired journal recovery state
        coordinator.SetJournalRecoveryStateForTest(JournalRecoveryStates.ActionRequired);

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(0, _process.LaunchCount);
    }

    // =========================================================================
    // Group 6: Observation Store (26-27)
    // =========================================================================

    [Fact]
    public async Task ObservationStore_26_SuccessfulVerifiedObservationPersistedToStore()
    {
        await SeedAccountsAsync();
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.65, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.True(result.Success);
        Assert.Single(_observationStore.Records);
        var (accountId, observations) = _observationStore.Records[0];
        Assert.Equal(_target.Id, accountId);
        Assert.Single(observations);
        Assert.Equal("claude-3-5-sonnet", observations[0].ModelKey);
        Assert.Equal(0.65, observations[0].RemainingFraction);
        Assert.Equal("LiveTargetVerification", observations[0].Source);
    }

    [Fact]
    public async Task ObservationStore_27_FailedVerificationDoesNotWriteFabricatedTargetEvidence()
    {
        await SeedAccountsAsync();
        // Quota is below threshold (0.20 < 0.30)
        _adapter.TargetQuota = new QuotaSnapshotDto(
            Timestamp: DateTimeOffset.UtcNow.ToString("O"),
            Models: [new ModelQuotaDto("Claude 3.5 Sonnet", "claude-3-5-sonnet", 0.20, null, false)],
            PromptCredits: null, FlowCredits: null);

        var coordinator = CreateCoordinator();
        var result = await coordinator.SwitchAutomaticallyAsync(
            _target!.Id, _source!.Id, () => true, "claude-3-5-sonnet", 30.0);

        Assert.False(result.Success);
        Assert.Empty(_observationStore.Records);
    }

    // =========================================================================
    // Synthetic Fakes
    // =========================================================================

    private sealed class TestAccountStore : InMemoryAccountStore, IAccountStore
    {
        private readonly List<string>? _timeline;
        public int FinalizeCalls { get; private set; }
        public List<string> Operations { get; } = [];

        public TestAccountStore(List<string>? timeline = null)
        {
            _timeline = timeline;
        }

        Task<AccountMetadata?> IAccountStore.TryFinalizeSwitchAsync(
            string? expectedActiveAccountId,
            string targetAccountId,
            UpdateAccountInput targetUpdates,
            CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            Operations.Add("TRY_FINALIZE");
            _timeline?.Add("TRY_FINALIZE");
            return base.TryFinalizeSwitchAsync(expectedActiveAccountId, targetAccountId, targetUpdates, cancellationToken);
        }
    }

    private sealed class TestCredentialStore : IWinCredReader, IWinCredWriter
    {
        private readonly List<string> _timeline;
        private WinCredEntry? _current;

        public WinCredEntry? Current => _current == null ? null : Clone(_current);

        public TestCredentialStore(List<string> timeline)
        {
            _timeline = timeline;
        }

        public void Set(WinCredEntry entry)
        {
            Clear();
            _current = Clone(entry);
        }

        public Task<WinCredEntry?> ReadCredentialAsync(string target = "gemini:antigravity", CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_current == null ? null : Clone(_current));
        }

        public Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default)
        {
            _timeline.Add($"WINCRED_WRITE:{entry.UserName}");
            cancellationToken.ThrowIfCancellationRequested();
            Set(entry);
            return Task.FromResult(true);
        }

        public void Clear()
        {
            if (_current?.Blob is { Length: > 0 })
                CryptographicOperations.ZeroMemory(_current.Blob);
            _current = null;
        }

        private static WinCredEntry Clone(WinCredEntry entry) =>
            new(entry.Target, entry.Type, entry.UserName, entry.Persistence, (byte[])entry.Blob.Clone());
    }

    private sealed class TestAdapter : IAG2Adapter
    {
        private readonly List<string> _timeline;
        public AccountIdentityDto? Identity { get; set; }
        public QuotaSnapshotDto? TargetQuota { get; set; }
        public string? RequestedModel { get; set; }
        public Task<RequestedModelObservation> GetRequestedModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RequestedModelObservation(Identity, RequestedModel));
        public ActivityStatusDto Activity { get; set; } = new("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O"));
        public Ag2StatusDto Status { get; set; } = new(true, "HEALTHY", new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")), "synthetic");
        public Func<CancellationToken, Task<AccountQuotaObservation>>? QuotaObservationBehavior { get; set; }

        public TestAdapter(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Identity);
        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) => Task.FromResult(TargetQuota);
        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default)
        {
            _timeline.Add("VERIFY_QUOTA");
            if (QuotaObservationBehavior != null) return QuotaObservationBehavior(cancellationToken);
            return Task.FromResult(new AccountQuotaObservation(Identity, TargetQuota));
        }
        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Activity);
    }

    private sealed class TestProcessLifecycle : IAG2ProcessLifecycle
    {
        private readonly List<string> _timeline;
        private readonly AG2ProcessSnapshot _snapshot = new(
            100, DateTime.UtcNow.AddMinutes(-1), @"C:\Synthetic\Antigravity\resources\bin\language_server.exe",
            ["--standalone", "--csrf_token", "synthetic"],
            ["--standalone", "--csrf_token", "[REDACTED]"], 10, DateTime.UtcNow.AddDays(-1),
            new string('A', 64), 1);
        private readonly AG2ProcessGeneration _replacement = new(
            101, DateTime.UtcNow, @"C:\Synthetic\Antigravity\resources\bin\language_server.exe", 2);

        public int LaunchCount { get; private set; }
        public int RestoreCount { get; private set; }
        public Exception? RestoreError { get; set; }
        public Exception? ReplacementError { get; set; }
        public Action? OnReplacement { get; set; }
        public Action? OnRestore { get; set; }
        public Func<Task>? BeforeStopProof { get; set; }
        public Func<Task>? AfterStopProof { get; set; }
        public Func<Task>? AfterStopIssued { get; set; }

        public TestProcessLifecycle(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<AG2ProcessSnapshot> CaptureVerifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task RevalidateAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task StopVerifiedAsync(
            AG2ProcessSnapshot snapshot,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? verifyBeforeKillAsync = null,
            Action? onStopAttempted = null,
            Action? onStopIssued = null)
        {
            if (BeforeStopProof != null) await BeforeStopProof();
            if (verifyBeforeKillAsync != null) await verifyBeforeKillAsync(cancellationToken);
            if (AfterStopProof != null) await AfterStopProof();
            onStopAttempted?.Invoke();
            _timeline.Add("PROCESS_STOP");
            onStopIssued?.Invoke();
            if (AfterStopIssued != null) await AfterStopIssued();
        }

        public Task<AG2ProcessGeneration> LaunchAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            LaunchCount++;
            return Task.FromResult(_replacement);
        }

        public Task<AG2ProcessGeneration> WaitForHealthyReplacementAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration launched,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (ReplacementError != null) return Task.FromException<AG2ProcessGeneration>(ReplacementError);
            OnReplacement?.Invoke();
            return Task.FromResult(_replacement);
        }

        public Task QuiesceForRollbackAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration? transactionOwnedReplacement,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            _timeline.Add("PROCESS_QUIESCE_ROLLBACK");
            return Task.CompletedTask;
        }

        public Task<AG2ProcessGeneration> RestoreAsync(
            AG2ProcessSnapshot original,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            _timeline.Add("PROCESS_RESTORE");
            if (RestoreError != null) return Task.FromException<AG2ProcessGeneration>(RestoreError);
            OnRestore?.Invoke();
            return Task.FromResult(new AG2ProcessGeneration(102, DateTime.UtcNow, original.ExecutablePath, 3));
        }

        public Task<bool> IsGenerationCurrentAsync(AG2ProcessGeneration generation, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TestJournalStore : ISwitchJournalStore
    {
        private readonly SwitchJournalStore _underlying;
        private readonly List<string> _timeline;

        public List<SwitchJournalEntry> RecordedEntries { get; } = [];
        public List<string> Operations { get; } = [];

        public string JournalFilePath => _underlying.JournalFilePath;
        public Exception? DeleteError { get; set; }
        public Func<Task<SwitchJournalReadResult>>? ReadBehavior { get; set; }

        public TestJournalStore(string journalFilePath, List<string> timeline)
        {
            _underlying = new SwitchJournalStore(journalFilePath);
            _timeline = timeline;
        }

        public Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            ReadBehavior?.Invoke() ?? _underlying.ReadAsync(cancellationToken);

        public async Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default)
        {
            _timeline.Add($"JOURNAL_WRITE:{entry.State}");
            Operations.Add($"WRITE:{entry.State}");
            RecordedEntries.Add(entry);
            await _underlying.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            _timeline.Add("JOURNAL_DELETE");
            Operations.Add("DELETE");
            if (DeleteError != null) return Task.FromException(DeleteError);
            return _underlying.DeleteAsync(cancellationToken);
        }

        public Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(
            SwitchJournalEntry expectedEntry,
            CancellationToken cancellationToken = default) =>
            _underlying.DeleteIfUnchangedAsync(expectedEntry, cancellationToken);
    }

    private sealed class FakeQuotaObservationStore : IQuotaObservationStore
    {
        public List<(string AccountId, List<AccountModelQuotaObservation> Observations)> Records { get; } = [];

        public Task RecordCompleteSnapshotAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations,
            DateTimeOffset observedAtUtc, string source, CancellationToken cancellationToken = default) =>
            RecordObservationsAsync(accountId, observations, cancellationToken);

        public Task RecordObservationsAsync(
            string accountId,
            IEnumerable<AccountModelQuotaObservation> observations,
            CancellationToken cancellationToken = default)
        {
            Records.Add((accountId, observations.ToList()));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetAllObservationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountModelQuotaObservation>>([]);

        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetObservationsForAccountAsync(
            string accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountModelQuotaObservation>>([]);

        public Task<AccountModelQuotaObservation?> GetObservationAsync(string accountId, string modelKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountModelQuotaObservation?>(null);
    }

    private static AccountMetadata CreateAccount(string id, string email) =>
        new(id, email, email, 0, false, AccountValidationStatus.Valid, true,
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));

    private sealed class FakeSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public int CallCount { get; private set; }
        public string? LastRequiredModelKey { get; private set; }
        public double? LastMinimumCandidateQuotaPercent { get; private set; }
        public bool QuarantineActive { get; set; }

        public bool CanAdmitSwitch(out string? blockingReason)
        {
            if (QuarantineActive)
            {
                blockingReason = "Switch blocked: account lifecycle is quarantined.";
                return false;
            }
            blockingReason = null;
            return true;
        }

        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null, QuarantineActive);

        public Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present."));

        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new NativeSwitchResult(
                "tx_fake", true, SwitchResultCodes.Success, NativeSwitchStates.Complete,
                targetAccountId, "target@example.com", null, null, "SUCCESS", [],
                DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")));
        }

        public Task<NativeSwitchResult> SwitchAutomaticallyAsync(
            string targetAccountId,
            string? expectedActiveAccountId,
            Func<bool> planIsCurrent,
            string? requiredWorkloadModelKey,
            double? minimumCandidateQuotaPercent,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequiredModelKey = requiredWorkloadModelKey;
            LastMinimumCandidateQuotaPercent = minimumCandidateQuotaPercent;
            return SwitchAsync(targetAccountId, cancellationToken);
        }

        public async Task<NativeSwitchResult> SwitchAutomaticallyAsync(
            string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent,
            string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent,
            Func<CancellationToken, Task<IDisposable>> acquireInterruptionAdmissionAsync,
            CancellationToken cancellationToken = default)
        {
            using var admission = await acquireInterruptionAdmissionAsync(cancellationToken);
            return await SwitchAutomaticallyAsync(targetAccountId, expectedActiveAccountId, planIsCurrent,
                requiredWorkloadModelKey, minimumCandidateQuotaPercent, cancellationToken);
        }

        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeAutoRouterAccountStore : IAccountStore
    {
        public Dictionary<string, AccountMetadata> Accounts { get; } = new(StringComparer.Ordinal);
        public string? ActiveAccountId { get; set; }

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AccountMetadata>>(Accounts.Values.ToList());

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.TryGetValue(id, out var a) ? a : null);

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.Values.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActiveAccountId);

        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default)
        {
            ActiveAccountId = id;
            return Task.CompletedTask;
        }

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default)
        {
            if (ActiveAccountId == expectedId)
            {
                ActiveAccountId = newId;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeAutoRouterSessionVault : ISessionVault
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ag2_target_quota_vault_{Guid.NewGuid():N}");
        public HashSet<string> StoredIds { get; } = new(StringComparer.Ordinal);

        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Contains(accountId));

        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(StoredIds.Contains(accountId) ? [1, 2, 3] : null);

        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Remove(accountId));

        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(StoredIds.ToList());

        public string GetVaultPath() => Path.Combine(_directory, "vault");
    }

    private sealed class FakeAutoRouterAdapter : IAG2Adapter
    {
        public AccountIdentityDto? Identity { get; set; }
        public ActivityStatusDto Activity { get; set; } = new("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O"));
        public Ag2StatusDto Status { get; set; } = new(true, "HEALTHY", new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")), "synthetic");
        public QuotaSnapshotDto? Quota { get; set; }
        public RequestedModelObservation RequestedModel { get; set; } = new(null, null);
        public Dictionary<string, AccountModelQuotaObservation> CandidateObservations { get; } = new(StringComparer.Ordinal);

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Identity);
        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) => Task.FromResult(Quota);
        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountQuotaObservation(Identity, Quota));
        public Task<RequestedModelObservation> GetRequestedModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RequestedModel);
        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Activity);
    }
}
