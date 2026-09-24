using System.IO;
using AG2Router.AG2.Persistence;
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
        _switchCoordinator.CommitSuccess = id => _accountStore.ActiveAccountId = id;
        _adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(
            _accountStore.ActiveAccountId is { } id && _accountStore.Accounts.TryGetValue(id, out var account)
                ? new AccountIdentityDto(account.Email)
                : null);
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
        _sessionVault.Dispose();
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
        router.SetObservedQuota("acc_candidate", 0.85);

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
        router.SetObservedQuota("acc_candidate", 0.85);

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
        router.SetObservedQuota("acc_fail", 0.85);
        router.SetObservedQuota("acc_fallback", 0.80);

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
        router.SetObservedQuota("acc_target", 0.85);

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
        router.SetObservedQuota("acc_candidate", 0.85);

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
    public async Task EvaluateCycleAsync_WhenCandidateHasNoObservedQuota_CandidateIsNotEligible()
    {
        await using var router = CreateRouter();

        var current = CreateAccount("acc_current", "curr@example.com");
        var unobservedCandidate = CreateAccount("acc_unobserved", "unobs@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[unobservedCandidate.Id] = unobservedCandidate;
        _accountStore.ActiveAccountId = "acc_current";
        _sessionVault.StoredIds.Add("acc_current");
        _sessionVault.StoredIds.Add("acc_unobserved");
        // No observed quota set for acc_unobserved

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        Assert.Contains("No candidate accounts meet", result.Reason);
    }

    [Fact]
    public async Task EvaluateCycleAsync_MixedModelSnapshot_ExhaustedModelForcesAccountLevelZero()
    {
        await using var router = CreateRouter();

        var current = CreateAccount("acc_current", "curr@example.com");
        var candidate = CreateAccount("acc_candidate", "cand@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = "acc_current";
        _sessionVault.StoredIds.Add("acc_current");
        _sessionVault.StoredIds.Add("acc_candidate");
        router.SetObservedQuota("acc_candidate", 0.85);

        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        // Current account has Gemini Pro at 90% healthy, but Claude 3.7 Sonnet is 0% exhausted!
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto>
            {
                new("Gemini Pro", "gemini-pro", 0.90, null, false),
                new("Claude 3.7 Sonnet", "claude-3-7-sonnet", 0.0, null, true)
            },
            null, null
        ));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        var result = await router.EvaluateCycleAsync();

        // Weakest-link ensures Claude exhaustion is NOT masked by Gemini Pro health
        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_candidate", result.BestCandidate.Account.Id);
    }

    [Fact]
    public async Task EvaluateCycleAsync_PreventsConcurrentOverlappingCycles()
    {
        await using var router = CreateRouter();

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _adapter.GetStatusFunc = async _ =>
        {
            entered.TrySetResult();
            await holdTcs.Task;
            return new Ag2StatusDto(true, "HEALTHY", null, "OK");
        };

        var task1 = Task.Run(() => router.EvaluateCycleAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result2 = await router.EvaluateCycleAsync();
            Assert.False(result2.ShouldSwitch);
            Assert.Contains("already in progress", result2.Reason);
        }
        finally
        {
            holdTcs.TrySetResult(true);
            await task1.WaitAsync(TimeSpan.FromSeconds(5));
        }
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

    [Fact]
    public async Task ManualSwitchNotifications_CoordinateWithSafetyGateAndCooldown()
    {
        await using var router = CreateRouter();

        // 1. If safety gate was in WaitingForIdle, manual switch start resets it
        _safetyGate.Transition(RoutingSafetyGateState.LowQuotaDetected, "pre");
        _safetyGate.Transition(RoutingSafetyGateState.SwitchPending, "pre");
        _safetyGate.Transition(RoutingSafetyGateState.WaitingForIdle, "pre");
        Assert.Equal(RoutingSafetyGateState.WaitingForIdle, _safetyGate.State);

        var successToken = router.NotifyManualSwitchStarted("acc_target");
        Assert.Equal(RoutingSafetyGateState.Idle, _safetyGate.State);

        // 2. Successful manual switch places router in cooldown
        var successResult = new NativeSwitchResult(
            "tx_ok", true, SwitchResultCodes.Success,
            NativeSwitchStates.Complete, "acc_target", "t@example.com", "prev", "p@example.com",
            "Success", [], DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")
        );
        _accountStore.Accounts["acc_target"] = CreateAccount("acc_target", "t@example.com");
        _accountStore.ActiveAccountId = "acc_target";
        await router.NotifyManualSwitchCompletedAsync(successToken, successResult);
        var status = router.GetStatus();
        Assert.Equal(RoutingSafetyGateState.Cooldown, status.State);

        // 3. Rollback failed trips manual recovery
        router.ResetManualRecovery();
        var failResult = new NativeSwitchResult(
            "tx_fail", false, SwitchResultCodes.SwitchFailedRollbackFailed,
            NativeSwitchStates.Failed, "acc_target", "t@example.com", "prev", "p@example.com",
            "Rollback failed", [], DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"),
            ManualRecoveryRequired: true
        );
        var failedToken = router.NotifyManualSwitchStarted("acc_target");
        await router.NotifyManualSwitchCompletedAsync(failedToken, failResult);
        status = router.GetStatus();
        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, status.State);

        // 4. Reset manual recovery clears state
        router.ResetManualRecovery();
        status = router.GetStatus();
        Assert.Equal(RoutingSafetyGateState.Idle, status.State);
    }

    [Fact]
    public async Task UpdateConfig_PersistsToConfigPathViaDurableFileWriter()
    {
        var fakeWriter = new FakeDurableFileWriter();
        var router = new NativeAutoRouter(
            _accountStore,
            _sessionVault,
            _adapter,
            _switchCoordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true),
            safetyGate: _safetyGate,
            configFilePath: @"C:\fake\config.json",
            fileWriter: fakeWriter
        );
        await using (router)
        {
            var update = new RouterConfigDto(AutoSwitchEnabled: false, LowQuotaThresholdPercent: 25);
            var result = router.UpdateConfig(update);
            Assert.False(result.AutoSwitchEnabled);
            Assert.Equal(25, result.LowQuotaThresholdPercent);

            Assert.Equal(@"C:\fake\config.json", fakeWriter.LastWrittenPath);
            Assert.NotNull(fakeWriter.LastWrittenContent);
            Assert.Contains("\"AutoSwitchEnabled\": false", fakeWriter.LastWrittenContent);
            Assert.Contains("\"LowQuotaThresholdPercent\": 25", fakeWriter.LastWrittenContent);
        }
    }

    [Fact]
    public async Task UpdateConfig_DurableFailureDoesNotReportOrApplySuccess()
    {
        var writer = new FakeDurableFileWriter { Failure = new IOException("synthetic write failure") };
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, PollingIntervalMs: 3000);
        await using var router = new NativeAutoRouter(
            _accountStore, _sessionVault, _adapter, _switchCoordinator,
            initialConfig: initial, safetyGate: _safetyGate,
            configFilePath: @"C:\fake\config.json", fileWriter: writer);

        Assert.Throws<IOException>(() => router.UpdateConfig(
            initial with { AutoSwitchEnabled = false, PollingIntervalMs = 9000 }));
        Assert.Equal(initial, router.GetConfig());
    }

    [Fact]
    public async Task RouterRestoresPersistedPollingIntervalAtStartup()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ag2_router_config_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(configPath,
                "{\"AutoSwitchEnabled\":false,\"LowQuotaThresholdPercent\":12,\"MinimumCandidateQuotaPercent\":30,\"PollingIntervalMs\":9000}");
            await using var router = new NativeAutoRouter(
                _accountStore, _sessionVault, _adapter, _switchCoordinator,
                safetyGate: _safetyGate, configFilePath: configPath);
            Assert.Equal(9000, router.GetConfig().PollingIntervalMs);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task ManualBusyContentionDoesNotLoseCompletedAutomaticIdentity()
    {
        await using var router = CreateRouter();
        var current = CreateAccount("acc_current", "curr@example.com");
        var candidate = CreateAccount("acc_candidate", "cand@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = current.Id;
        _sessionVault.StoredIds.UnionWith([current.Id, candidate.Id]);
        router.SetObservedQuota(candidate.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));
        var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _switchCoordinator.SwitchBehavior = async (id, _) =>
        {
            admitted.TrySetResult(true);
            await release.Task;
            _accountStore.ActiveAccountId = id;
            return _switchCoordinator.MakeResult(id);
        };

        var evaluating = router.EvaluateCycleAsync();
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted(current.Id);
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, false,
            SwitchResultCodes.SwitchInProgress, NativeSwitchStates.Failed, current.Id,
            current.Email, null, null, "Automatic switch owns transaction.", [],
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")));
        release.TrySetResult(true);
        await evaluating.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(candidate.Id, router.GetStatus().ActiveAccountId);
        Assert.Equal(RoutingSafetyGateState.Cooldown, router.GetStatus().State);
        Assert.Equal(1, _switchCoordinator.CallCount);
    }

    [Fact]
    public async Task DelayedQuotaAfterManualSwitchCannotBeCachedUnderFormerAccount()
    {
        await using var router = CreateRouter();
        var former = CreateAccount("former", "former@example.com");
        var current = CreateAccount("current", "current@example.com");
        _accountStore.Accounts[former.Id] = former;
        _accountStore.Accounts[current.Id] = current;
        _accountStore.ActiveAccountId = former.Id;
        _sessionVault.StoredIds.UnionWith([former.Id, current.Id]);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        var quotaEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQuota = new TaskCompletionSource<QuotaSnapshotDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int quotaCalls = 0;
        _adapter.GetQuotaFunc = _ => Interlocked.Increment(ref quotaCalls) == 1
            ? ObserveDelayedQuota() : Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
                DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        async Task<QuotaSnapshotDto?> ObserveDelayedQuota()
        {
            quotaEntered.TrySetResult();
            return await releaseQuota.Task;
        }

        var pending = router.EvaluateCycleAsync();
        await quotaEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted(current.Id);
        _accountStore.ActiveAccountId = current.Id;
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, true,
            SwitchResultCodes.Success, NativeSwitchStates.Complete, current.Id, current.Email,
            former.Id, former.Email, "Manual success", [],
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")));
        releaseQuota.SetResult(new QuotaSnapshotDto(DateTime.UtcNow.ToString("O"),
            [new ModelQuotaDto("Gemini", "gemini", 0.9, null, false)], null, null));
        Assert.False((await pending.WaitAsync(TimeSpan.FromSeconds(5))).ShouldSwitch);

        _safetyGate.Reset();
        var next = await router.EvaluateCycleAsync();
        Assert.False(next.ShouldSwitch);
        Assert.Equal(0, _switchCoordinator.CallCount);
    }

    [Fact]
    public async Task CompletedManualSuccessCannotBeOverwrittenByDelayedAutomaticPublication()
    {
        await using var router = CreateRouter();
        var former = CreateAccount("acc_current", "former@example.com");
        var autoTarget = CreateAccount("acc_candidate", "auto@example.com");
        var manualTarget = CreateAccount("manual", "manual@example.com");
        foreach (var account in new[] { former, autoTarget, manualTarget })
            _accountStore.Accounts[account.Id] = account;
        _accountStore.ActiveAccountId = former.Id;
        _sessionVault.StoredIds.UnionWith([former.Id, autoTarget.Id, manualTarget.Id]);
        router.SetObservedQuota(autoTarget.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _switchCoordinator.SwitchBehavior = async (id, _) => {
            admitted.TrySetResult();
            await release.Task;
            return _switchCoordinator.MakeResult(id);
        };

        var evaluating = router.EvaluateCycleAsync();
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted(manualTarget.Id);
        _accountStore.ActiveAccountId = manualTarget.Id;
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, true,
            SwitchResultCodes.Success, NativeSwitchStates.Complete, manualTarget.Id, manualTarget.Email,
            former.Id, former.Email, "Manual success", [],
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")));
        _accountStore.ThrowOnActiveRead = true;
        release.TrySetResult();
        await evaluating.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(manualTarget.Id, router.GetStatus().ActiveAccountId);
        Assert.Equal(RoutingSafetyGateState.Cooldown, router.GetStatus().State);
    }

    [Fact]
    public async Task ManualCommitBeforeCallbackCannotBeOverwrittenByAutomaticPublication()
    {
        await using var router = CreateRouter();
        var former = CreateAccount("acc_current", "former@example.com");
        var autoTarget = CreateAccount("acc_candidate", "auto@example.com");
        var manualTarget = CreateAccount("manual", "manual@example.com");
        foreach (var account in new[] { former, autoTarget, manualTarget })
            _accountStore.Accounts[account.Id] = account;
        _accountStore.ActiveAccountId = former.Id;
        _sessionVault.StoredIds.UnionWith([former.Id, autoTarget.Id, manualTarget.Id]);
        router.SetObservedQuota(autoTarget.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));
        var autoCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAutoResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _switchCoordinator.SwitchBehavior = async (id, _) => {
            _accountStore.ActiveAccountId = id;
            autoCommitted.TrySetResult();
            await releaseAutoResult.Task;
            return _switchCoordinator.MakeResult(id);
        };

        var evaluating = router.EvaluateCycleAsync();
        await autoCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted(manualTarget.Id);
        // Manual coordinator has committed and released switch ownership, but its
        // loopback completion callback has not yet updated the router epoch.
        _accountStore.ActiveAccountId = manualTarget.Id;
        releaseAutoResult.TrySetResult();
        await evaluating.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(autoTarget.Id, router.GetStatus().ActiveAccountId);

        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, true,
            SwitchResultCodes.Success, NativeSwitchStates.Complete, manualTarget.Id, manualTarget.Email,
            former.Id, former.Email, "Manual success", [],
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")));
        Assert.Equal(manualTarget.Id, router.GetStatus().ActiveAccountId);
        Assert.Equal(RoutingSafetyGateState.Cooldown, router.GetStatus().State);
    }

    [Fact]
    public async Task ManualRecoveryTerminalStateSurvivesDelayedAutomaticSuccess()
    {
        await using var router = CreateRouter();
        var former = CreateAccount("acc_current", "former@example.com");
        var autoTarget = CreateAccount("acc_candidate", "auto@example.com");
        var manualTarget = CreateAccount("manual", "manual@example.com");
        foreach (var account in new[] { former, autoTarget, manualTarget })
            _accountStore.Accounts[account.Id] = account;
        _accountStore.ActiveAccountId = former.Id;
        _sessionVault.StoredIds.UnionWith([former.Id, autoTarget.Id, manualTarget.Id]);
        router.SetObservedQuota(autoTarget.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));
        var autoCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAutoResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _switchCoordinator.SwitchBehavior = async (id, _) => {
            _accountStore.ActiveAccountId = id;
            autoCommitted.TrySetResult();
            await releaseAutoResult.Task;
            return _switchCoordinator.MakeResult(id);
        };

        var evaluating = router.EvaluateCycleAsync();
        await autoCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted(manualTarget.Id);
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, false,
            SwitchResultCodes.SwitchFailedRollbackFailed, NativeSwitchStates.Failed,
            manualTarget.Id, manualTarget.Email, autoTarget.Id, autoTarget.Email,
            "Manual rollback uncertain", [], DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"), ManualRecoveryRequired: true));
        releaseAutoResult.TrySetResult();
        await evaluating.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, router.GetStatus().State);
        Assert.False(router.GetConfig().AutoSwitchEnabled);
    }

    [Fact]
    public async Task DelayedAutomaticRollbackFailureAfterManualSuccessStillRequiresRecovery()
    {
        await using var router = CreateRouter();
        var former = CreateAccount("former", "former@example.com");
        var autoTarget = CreateAccount("auto", "auto@example.com");
        var manualTarget = CreateAccount("manual", "manual@example.com");
        foreach (var account in new[] { former, autoTarget, manualTarget })
            _accountStore.Accounts[account.Id] = account;
        _accountStore.ActiveAccountId = former.Id;
        _sessionVault.StoredIds.UnionWith([former.Id, autoTarget.Id, manualTarget.Id]);
        router.SetObservedQuota(autoTarget.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));
        var autoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAutoResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _switchCoordinator.SwitchBehavior = async (id, _) =>
        {
            autoEntered.TrySetResult();
            await releaseAutoResult.Task;
            return new NativeSwitchResult(null, false, SwitchResultCodes.SwitchFailedRollbackFailed,
                NativeSwitchStates.Failed, id, autoTarget.Email, former.Id, former.Email,
                "Rollback could not be verified", [], DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"), ManualRecoveryRequired: true);
        };

        var evaluating = router.EvaluateCycleAsync();
        await autoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted(manualTarget.Id);
        _accountStore.ActiveAccountId = manualTarget.Id;
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, true,
            SwitchResultCodes.Success, NativeSwitchStates.Complete, manualTarget.Id, manualTarget.Email,
            former.Id, former.Email, "Manual success", [], DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O")));
        Assert.Equal(RoutingSafetyGateState.Cooldown, router.GetStatus().State);

        releaseAutoResult.TrySetResult();
        await evaluating.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(manualTarget.Id, router.GetStatus().ActiveAccountId);
        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, router.GetStatus().State);
        Assert.False(router.GetConfig().AutoSwitchEnabled);
    }

    [Fact]
    public async Task OverlappingManualSwitches_DelayedOlderCallbackCannotReplaceAuthoritativeIdentity()
    {
        await using var router = CreateRouter();
        _accountStore.Accounts["acc_1"] = CreateAccount("acc_1", "acc1@example.com");
        _accountStore.Accounts["acc_2"] = CreateAccount("acc_2", "acc2@example.com");
        var token1 = router.NotifyManualSwitchStarted("acc_1");
        var token2 = router.NotifyManualSwitchStarted("acc_2");

        Assert.NotEqual(token1.Id, token2.Id);

        var result2 = new NativeSwitchResult(
            TransactionId: "tx_2",
            Success: true,
            Code: SwitchResultCodes.Success,
            State: NativeSwitchStates.Complete,
            TargetAccountId: "acc_2",
            TargetEmail: "acc2@example.com",
            PreviousAccountId: "acc_1",
            PreviousEmail: "acc1@example.com",
            Message: "Switch successful",
            StagesCompleted: Array.Empty<string>(),
            StartedAt: DateTimeOffset.UtcNow.ToString("O"),
            FinishedAt: DateTimeOffset.UtcNow.ToString("O")
        );

        // A committed first; B committed later, but A's callback is delayed.
        _accountStore.ActiveAccountId = "acc_1";
        _accountStore.ActiveAccountId = "acc_2";
        await router.NotifyManualSwitchCompletedAsync(token2, result2);

        // A duplicate completion of already-completed token does nothing:
        await router.NotifyManualSwitchCompletedAsync(token2, result2);

        // Now complete token1:
        var result1 = new NativeSwitchResult(
            TransactionId: "tx_1",
            Success: true,
            Code: SwitchResultCodes.Success,
            State: NativeSwitchStates.Complete,
            TargetAccountId: "acc_1",
            TargetEmail: "acc1@example.com",
            PreviousAccountId: "acc_2",
            PreviousEmail: "acc2@example.com",
            Message: "Switch successful",
            StagesCompleted: Array.Empty<string>(),
            StartedAt: DateTimeOffset.UtcNow.ToString("O"),
            FinishedAt: DateTimeOffset.UtcNow.ToString("O")
        );
        await router.NotifyManualSwitchCompletedAsync(token1, result1);

        // A's late callback cannot undo the authoritative B publication.
        Assert.Equal("acc_2", router.GetStatus().ActiveAccountId);
        Assert.Equal(RoutingSafetyGateState.Cooldown, router.GetStatus().State);
    }

    [Fact]
    public async Task ManualCompletionWithUnreadableAuthorityFailsClosed()
    {
        await using var router = CreateRouter();
        var target = CreateAccount("target", "target@example.com");
        _accountStore.Accounts[target.Id] = target;
        _accountStore.ActiveAccountId = target.Id;
        var token = router.NotifyManualSwitchStarted(target.Id);
        _accountStore.ThrowOnActiveRead = true;

        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, true,
            SwitchResultCodes.Success, NativeSwitchStates.Complete, target.Id, target.Email,
            null, null, "Manual success", [], DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O")));

        Assert.NotEqual(target.Id, router.GetStatus().ActiveAccountId);
        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, router.GetStatus().State);
        Assert.False(router.GetConfig().AutoSwitchEnabled);
    }

    [Fact]
    public async Task OldCooldownProbeCannotClearManualRecovery()
    {
        await using var router = CreateRouter();
        _safetyGate.Transition(RoutingSafetyGateState.Cooldown, "Expired synthetic cooldown");
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<Ag2StatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _adapter.GetStatusFunc = _ => { probeEntered.TrySetResult(); return releaseProbe.Task; };

        var evaluation = router.EvaluateCycleAsync();
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted("manual");
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, false,
            SwitchResultCodes.SwitchFailedRollbackFailed, NativeSwitchStates.Failed,
            "manual", "manual@example.com", null, null, "Rollback uncertain", [],
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"),
            ManualRecoveryRequired: true));
        releaseProbe.SetResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        await evaluation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, router.GetStatus().State);
        Assert.False(router.GetConfig().AutoSwitchEnabled);
    }

    [Fact]
    public async Task OldCooldownProbeCannotClearNewManualSuccessCooldown()
    {
        await using var router = CreateRouter();
        var target = CreateAccount("manual", "manual@example.com");
        _accountStore.Accounts[target.Id] = target;
        _accountStore.ActiveAccountId = target.Id;
        _safetyGate.Transition(RoutingSafetyGateState.Cooldown, "Expired synthetic cooldown");
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource<Ag2StatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _adapter.GetStatusFunc = _ => { probeEntered.TrySetResult(); return releaseProbe.Task; };

        var evaluation = router.EvaluateCycleAsync();
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted(target.Id);
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, true,
            SwitchResultCodes.Success, NativeSwitchStates.Complete, target.Id, target.Email,
            null, null, "Manual success", [], DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O")));
        releaseProbe.SetResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        await evaluation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RoutingSafetyGateState.Cooldown, router.GetStatus().State);
        Assert.Contains("cooldown", (await router.EvaluateCycleAsync()).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManualFailureBeforeGatePublicationCannotStrandStaleEvaluation()
    {
        await using var router = CreateRouter();
        var current = CreateAccount("current", "current@example.com");
        var candidate = CreateAccount("candidate", "candidate@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = current.Id;
        _sessionVault.StoredIds.UnionWith([current.Id, candidate.Id]);
        router.SetObservedQuota(candidate.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));
        var publicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforeGatePublicationAsync = async () => {
            publicationEntered.TrySetResult();
            await releasePublication.Task;
        };

        var evaluation = router.EvaluateCycleAsync();
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var token = router.NotifyManualSwitchStarted(current.Id);
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult(null, false,
            SwitchResultCodes.Cancelled, NativeSwitchStates.Failed, current.Id, current.Email,
            null, null, "Cancelled before mutation", [], DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O")));
        releasePublication.TrySetResult();
        await evaluation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RoutingSafetyGateState.Idle, router.GetStatus().State);
        Assert.Equal(0, _switchCoordinator.CallCount);
    }

    [Fact]
    public async Task LiveIdentityMismatchCannotAttributeQuotaOrSwitch()
    {
        await using var router = CreateRouter();
        var stored = CreateAccount("stored", "stored@example.com");
        var candidate = CreateAccount("candidate", "candidate@example.com");
        _accountStore.Accounts[stored.Id] = stored;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = stored.Id;
        _sessionVault.StoredIds.UnionWith([stored.Id, candidate.Id]);
        router.SetObservedQuota(stored.Id, 0.8);
        router.SetObservedQuota(candidate.Id, 0.9);
        _adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(
            new AccountIdentityDto("external@example.com"));
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));

        Assert.False((await router.EvaluateCycleAsync()).ShouldSwitch);
        Assert.Equal(0, _switchCoordinator.CallCount);
        var observed = (Dictionary<string, double>)typeof(NativeAutoRouter)
            .GetField("_observedQuotas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(router)!;
        Assert.Equal(0.8, observed[stored.Id]);
    }

    [Fact]
    public async Task QuotaPayloadFromTransientOtherIdentityCannotPassAbaIdentityReads()
    {
        await using var router = CreateRouter();
        var stored = CreateAccount("stored", "stored@example.com");
        var candidate = CreateAccount("candidate", "candidate@example.com");
        _accountStore.Accounts[stored.Id] = stored;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = stored.Id;
        _sessionVault.StoredIds.UnionWith([stored.Id, candidate.Id]);
        router.SetObservedQuota(stored.Id, 0.8);
        router.SetObservedQuota(candidate.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetAccountQuotaObservationFunc = _ => Task.FromResult(new AccountQuotaObservation(
            new AccountIdentityDto("transient@example.com"),
            new QuotaSnapshotDto(DateTime.UtcNow.ToString("O"),
                [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null)));
        // Separate identity reads both see the original account. Only the
        // identity carried by the quota RPC reveals the transient account.

        Assert.False((await router.EvaluateCycleAsync()).ShouldSwitch);
        Assert.Equal(0, _switchCoordinator.CallCount);
        var observed = (Dictionary<string, double>)typeof(NativeAutoRouter)
            .GetField("_observedQuotas", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(router)!;
        Assert.Equal(0.8, observed[stored.Id]);
    }

    [Fact]
    public async Task UnknownActivityCannotAuthorizeAutomaticSwitch()
    {
        await using var router = CreateRouter();
        var current = CreateAccount("current", "current@example.com");
        var candidate = CreateAccount("candidate", "candidate@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = current.Id;
        _sessionVault.StoredIds.UnionWith([current.Id, candidate.Id]);
        router.SetObservedQuota(candidate.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(
            AG2Router.AG2.Normalization.AG2TelemetryNormalizer.NormalizeActivitySnapshot(null));

        await router.EvaluateCycleAsync();
        Assert.Equal(RoutingSafetyGateState.WaitingForIdle, router.GetStatus().State);
        Assert.Equal(0, _switchCoordinator.CallCount);
    }

    [Fact]
    public async Task UnclassifiedCoordinatorFailureAfterAdmissionRequiresManualRecovery()
    {
        await using var router = CreateRouter();
        var current = CreateAccount("current", "current@example.com");
        var candidate = CreateAccount("candidate", "candidate@example.com");
        _accountStore.Accounts[current.Id] = current;
        _accountStore.Accounts[candidate.Id] = candidate;
        _accountStore.ActiveAccountId = current.Id;
        _sessionVault.StoredIds.UnionWith([current.Id, candidate.Id]);
        router.SetObservedQuota(candidate.Id, 0.9);
        _adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        _adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"), [new ModelQuotaDto("Gemini", "gemini", 0.05, null, false)], null, null));
        _adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));
        _switchCoordinator.SwitchBehavior = (_, _) => throw new IOException("synthetic uncertain cleanup");

        await router.EvaluateCycleAsync();

        Assert.Equal(1, _switchCoordinator.CallCount);
        Assert.Equal(RoutingSafetyGateState.ManualRecoveryRequired, router.GetStatus().State);
        Assert.False(router.GetConfig().AutoSwitchEnabled);
    }

    private sealed class FakeDurableFileWriter : IDurableFileWriter
    {
        public Exception? Failure { get; set; }
        public string? LastWrittenPath { get; private set; }
        public string? LastWrittenContent { get; private set; }

        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Failure != null) return Task.FromException(Failure);
            LastWrittenPath = destinationPath;
            LastWrittenContent = content;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccountStore : IAccountStore
    {
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Dictionary<string, AccountMetadata> Accounts { get; } = new(StringComparer.Ordinal);
        public string? ActiveAccountId { get; set; }
        public bool ThrowOnActiveRead { get; set; }

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AccountMetadata>>(Accounts.Values.ToList());

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.TryGetValue(id, out var a) ? a : null);

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.Values.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
            => ThrowOnActiveRead
                ? Task.FromException<string?>(new IOException("Injected active identity read failure."))
                : Task.FromResult(ActiveAccountId);

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

    private sealed class FakeSessionVault : ISessionVault, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ag2_router_auto_test_{Guid.NewGuid():N}");
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
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public Action<string>? CommitSuccess { get; set; }
        public Func<string, CancellationToken, Task<NativeSwitchResult>>? SwitchBehavior { get; set; }
        public string NextCode { get; set; } = SwitchResultCodes.Success;
        public int CallCount { get; private set; }
        public string? LastTargetAccountId { get; private set; }

        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null);

        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastTargetAccountId = targetAccountId;

            if (SwitchBehavior != null) return SwitchBehavior(targetAccountId, cancellationToken);

            var result = MakeResult(targetAccountId);
            if (result.Success) CommitSuccess?.Invoke(targetAccountId);
            return Task.FromResult(result);
        }

        public NativeSwitchResult MakeResult(string targetAccountId)
        {

            bool success = NextCode == SwitchResultCodes.Success;
            string state = success ? NativeSwitchStates.Complete :
                NextCode == SwitchResultCodes.SwitchFailedRolledBack ? NativeSwitchStates.RolledBack :
                NativeSwitchStates.Failed;

            return new NativeSwitchResult(
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
            );
        }

        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
