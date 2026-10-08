using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Switching;
using AG2Router.App.Server;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed class SwitchPlannerTests
{
    private readonly FakeAccountStore _accountStore = new();
    private readonly FakeSessionVault _sessionVault = new();
    private readonly FakeAdapter _adapter = new();
    private readonly FakeCoordinator _coordinator = new();
    private readonly FakeAutoRouter _autoRouter = new();
    private readonly FakeQuotaObservationStore _quotaStore = new();

    public SwitchPlannerTests()
    {
        var active = new AccountMetadata(
            Id: "acc_active",
            Email: "current@example.com",
            Name: "Current User",
            Priority: 5,
            IsReserve: false,
            ValidationStatus: "VALID",
            HasVaultedSession: true,
            CreatedAt: "2026-01-01T00:00:00Z",
            UpdatedAt: "2026-01-01T00:00:00Z",
            LastActiveAt: null,
            Notes: null,
            Alias: "current",
            IsActive: true
        );
        _accountStore.Accounts[active.Id] = active;
        _accountStore.ActiveAccountId = active.Id;
        _sessionVault.VaultedIds.Add(active.Id);

        var other = new AccountMetadata(
            Id: "acc_other",
            Email: "current@example.com",
            Name: "Current User",
            Priority: 5,
            IsReserve: false,
            ValidationStatus: "VALID",
            HasVaultedSession: true,
            CreatedAt: "2026-01-01T00:00:00Z",
            UpdatedAt: "2026-01-01T00:00:00Z",
            LastActiveAt: null,
            Notes: null,
            Alias: "other",
            IsActive: true
        );
        _accountStore.Accounts[other.Id] = other;
        _sessionVault.VaultedIds.Add(other.Id);
    }

    private SwitchPlanner CreatePlanner() =>
        new(_accountStore, _sessionVault, _adapter, _coordinator, _autoRouter, _quotaStore);

    private AccountMetadata CreateTargetAccount(
        string id = "acc_target",
        string email = "target@example.com",
        bool isReserve = false,
        string validationStatus = "VALID") =>
        new(
            Id: id,
            Email: email,
            Name: "Target User",
            Priority: 10,
            IsReserve: isReserve,
            ValidationStatus: validationStatus,
            HasVaultedSession: true,
            CreatedAt: "2026-01-01T00:00:00Z",
            UpdatedAt: "2026-01-01T00:00:00Z",
            LastActiveAt: null,
            Notes: null,
            Alias: "target",
            IsActive: false
        );

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task PlanSwitchAsync_TargetNullOrWhitespace_ReturnsTargetNotFound(string? targetId)
    {
        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(targetId!);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TargetNotFound, plan.ReasonCode);
        Assert.False(plan.TargetExists);
    }

    [Fact]
    public async Task PlanSwitchAsync_TargetDoesNotExist_ReturnsTargetNotFound()
    {
        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync("acc_nonexistent");

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TargetNotFound, plan.ReasonCode);
        Assert.False(plan.TargetExists);
    }

    [Fact]
    public async Task PlanSwitchAsync_TargetNotVaulted_ReturnsTargetNotVaulted()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        // Session not in vault
        _sessionVault.VaultedIds.Remove(target.Id);

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TargetNotVaulted, plan.ReasonCode);
        Assert.True(plan.TargetExists);
        Assert.False(plan.HasVaultedSession);
    }

    [Fact]
    public async Task PlanSwitchAsync_TargetAlreadyActiveByStoreId_ReturnsAlreadyActive()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _accountStore.ActiveAccountId = target.Id;

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.AlreadyActive, plan.ReasonCode);
        Assert.True(plan.IsAlreadyActive);
    }

    [Fact]
    public async Task PlanSwitchAsync_TargetAlreadyActiveByLiveEmail_ReturnsAlreadyActive()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _accountStore.ActiveAccountId = "acc_other";
        _adapter.CurrentAccount = new AccountIdentityDto(target.Email, "Live Target", "tier_pro", "Pro");

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.AlreadyActive, plan.ReasonCode);
        Assert.True(plan.IsAlreadyActive);
    }

    [Theory]
    [InlineData("EXPIRED")]
    [InlineData("FAILED")]
    public async Task PlanSwitchAsync_TargetValidationStatusIneligible_ReturnsTargetIneligible(string status)
    {
        var target = CreateTargetAccount(validationStatus: status);
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TargetIneligible, plan.ReasonCode);
        Assert.False(plan.IsEligible);
    }

    [Fact]
    public async Task PlanSwitchAsync_CoordinatorSafetyBlocked_ReturnsSafetyBlocked()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _coordinator.CanAdmit = false;
        _coordinator.BlockReason = "Quarantine is active.";
        _coordinator.StatusToReturn = new NativeSwitchStatus(null, NativeSwitchStates.Idle, null, QuarantineActive: true);

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.SafetyBlocked, plan.ReasonCode);
    }

    [Fact]
    public async Task PlanSwitchAsync_CoordinatorSwitchInProgress_ReturnsSwitchInProgress()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _coordinator.CanAdmit = false;
        _coordinator.BlockReason = "Switch transaction in progress.";
        _coordinator.StatusToReturn = new NativeSwitchStatus("tx_456", NativeSwitchStates.ApplyingCredential, null);

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.SwitchInProgress, plan.ReasonCode);
    }

    [Fact]
    public async Task PlanSwitchAsync_AutoRouterCooldownActive_ReturnsCooldownActive()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Status = new RouterStatusDto(
            State: RoutingSafetyGateState.Cooldown,
            AutoSwitchEnabled: true,
            ActiveAccountId: "acc_other",
            ActiveAccountEmail: "other@example.com",
            PendingTargetAccountId: null,
            LastEvaluatedAt: null,
            LastDecisionReason: null,
            Config: new RouterConfigDto()
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.CooldownActive, plan.ReasonCode);
        Assert.True(plan.InCooldown);
    }

    [Fact]
    public async Task PlanSwitchAsync_Ag2DegradedOrOffline_ReturnsTelemetryUnavailable()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _adapter.Status = new Ag2StatusDto(Connected: false, Status: "OFFLINE", null, "Cannot connect to AG2 RPC.");

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TelemetryUnavailable, plan.ReasonCode);
    }

    [Fact]
    public async Task PlanSwitchAsync_Ag2Busy_ReturnsAg2Busy()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _adapter.Activity = new ActivityStatusDto(
            State: "BUSY",
            TotalTrajectories: 2,
            RunningTrajectories: 1,
            Timestamp: DateTimeOffset.UtcNow.ToString("O")
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Ag2Busy, plan.ReasonCode);
    }

    [Fact]
    public async Task PlanSwitchAsync_TargetQuotaExhausted_RemainsAdmissibleWithWarning()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Config = new RouterConfigDto(WorkloadModelKey: "claude-3-5-sonnet");
        _quotaStore.Observations[(target.Id, "claude-3-5-sonnet")] = new AccountModelQuotaObservation(
            target.Id,
            "claude-3-5-sonnet",
            0.0,
            null,
            DateTimeOffset.UtcNow,
            "test"
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
        Assert.Equal("EXHAUSTED", plan.QuotaStatus);
        Assert.Equal(0.0, plan.TargetQuotaPercent);
        Assert.Contains("quota is exhausted", plan.Message);
    }

    [Fact]
    public async Task PlanSwitchAsync_TargetQuotaBelowMinimum_RemainsAdmissibleWithWarning()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Config = new RouterConfigDto(
            WorkloadModelKey: "claude-3-5-sonnet",
            MinimumCandidateQuotaPercent: 50
        );
        _quotaStore.Observations[(target.Id, "claude-3-5-sonnet")] = new AccountModelQuotaObservation(
            target.Id,
            "claude-3-5-sonnet",
            0.25, // 25% < 50%
            null,
            DateTimeOffset.UtcNow,
            "test"
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
        Assert.Equal("BELOW_MINIMUM", plan.QuotaStatus);
        Assert.Equal(25.0, plan.TargetQuotaPercent);
        Assert.Contains("below candidate threshold", plan.Message);
    }

    [Fact]
    public async Task PlanSwitchAsync_MissingWorkloadModelKey_RemainsAdmissibleAsUnconfigured()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Config = new RouterConfigDto(WorkloadModelKey: null);

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
        Assert.Equal("UNCONFIGURED", plan.QuotaStatus);
        Assert.Null(plan.TargetQuotaPercent);
    }

    [Fact]
    public async Task PlanSwitchAsync_WorkloadModelObservationMissing_RemainsAdmissibleAsNotObserved()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Config = new RouterConfigDto(WorkloadModelKey: "claude-3-5-sonnet");
        // Observation exists for gemini-pro, but NOT for claude-3-5-sonnet
        _quotaStore.Observations[(target.Id, "gemini-pro")] = new AccountModelQuotaObservation(
            target.Id,
            "gemini-pro",
            0.8,
            null,
            DateTimeOffset.UtcNow,
            "test"
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
        Assert.Equal("NOT_OBSERVED", plan.QuotaStatus);
        Assert.Null(plan.TargetQuotaPercent);
    }

    [Fact]
    public async Task PlanSwitchAsync_AllPreconditionsSatisfied_ReturnsSuccessAndAdmissible()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _accountStore.ActiveAccountId = "acc_other";
        _autoRouter.Config = new RouterConfigDto(
            WorkloadModelKey: "claude-3-5-sonnet",
            MinimumCandidateQuotaPercent: 30
        );
        _quotaStore.Observations[(target.Id, "claude-3-5-sonnet")] = new AccountModelQuotaObservation(
            target.Id,
            "claude-3-5-sonnet",
            0.85,
            null,
            DateTimeOffset.UtcNow,
            "test"
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
        Assert.True(plan.TargetExists);
        Assert.True(plan.HasVaultedSession);
        Assert.False(plan.IsAlreadyActive);
        Assert.Equal("claude-3-5-sonnet", plan.WorkloadModelKey);
        Assert.Equal(85.0, plan.TargetQuotaPercent);
        Assert.Equal("USABLE", plan.QuotaStatus);
    }

    [Fact]
    public async Task PlanSwitchAsync_StrictlyReadOnly_PerformsZeroMutations()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _accountStore.ActiveAccountId = "acc_other";

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.True(plan.Admissible);
        Assert.Equal(0, _accountStore.MutationCallCount);
        Assert.Equal(0, _sessionVault.MutationCallCount);
        Assert.Equal(0, _coordinator.SwitchCallCount);
    }

    [Fact]
    public async Task PlanSwitchAsync_LiveIdentityMismatch_ReturnsTelemetryUnavailable()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _adapter.CurrentAccount = new AccountIdentityDto("diverged@example.com", "Diverged", "tier_pro", "Pro");

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TelemetryUnavailable, plan.ReasonCode);
        Assert.Equal("Live identity does not match the active account metadata.", plan.Message);
    }

    [Fact]
    public async Task PlanSwitchAsync_ActiveAccountMissingInStore_ReturnsTelemetryUnavailable()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _accountStore.ActiveAccountId = null;

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TelemetryUnavailable, plan.ReasonCode);
        Assert.Equal("Live identity does not match the active account metadata.", plan.Message);
    }

    [Fact]
    public async Task PlanSwitchAsync_LiveIdentityMatchesStored_DoesNotBlock()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        // Default constructor set _accountStore.ActiveAccountId to acc_active with current@example.com
        // and _adapter.CurrentAccount has current@example.com

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
    }

    [Theory]
    [InlineData("STARTING")]
    [InlineData("PAUSED")]
    [InlineData("UNKNOWN")]
    [InlineData("DEGRADED")]
    public async Task PlanSwitchAsync_Ag2ActivityNotIdle_ReturnsTelemetryUnavailable(string activityState)
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _adapter.Activity = new ActivityStatusDto(
            State: activityState,
            TotalTrajectories: 0,
            RunningTrajectories: 0,
            Timestamp: DateTimeOffset.UtcNow.ToString("O")
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TelemetryUnavailable, plan.ReasonCode);
        Assert.Equal("Antigravity activity telemetry is unknown or degraded.", plan.Message);
    }

    [Fact]
    public async Task PlanSwitchAsync_Ag2ActivityRunningTrajectoriesWithIdle_ReturnsAg2Busy()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _adapter.Activity = new ActivityStatusDto(
            State: "IDLE",
            TotalTrajectories: 2,
            RunningTrajectories: 1,
            Timestamp: DateTimeOffset.UtcNow.ToString("O")
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Ag2Busy, plan.ReasonCode);
        Assert.Equal("Antigravity must be IDLE before switching.", plan.Message);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task PlanSwitchAsync_NonFiniteQuota_ReturnsUnknownAndDoesNotProduceNonFinitePercent(double remainingFraction)
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Config = new RouterConfigDto(WorkloadModelKey: "claude-3-5-sonnet");
        var baseObs = new AccountModelQuotaObservation(
            target.Id,
            "claude-3-5-sonnet",
            0.5,
            null,
            DateTimeOffset.UtcNow,
            "test"
        );
        _quotaStore.Observations[(target.Id, "claude-3-5-sonnet")] = baseObs with { RemainingFraction = remainingFraction };

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.Equal("UNKNOWN", plan.QuotaStatus);
        Assert.Null(plan.TargetQuotaPercent);
        Assert.True(plan.Admissible);

        var json = JsonSerializer.Serialize(plan);
        Assert.NotNull(json);
        Assert.DoesNotContain("NaN", json);
        Assert.DoesNotContain("Infinity", json);
    }

    [Fact]
    public async Task PlanSwitchAsync_NullRemainingFraction_ReturnsUnknown()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Config = new RouterConfigDto(WorkloadModelKey: "claude-3-5-sonnet");
        _quotaStore.Observations[(target.Id, "claude-3-5-sonnet")] = new AccountModelQuotaObservation(
            target.Id,
            "claude-3-5-sonnet",
            null,
            null,
            DateTimeOffset.UtcNow,
            "test"
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.Equal("UNKNOWN", plan.QuotaStatus);
        Assert.Null(plan.TargetQuotaPercent);
        Assert.True(plan.Admissible);
    }

    [Fact]
    public async Task PlanSwitchAsync_StaleQuota_AdmissibleForManualSwitch()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Config = new RouterConfigDto(WorkloadModelKey: "claude-3-5-sonnet");
        _quotaStore.Observations[(target.Id, "claude-3-5-sonnet")] = new AccountModelQuotaObservation(
            target.Id,
            "claude-3-5-sonnet",
            0.0,
            null,
            DateTimeOffset.UtcNow.AddHours(-3),
            "test"
        );

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.Equal("STALE", plan.QuotaStatus);
        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
    }

    [Fact]
    public async Task PlanSwitchAsync_MissingQuota_AdmissibleForManualSwitch()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _autoRouter.Config = new RouterConfigDto(WorkloadModelKey: "claude-3-5-sonnet");

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.Equal("NOT_OBSERVED", plan.QuotaStatus);
        Assert.Null(plan.TargetQuotaPercent);
        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
    }

    [Fact]
    public async Task PlanSwitchAsync_ReserveAccount_AdmissibleForManualSwitch()
    {
        var target = CreateTargetAccount(isReserve: true);
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);

        var planner = CreatePlanner();
        var plan = await planner.PlanSwitchAsync(target.Id);

        Assert.True(plan.IsReserve);
        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
    }

    // ---- Loopback HTTP Endpoint Integration Tests ----

    [Fact]
    public async Task LoopbackServer_PostSwitchPlan_ValidAccount_Returns200WithDto()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        _sessionVault.VaultedIds.Add(target.Id);
        _accountStore.ActiveAccountId = "acc_other";

        var planner = CreatePlanner();
        await using var server = new LoopbackServer();
        await server.StartAsync(requestedPort: 0, switchPlanner: planner);

        using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };
        using var response = await client.PostAsync($"/api/accounts/{target.Id}/switch-plan", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<SwitchPlanResultDto>();
        Assert.NotNull(plan);
        Assert.True(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.Success, plan.ReasonCode);
        Assert.Equal(target.Id, plan.TargetAccountId);
    }

    [Fact]
    public async Task LoopbackServer_PostSwitchPlan_UnknownAccount_Returns404WithDto()
    {
        var planner = CreatePlanner();
        await using var server = new LoopbackServer();
        await server.StartAsync(requestedPort: 0, switchPlanner: planner);

        using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };
        using var response = await client.PostAsync("/api/accounts/acc_missing/switch-plan", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<SwitchPlanResultDto>();
        Assert.NotNull(plan);
        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TargetNotFound, plan.ReasonCode);
        Assert.False(plan.TargetExists);
    }

    [Fact]
    public async Task LoopbackServer_PostSwitchPlan_InadmissibleAccount_Returns200WithAdmissibleFalse()
    {
        var target = CreateTargetAccount();
        _accountStore.Accounts[target.Id] = target;
        // Not vaulted
        _sessionVault.VaultedIds.Remove(target.Id);

        var planner = CreatePlanner();
        await using var server = new LoopbackServer();
        await server.StartAsync(requestedPort: 0, switchPlanner: planner);

        using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };
        using var response = await client.PostAsync($"/api/accounts/{target.Id}/switch-plan", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = await response.Content.ReadFromJsonAsync<SwitchPlanResultDto>();
        Assert.NotNull(plan);
        Assert.False(plan.Admissible);
        Assert.Equal(SwitchPlanReasonCodes.TargetNotVaulted, plan.ReasonCode);
    }

    [Fact]
    public async Task LoopbackServer_PostSwitchPlan_RejectsForeignOrigin()
    {
        var planner = CreatePlanner();
        await using var server = new LoopbackServer();
        await server.StartAsync(requestedPort: 0, switchPlanner: planner);

        using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/acc_test/switch-plan");
        request.Headers.Add("Origin", "https://malicious.example.com");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LoopbackServer_PostSwitchPlan_RejectsSecFetchSiteCrossSite()
    {
        var planner = CreatePlanner();
        await using var server = new LoopbackServer();
        await server.StartAsync(requestedPort: 0, switchPlanner: planner);

        using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/acc_test/switch-plan");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LoopbackServer_PostSwitchPlan_WhenPlannerNull_Returns501()
    {
        await using var server = new LoopbackServer();
        await server.StartAsync(requestedPort: 0, switchPlanner: null);

        using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };
        using var response = await client.PostAsync("/api/accounts/acc_test/switch-plan", null);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    // ---- Fakes ----

    private sealed class FakeAccountStore : IAccountStore
    {
        public Dictionary<string, AccountMetadata> Accounts { get; } = new(StringComparer.Ordinal);
        public string? ActiveAccountId { get; set; }
        public int MutationCallCount { get; private set; }

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountMetadata>>(Accounts.Values.ToList());

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accounts.TryGetValue(id, out var a) ? a : null);

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(Accounts.Values.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ActiveAccountId);

        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            ActiveAccountId = id;
            return Task.CompletedTask;
        }

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            throw new NotImplementedException();
        }

        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            throw new NotImplementedException();
        }

        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            throw new NotImplementedException();
        }

        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            throw new NotImplementedException();
        }

        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            throw new NotImplementedException();
        }

        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            throw new NotImplementedException();
        }

        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            throw new NotImplementedException();
        }
    }

    private sealed class FakeSessionVault : ISessionVault
    {
        public HashSet<string> VaultedIds { get; } = new(StringComparer.Ordinal);
        public int MutationCallCount { get; private set; }

        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(VaultedIds.Contains(accountId));

        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(VaultedIds.Contains(accountId) ? new byte[] { 1, 2, 3 } : null);

        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default)
        {
            MutationCallCount++;
            return Task.FromResult(VaultedIds.Remove(accountId));
        }

        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(VaultedIds.ToList());

        public string GetVaultPath() => "C:\\fake\\vault";
    }

    private sealed class FakeAdapter : IAG2Adapter
    {
        public Ag2StatusDto Status { get; set; } = new(Connected: true, Status: "ONLINE", null, "Operational");
        public AccountIdentityDto? CurrentAccount { get; set; } = new("current@example.com", "Current User", "tier_pro", "Pro");
        public ActivityStatusDto Activity { get; set; } = new("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O"));

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentAccount);

        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotaSnapshotDto?>(null);

        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountQuotaObservation(CurrentAccount, null));

        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Activity);
    }

    private sealed class FakeCoordinator : INativeAccountSwitchCoordinator
    {
        public bool CanAdmit { get; set; } = true;
        public string? BlockReason { get; set; }
        public NativeSwitchStatus StatusToReturn { get; set; } = new(null, NativeSwitchStates.Idle, null);
        public int SwitchCallCount { get; private set; }

        public bool CanAdmitSwitch(out string? blockingReason)
        {
            blockingReason = BlockReason;
            return CanAdmit;
        }

        public NativeSwitchStatus GetStatus() => StatusToReturn;

        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default)
        {
            SwitchCallCount++;
            return Task.FromResult(new NativeSwitchResult(
                "tx_1", true, SwitchResultCodes.Success, NativeSwitchStates.Complete,
                targetAccountId, "target@example.com", null, null, "SUCCESS", [],
                DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")
            ));
        }

        public Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No journal."));

        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeAutoRouter : INativeAutoRouter
    {
        public RouterConfigDto Config { get; set; } = new();
        public RouterStatusDto Status { get; set; } = new(
            RoutingSafetyGateState.Idle, false, null, null, null, null, null, new RouterConfigDto());

        public RouterConfigDto GetConfig() => Config;
        public RouterStatusDto GetStatus() => Status;

        public Task<SelectionResult> EvaluateCycleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SelectionResult(false, "Idle", null, null, null, []));

        public RouterConfigDto UpdateConfig(RouterConfigDto updates) => Config = updates;
        public void ResetManualRecovery() { }
        public ManualSwitchToken NotifyManualSwitchStarted(string targetAccountId) => new(1);
        public Task NotifyManualSwitchCompletedAsync(ManualSwitchToken token, NativeSwitchResult result) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeQuotaObservationStore : IQuotaObservationStore
    {
        public Dictionary<(string AccountId, string ModelKey), AccountModelQuotaObservation> Observations { get; } = new();

        public Task<AccountModelQuotaObservation?> GetObservationAsync(string accountId, string modelKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Observations.TryGetValue((accountId, modelKey), out var obs) ? obs : null);

        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetAllObservationsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountModelQuotaObservation>>(Observations.Values.ToList());

        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetObservationsForAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountModelQuotaObservation>>(Observations.Values.Where(o => o.AccountId == accountId).ToList());

        public Task RecordObservationsAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
