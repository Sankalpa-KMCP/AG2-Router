using AG2Router.AG2.Routing;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class NativeAutoRouterTests : IAsyncDisposable
{
    private readonly FakeAccountStore _accountStore = new();
    private readonly FakeSessionVault _sessionVault = new();
    private readonly MockAG2Adapter _adapter = new();
    private readonly FakeSwitchCoordinator _switchCoordinator = new();
    private readonly RoutingSafetyGate _safetyGate = new();

    private NativeAutoRouter CreateRouter(RouterConfigDto? config = null)
    {
        return new NativeAutoRouter(
            _accountStore,
            _sessionVault,
            _adapter,
            _switchCoordinator,
            initialConfig: config ?? new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30),
            safetyGate: _safetyGate
        );
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static AccountMetadata CreateAccount(string id, string email, int priority = 1, bool isReserve = false)
    {
        return new AccountMetadata(
            Id: id,
            Email: email,
            Name: "User",
            Priority: priority,
            IsReserve: isReserve,
            ValidationStatus: "VALID",
            HasVaultedSession: true,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O")
        );
    }

    [Fact]
    public async Task EvaluateCycleAsync_WhenAntigravityOffline_ReturnsNoSwitchAndResetsPending()
    {
        await using var router = CreateRouter();

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(false, "OFFLINE", null, "Offline"));

        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Equal(RoutingSafetyGateState.Idle, _safetyGate.State);
        Assert.Equal(0, _switchCoordinator.CallCount);
    }

    [Fact]
    public async Task EvaluateCycleAsync_WhenCurrentQuotaHealthy_DoesNotSwitch()
    {
        await using var router = CreateRouter();

        var current = CreateAccount("acc_current", "curr@example.com");
        var candidate = CreateAccount("acc_candidate", "cand@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = "acc_current";
        _sessionVault.StoredIds.Add("acc_current");
        _sessionVault.StoredIds.Add("acc_candidate");

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.60, null, false) },
            null, null
        ));

        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Equal(RoutingSafetyGateState.Idle, _safetyGate.State);
        Assert.Equal(0, _switchCoordinator.CallCount);
    }

    [Fact]
    public async Task EvaluateCycleAsync_WhenLowQuotaAndBusy_TransitionsToWaitingForIdleWithoutSwitching()
    {
        await using var router = CreateRouter();

        var current = CreateAccount("acc_current", "curr@example.com");
        var candidate = CreateAccount("acc_candidate", "cand@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = "acc_current";
        _sessionVault.StoredIds.Add("acc_current");
        _sessionVault.StoredIds.Add("acc_candidate");

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.10, null, false) },
            null, null
        ));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("BUSY", 1, 0, DateTime.UtcNow.ToString("O")));

        var result = await router.EvaluateCycleAsync();

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_candidate", result.BestCandidate.Account.Id);
        Assert.Equal(RoutingSafetyGateState.WaitingForIdle, _safetyGate.State);
        Assert.Equal("acc_candidate", _safetyGate.TargetAccountId);
        Assert.Equal(0, _switchCoordinator.CallCount); // Mutating coordinator was NOT called!
    }

    [Fact]
    public async Task EvaluateCycleAsync_WhenActivityTransitionsToIdle_ExecutesSwitchAndEntersCooldown()
    {
        await using var router = CreateRouter();

        var current = CreateAccount("acc_current", "curr@example.com");
        var candidate = CreateAccount("acc_candidate", "cand@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = "acc_current";
        _sessionVault.StoredIds.Add("acc_current");
        _sessionVault.StoredIds.Add("acc_candidate");

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));

        // Cycle 1: Antigravity is BUSY
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("BUSY", 2, 0, DateTime.UtcNow.ToString("O")));
        var result1 = await router.EvaluateCycleAsync();
        Assert.Equal(RoutingSafetyGateState.WaitingForIdle, _safetyGate.State);
        Assert.Equal(0, _switchCoordinator.CallCount);

        // Cycle 2: Antigravity is now IDLE
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));
        _switchCoordinator.NextCode = SwitchResultCodes.Success;

        var result2 = await router.EvaluateCycleAsync();

        Assert.Equal(1, _switchCoordinator.CallCount);
        Assert.Equal("acc_candidate", _switchCoordinator.LastTargetAccountId);
        Assert.Equal(RoutingSafetyGateState.Cooldown, _safetyGate.State);

        // Cycle 3: While in cooldown, no evaluation proceeds
        var result3 = await router.EvaluateCycleAsync();
        Assert.False(result3.ShouldSwitch);
        Assert.Contains("cooldown", result3.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, _switchCoordinator.CallCount);
    }

    [Fact]
    public async Task EvaluateCycleAsync_WhenSwitchFailsWithRollback_AppliesCandidateCooldown()
    {
        await using var router = CreateRouter();

        var current = CreateAccount("acc_current", "curr@example.com");
        var target1 = CreateAccount("acc_fail", "fail@example.com", priority: 1);
        var target2 = CreateAccount("acc_fallback", "fb@example.com", priority: 2);
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[target1.Id] = target1;
        _accountStore.Accounts[target2.Id] = target2;
        _accountStore.ActiveAccountId = "acc_current";
        _sessionVault.StoredIds.Add("acc_current");
        _sessionVault.StoredIds.Add("acc_fail");
        _sessionVault.StoredIds.Add("acc_fallback");

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        _switchCoordinator.NextCode = SwitchResultCodes.SwitchFailedRolledBack;

        await router.EvaluateCycleAsync();

        Assert.Equal(1, _switchCoordinator.CallCount);
        Assert.Equal("acc_fail", _switchCoordinator.LastTargetAccountId);
        Assert.Equal(RoutingSafetyGateState.Cooldown, _safetyGate.State);
    }

    [Fact]
    public async Task EvaluateCycleAsync_WhenSwitchFailsRollbackFailed_TripsManualRecoveryCircuitBreaker()
    {
        await using var router = CreateRouter();

        var current = CreateAccount("acc_current", "curr@example.com");
        var target = CreateAccount("acc_target", "target@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[target.Id] = target;
        _accountStore.ActiveAccountId = "acc_current";
        _sessionVault.StoredIds.Add("acc_current");
        _sessionVault.StoredIds.Add("acc_target");

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        _switchCoordinator.NextCode = SwitchResultCodes.SwitchFailedRollbackFailed;

        await router.EvaluateCycleAsync();

        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, _safetyGate.State);
        Assert.False(router.GetConfig().AutoSwitchEnabled); // Circuit breaker tripped!

        // Subsequent cycle halts immediately
        var result = await router.EvaluateCycleAsync();
        Assert.False(result.ShouldSwitch);
        Assert.Contains("manual recovery required", result.Reason, StringComparison.OrdinalIgnoreCase);

        // Operator resets manual recovery
        router.ResetManualRecovery();
        Assert.Equal(RoutingSafetyGateState.Idle, _safetyGate.State);
    }

    [Fact]
    public async Task UpdateConfig_DisablingAutoSwitch_ResetsWaitingForIdleGate()
    {
        await using var router = CreateRouter();

        var current = CreateAccount("acc_current", "curr@example.com");
        var candidate = CreateAccount("acc_candidate", "cand@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = "acc_current";
        _sessionVault.StoredIds.Add("acc_current");
        _sessionVault.StoredIds.Add("acc_candidate");

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("BUSY", 1, 0, DateTime.UtcNow.ToString("O")));

        await router.EvaluateCycleAsync();
        Assert.Equal(RoutingSafetyGateState.WaitingForIdle, _safetyGate.State);

        // Update config to disable auto switch
        router.UpdateConfig(router.GetConfig() with { AutoSwitchEnabled = false });
        Assert.Equal(RoutingSafetyGateState.Idle, _safetyGate.State);
    }

    [Fact]
    public async Task EvaluateCycleAsync_PreventsConcurrentOverlappingCycles()
    {
        await using var router = CreateRouter();

        var holdTcs = new TaskCompletionSource<bool>();
        _adapter.GetStatusFunc = async _ =>
        {
            await holdTcs.Task;
            return new Ag2StatusDto(true, "HEALTHY", null, "OK");
        };

        var task1 = Task.Run(() => router.EvaluateCycleAsync());
        await Task.Delay(50); // Let task1 start

        var result2 = await router.EvaluateCycleAsync();
        Assert.False(result2.ShouldSwitch);
        Assert.Contains("already in progress", result2.Reason);

        holdTcs.SetResult(true);
        await task1;
    }

    [Fact]
    public async Task StartAndStop_PeriodicExecutionStartsAndStopsCleanly()
    {
        await using var router = CreateRouter(new RouterConfigDto(AutoSwitchEnabled: false, PollingIntervalMs: 50));

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));

        router.Start();
        await Task.Delay(150);
        await router.StopAsync();
    }

    private sealed class FakeAccountStore : IAccountStore
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

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default)
        {
            if (ActiveAccountId == expectedId)
            {
                ActiveAccountId = newId;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeSessionVault : ISessionVault
    {
        public HashSet<string> StoredIds { get; } = new(StringComparer.Ordinal);

        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Contains(accountId));

        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(StoredIds.Contains(accountId) ? new byte[] { 1, 2, 3 } : null);

        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Remove(accountId));

        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(StoredIds.ToList());

        public string GetVaultPath() => "C:\\fake\\vault";
    }

    private sealed class FakeSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public string NextCode { get; set; } = SwitchResultCodes.Success;
        public int CallCount { get; private set; }
        public string? LastTargetAccountId { get; private set; }

        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null);

        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTargetAccountId = targetAccountId;

            bool success = NextCode == SwitchResultCodes.Success;
            string state = success ? NativeSwitchStates.Complete :
                NextCode == SwitchResultCodes.SwitchFailedRolledBack ? NativeSwitchStates.RolledBack :
                NativeSwitchStates.Failed;

            return Task.FromResult(new NativeSwitchResult(
                TransactionId: "tx_123",
                Success: success,
                Code: NextCode,
                State: state,
                TargetAccountId: targetAccountId,
                TargetEmail: "target@example.com",
                PreviousAccountId: "acc_current",
                PreviousEmail: "curr@example.com",
                Message: NextCode,
                StagesCompleted: Array.Empty<string>(),
                StartedAt: DateTimeOffset.UtcNow.ToString("O"),
                FinishedAt: DateTimeOffset.UtcNow.ToString("O"),
                ManualRecoveryRequired: NextCode == SwitchResultCodes.SwitchFailedRollbackFailed
            ));
        }
    }
}
