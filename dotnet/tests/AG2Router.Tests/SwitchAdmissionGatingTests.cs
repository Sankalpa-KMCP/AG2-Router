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
/// Comprehensive synthetic unit tests for the central backend recovery-admission gating engine
/// (Slice 4 / ADR-001 / Domain Rules).
/// Covers all mandated test scenarios with synthetic fixtures and zero OS mutation.
/// </summary>
[Collection("SwitchCoordinator")]
public sealed class SwitchAdmissionGatingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_admission_test_{Guid.NewGuid():N}");
    private readonly TestAccountStore _accounts = new();
    private readonly FakeDpapiProvider _dpapi = new();
    private readonly TestCredentialStore _credentials;
    private readonly TestAdapter _adapter;
    private readonly TestProcessLifecycle _process;
    private readonly TestJournalStore _journal;
    private readonly SessionVault _vault;
    private readonly List<string> _globalTimeline = [];
    private AccountMetadata? _source;
    private AccountMetadata? _target;

    public SwitchAdmissionGatingTests()
    {
        Directory.CreateDirectory(_tempDir);
        _vault = new SessionVault(_tempDir, _dpapi);
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

    private NativeAccountSwitchCoordinator CreateCoordinator(TimeSpan? transactionTimeout = null) =>
        new(
            _accounts,
            _vault,
            _credentials,
            _credentials,
            _adapter,
            _process,
            _journal,
            processTimeout: TimeSpan.FromMilliseconds(100),
            verificationTimeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(5),
            rollbackTimeout: TimeSpan.FromMilliseconds(500),
            transactionTimeout: transactionTimeout ?? TimeSpan.FromSeconds(5));

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
    }

    // 1. CanAdmitSwitch: Clean/Idle state returns true, blockingReason null
    [Fact]
    public void CanAdmitSwitch_CleanIdleState_ReturnsTrueAndNullReason()
    {
        var coordinator = CreateCoordinator();

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.True(canAdmit);
        Assert.Null(blockingReason);
    }

    // 2. CanAdmitSwitch: ACTION_REQUIRED -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_ActionRequired_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        coordinator.SetJournalRecoveryStateForTest(JournalRecoveryStates.ActionRequired);

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: journal recovery action is required.", blockingReason);
    }

    // 3. CanAdmitSwitch: NOT_RESOLVABLE -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_NotResolvable_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        coordinator.SetJournalRecoveryStateForTest(JournalRecoveryStates.NotResolvable);

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: switch journal is not resolvable.", blockingReason);
    }

    // 4. CanAdmitSwitch: RESTART_REQUIRED -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_RestartRequired_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        coordinator.SetJournalRecoveryStateForTest(JournalRecoveryStates.RestartRequired);

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: application restart is required after journal resolution.", blockingReason);
    }

    // 5. CanAdmitSwitch: UNKNOWN -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_Unknown_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        coordinator.SetJournalRecoveryStateForTest(JournalRecoveryStates.Unknown);

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: journal recovery state is unknown.", blockingReason);
    }

    // 6. CanAdmitSwitch: Unrecognized non-None state -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_UnrecognizedNonNoneState_ReturnsFalseWithUnknownReason()
    {
        var coordinator = CreateCoordinator();
        coordinator.SetJournalRecoveryStateForTest("CUSTOM_NON_NONE_STATE");

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: journal recovery state is unknown.", blockingReason);
    }

    // 7. CanAdmitSwitch: In-process quarantine -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_InProcessQuarantine_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        RecoveryQuarantineRegistry.Get(_vault.GetVaultPath() + ".switch").Mark();

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: account lifecycle is quarantined.", blockingReason);
    }

    // 8. CanAdmitSwitch: Registry quarantine (.switch) -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_RegistryQuarantineSwitchResource_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        RecoveryQuarantineRegistry.Get(_vault.GetVaultPath() + ".switch").Mark();

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: account lifecycle is quarantined.", blockingReason);
    }

    // 9. CanAdmitSwitch: Registry quarantine (vault) -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_RegistryQuarantineVaultResource_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        RecoveryQuarantineRegistry.Get(_vault.GetVaultPath()).Mark();

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: account lifecycle is quarantined.", blockingReason);
    }

    // 10. CanAdmitSwitch: SessionVault quarantine -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_SessionVaultQuarantine_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        _vault.QuarantineUnresolvedMutation();

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: account lifecycle is quarantined.", blockingReason);
    }

    // 11. CanAdmitSwitch: Active transaction ID present -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_ActiveTransactionIdPresent_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        coordinator.SetActiveTransactionForTest("tx_active_123", NativeSwitchStates.Idle);

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: another switch transaction is currently in progress.", blockingReason);
    }

    // 12. CanAdmitSwitch: Current state not Idle -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_CurrentStateNotIdle_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        coordinator.SetActiveTransactionForTest(null, NativeSwitchStates.Preflight);

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: another switch transaction is currently in progress.", blockingReason);
    }

    // 13. CanAdmitSwitch: SwitchGate held -> returns false, exact reason
    [Fact]
    public async Task CanAdmitSwitch_SwitchGateHeld_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        await NativeAccountSwitchCoordinator.SwitchGateForTest.WaitAsync();
        try
        {
            bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

            Assert.False(canAdmit);
            Assert.Equal("Switch blocked: another switch transaction is currently in progress.", blockingReason);
        }
        finally
        {
            NativeAccountSwitchCoordinator.SwitchGateForTest.Release();
        }
    }

    // 14. CanAdmitSwitch: Coordinator shutting down -> returns false, exact reason
    [Fact]
    public void CanAdmitSwitch_CoordinatorShuttingDown_ReturnsFalseWithExactReason()
    {
        var coordinator = CreateCoordinator();
        coordinator.SetShuttingDownForTest(true);

        bool canAdmit = coordinator.CanAdmitSwitch(out var blockingReason);

        Assert.False(canAdmit);
        Assert.Equal("Switch blocked: coordinator is shutting down.", blockingReason);
    }

    // 15. Direct SwitchAsync invocation blocked when recovery/quarantine active
    [Fact]
    public async Task SwitchAsync_WhenRecoveryQuarantineActive_FailsWithManualRecoveryAndZeroMutations()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();
        coordinator.SetJournalRecoveryStateForTest(JournalRecoveryStates.ActionRequired);

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(SwitchResultCodes.SwitchFailedRollbackFailed, result.Code);
        Assert.Equal("Switch blocked: journal recovery action is required.", result.Message);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
    }

    // 16. Direct SwitchAsync invocation blocked when switch in progress
    [Fact]
    public async Task SwitchAsync_WhenSwitchInProgress_ReturnsSwitchInProgressCode()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();
        coordinator.SetActiveTransactionForTest("tx_busy", NativeSwitchStates.ApplyingCredential);

        var result = await coordinator.SwitchAsync(_target!.Id);

        Assert.False(result.Success);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(SwitchResultCodes.SwitchInProgress, result.Code);
        Assert.Equal("Switch blocked: another switch transaction is currently in progress.", result.Message);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
    }

    // 17. Direct SwitchAutomaticallyAsync invocation blocked when predicate false
    [Fact]
    public async Task SwitchAutomaticallyAsync_WhenPredicateFalse_ReturnsCancelledAndZeroMutations()
    {
        await SeedAccountsAsync();
        var coordinator = CreateCoordinator();

        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => false);

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.Cancelled, result.Code);
        Assert.Equal("Automatic switch plan became stale.", result.Message);
        Assert.Equal(0, _credentials.WriteCount);
        Assert.Equal(0, _process.StopCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
    }

    // 18. AutoRouter EvaluateCycleAsync halts with ManualRecoveryRequired when coordinator reports quarantine/recovery
    [Fact]
    public async Task EvaluateCycleAsync_WhenCoordinatorReportsQuarantineOrRecovery_HaltsWithManualRecoveryRequired()
    {
        var fakeStore = new FakeAutoRouterAccountStore();
        var fakeVault = new FakeAutoRouterSessionVault();
        var fakeAdapter = new MockAG2Adapter();
        var fakeCoordinator = new FakeAdmissionSwitchCoordinator
        {
            QuarantineActive = true
        };
        var safetyGate = new RoutingSafetyGate();

        await using var router = new NativeAutoRouter(
            fakeStore,
            fakeVault,
            fakeAdapter,
            fakeCoordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30),
            safetyGate: safetyGate);

        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, safetyGate.State);
        Assert.False(router.GetConfig().AutoSwitchEnabled);
        Assert.Equal(0, fakeCoordinator.CallCount);
        fakeVault.Dispose();
    }

    // 19. AutoRouter EvaluateCycleAsync skips cycle without ManualRecoveryRequired when coordinator reports in-progress transaction
    [Fact]
    public async Task EvaluateCycleAsync_WhenCoordinatorReportsInProgress_SkipsCycleWithoutManualRecoveryRequired()
    {
        var fakeStore = new FakeAutoRouterAccountStore();
        var fakeVault = new FakeAutoRouterSessionVault();
        var fakeAdapter = new MockAG2Adapter();
        var fakeCoordinator = new FakeAdmissionSwitchCoordinator
        {
            CanAdmitSwitchBehavior = _ => (false, "Switch blocked: another switch transaction is currently in progress.")
        };
        var safetyGate = new RoutingSafetyGate();

        await using var router = new NativeAutoRouter(
            fakeStore,
            fakeVault,
            fakeAdapter,
            fakeCoordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30),
            safetyGate: safetyGate);

        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Equal("Switch blocked: another switch transaction is currently in progress.", result.Reason);
        Assert.NotEqual(RoutingSafetyGateState.ManualRecoveryRequired, safetyGate.State);
        Assert.True(router.GetConfig().AutoSwitchEnabled);
        Assert.Equal(0, fakeCoordinator.CallCount);
        fakeVault.Dispose();
    }

    // 20. AutoRouter EvaluateCycleAsync candidate selection blocked before pending/execution when coordinator predicate returns false
    [Fact]
    public async Task EvaluateCycleAsync_CandidateSelectionBlocked_WhenCoordinatorPredicateReturnsFalse()
    {
        var fakeStore = new FakeAutoRouterAccountStore();
        var fakeVault = new FakeAutoRouterSessionVault();
        var fakeAdapter = new MockAG2Adapter();
        var fakeCoordinator = new FakeAdmissionSwitchCoordinator();
        var safetyGate = new RoutingSafetyGate();

        var current = new AccountMetadata("acc_current", "curr@example.com", "Current User", 1, false, "VALID", true, DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
        var candidate = new AccountMetadata("acc_candidate", "cand@example.com", "Candidate User", 1, false, "VALID", true, DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
        fakeStore.Accounts[current.Id] = current;
        fakeStore.Accounts[candidate.Id] = candidate;
        fakeStore.ActiveAccountId = "acc_current";
        fakeVault.StoredIds.Add("acc_current");
        fakeVault.StoredIds.Add("acc_candidate");

        fakeAdapter.RequestedModelOrTier = "gemini-pro";
        fakeAdapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto("curr@example.com"));
        fakeAdapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        fakeAdapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null));
        fakeAdapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        await using var router = new NativeAutoRouter(
            fakeStore,
            fakeVault,
            fakeAdapter,
            fakeCoordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30),
            safetyGate: safetyGate);

        // Populate fresh high quota for candidate
        router.SetObservedQuota("acc_candidate", 0.85, [new("Gemini Pro", "gemini-pro", 0.85, null, false)]);

        // First verify CanAdmitSwitch is true at cycle start, but gate progression fails because CanAdmitSwitch returns false
        int canAdmitCallIndex = 0;
        fakeCoordinator.CanAdmitSwitchBehavior = _ =>
        {
            canAdmitCallIndex++;
            if (canAdmitCallIndex == 1)
            {
                // Pass start of EvaluateCycleAsync
                return (true, null);
            }
            // Block at gate progression check
            return (false, "Switch blocked: coordinator is shutting down.");
        };

        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Equal("Switch blocked: coordinator is shutting down.", result.Reason);
        Assert.NotEqual(RoutingSafetyGateState.SwitchPending, safetyGate.State);
        Assert.NotEqual(RoutingSafetyGateState.SwitchInProgress, safetyGate.State);
        Assert.Equal(0, fakeCoordinator.CallCount);
        fakeVault.Dispose();
    }

    // 21. Non-secret guarantee: blockingReason contains zero credentials, tokens, or paths
    [Theory]
    [InlineData("Switch blocked: coordinator is shutting down.")]
    [InlineData("Switch blocked: journal recovery action is required.")]
    [InlineData("Switch blocked: switch journal is not resolvable.")]
    [InlineData("Switch blocked: application restart is required after journal resolution.")]
    [InlineData("Switch blocked: journal recovery state is unknown.")]
    [InlineData("Switch blocked: another switch transaction is currently in progress.")]
    [InlineData("Switch blocked: account lifecycle is quarantined.")]
    public void BlockingReason_NonSecretGuarantee_ContainsZeroSecretsTokensOrPaths(string blockingReason)
    {
        Assert.DoesNotContain("secret", blockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", blockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", blockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", blockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", blockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/", blockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", blockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".json", blockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", blockingReason, StringComparison.OrdinalIgnoreCase);
    }

    // Synthetic Test Doubles
    private sealed class TestAccountStore : InMemoryAccountStore, IAccountStore
    {
        public int FinalizeCalls { get; private set; }

        public new Task<AccountMetadata?> TryFinalizeSwitchAsync(
            string? expectedActiveAccountId,
            string targetAccountId,
            UpdateAccountInput targetUpdates,
            CancellationToken cancellationToken = default)
        {
            FinalizeCalls++;
            return base.TryFinalizeSwitchAsync(expectedActiveAccountId, targetAccountId, targetUpdates, cancellationToken);
        }
    }

    private sealed class TestCredentialStore : IWinCredReader, IWinCredWriter
    {
        private readonly List<string> _timeline;
        private WinCredEntry? _current;
        public int WriteCount { get; private set; }

        public TestCredentialStore(List<string> timeline)
        {
            _timeline = timeline;
        }

        public void Set(WinCredEntry entry)
        {
            Clear();
            _current = new WinCredEntry(entry.Target, entry.Type, entry.UserName, entry.Persistence, (byte[])entry.Blob.Clone());
        }

        public Task<WinCredEntry?> ReadCredentialAsync(string target = "gemini:antigravity", CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_current == null ? null : new WinCredEntry(_current.Target, _current.Type, _current.UserName, _current.Persistence, (byte[])_current.Blob.Clone()));
        }

        public Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            _timeline.Add($"WINCRED_WRITE:{entry.UserName}");
            Set(entry);
            return Task.FromResult(true);
        }

        public void Clear()
        {
            if (_current?.Blob is { Length: > 0 })
                CryptographicOperations.ZeroMemory(_current.Blob);
            _current = null;
        }
    }

    private sealed class TestAdapter : IAG2Adapter
    {
        private readonly List<string> _timeline;
        public AccountIdentityDto? Identity { get; set; }

        public TestAdapter(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Ag2StatusDto(true, "HEALTHY", new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")), "synthetic"));

        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Identity);

        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotaSnapshotDto?>(null);

        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountQuotaObservation(Identity, null));

        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")));
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

        public int StopCount { get; private set; }
        public int QuiesceCount { get; private set; }
        public int LaunchCount { get; private set; }
        public int RestoreCount { get; private set; }

        public TestProcessLifecycle(List<string> timeline)
        {
            _timeline = timeline;
        }

        public Task<AG2ProcessSnapshot> CaptureVerifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task RevalidateAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopVerifiedAsync(
            AG2ProcessSnapshot snapshot,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? verifyBeforeKillAsync = null,
            Action? onStopAttempted = null)
        {
            StopCount++;
            _timeline.Add("PROCESS_STOP");
            return Task.CompletedTask;
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_replacement);

        public Task QuiesceForRollbackAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration? transactionOwnedReplacement,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            QuiesceCount++;
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
            return Task.FromResult(new AG2ProcessGeneration(102, DateTime.UtcNow, original.ExecutablePath, 3));
        }

        public Task<bool> IsGenerationCurrentAsync(AG2ProcessGeneration generation, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class TestJournalStore : ISwitchJournalStore
    {
        private readonly SwitchJournalStore _underlying;
        private readonly List<string> _timeline;

        public string JournalFilePath => _underlying.JournalFilePath;

        public TestJournalStore(string journalFilePath, List<string> timeline)
        {
            _underlying = new SwitchJournalStore(journalFilePath);
            _timeline = timeline;
        }

        public Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            _underlying.ReadAsync(cancellationToken);

        public Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default) =>
            _underlying.WriteEntryAsync(entry, cancellationToken);

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            _underlying.DeleteAsync(cancellationToken);

        public Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(
            SwitchJournalEntry expectedEntry,
            CancellationToken cancellationToken = default) =>
            _underlying.DeleteIfUnchangedAsync(expectedEntry, cancellationToken);
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

    private sealed class FakeAutoRouterSessionVault : ISessionVault, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ag2_admission_vault_{Guid.NewGuid():N}");
        public HashSet<string> StoredIds { get; } = new(StringComparer.Ordinal);

        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Contains(accountId));

        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(StoredIds.Contains(accountId) ? new byte[] { 1, 2, 3 } : null);

        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Remove(accountId));

        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(StoredIds.ToList());

        public string GetVaultPath() => Path.Combine(_directory, "vault");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                try { Directory.Delete(_directory, recursive: true); } catch { }
            }
        }
    }

    private sealed class FakeAdmissionSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public Func<string?, (bool CanAdmit, string? Reason)>? CanAdmitSwitchBehavior { get; set; }
        public string JournalRecoveryState { get; set; } = JournalRecoveryStates.None;
        public bool QuarantineActive { get; set; }
        public int CallCount { get; private set; }

        public bool CanAdmitSwitch(out string? blockingReason)
        {
            if (CanAdmitSwitchBehavior != null)
            {
                var (canAdmit, reason) = CanAdmitSwitchBehavior(null);
                blockingReason = reason;
                return canAdmit;
            }
            if (QuarantineActive)
            {
                blockingReason = "Switch blocked: account lifecycle is quarantined.";
                return false;
            }
            if (!string.Equals(JournalRecoveryState, JournalRecoveryStates.None, StringComparison.Ordinal))
            {
                blockingReason = JournalRecoveryState switch
                {
                    JournalRecoveryStates.ActionRequired => "Switch blocked: journal recovery action is required.",
                    JournalRecoveryStates.NotResolvable => "Switch blocked: switch journal is not resolvable.",
                    JournalRecoveryStates.RestartRequired => "Switch blocked: application restart is required after journal resolution.",
                    _ => "Switch blocked: journal recovery state is unknown."
                };
                return false;
            }
            blockingReason = null;
            return true;
        }

        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null,
            QuarantineActive, JournalRecoveryState);

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

        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
