using AG2Router.AG2.Routing;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed partial class TargetQuotaVerificationSwitchTests
{
    private void SetRoutingSourceQuota(double fraction)
    {
        _adapter.QuotaObservationBehavior = _ => Task.FromResult(new AccountQuotaObservation(
            _adapter.Identity, _adapter.Identity?.Email == _source!.Email
                ? EvidenceQuota(fraction) : _adapter.TargetQuota));
    }

    private async Task ChangePreparedRoutingConfigAsync(
        NativeAutoRouter router, bool atStopBoundary, Action update)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Pause()
        {
            entered.TrySetResult();
            await resume.Task;
        }

        if (atStopBoundary) _process.BeforeStopProof = Pause;
        else router.BeforeGatePublicationAsync = Pause;
        var evaluation = router.EvaluateCycleAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("PROCESS_STOP", _globalTimeline);
            update();
        }
        finally
        {
            resume.TrySetResult();
            await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
            _process.BeforeStopProof = null;
            router.BeforeGatePublicationAsync = null;
        }
    }

    private async Task AssertPreparedRoutingDidNotMutateAsync(NativeAutoRouter router)
    {
        Assert.Equal(0, _process.LaunchCount);
        Assert.Equal(0, _process.RestoreCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.DoesNotContain("PROCESS_STOP", _globalTimeline);
        Assert.DoesNotContain(_globalTimeline, entry => entry.StartsWith("WINCRED_WRITE:", StringComparison.Ordinal));
        Assert.Equal(_source!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal(RoutingSafetyGateState.Idle, router.GetStatus().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutingConfig_LoweredThresholdRejectsPreparedDecisionBeforeMutation(bool atStopBoundary)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        SetRoutingSourceQuota(0.10);
        await using var router = EvidenceRouter(store, clock);

        await ChangePreparedRoutingConfigAsync(router, atStopBoundary,
            () => router.UpdateConfig(router.GetConfig() with { LowQuotaThresholdPercent = 5 }));

        await AssertPreparedRoutingDidNotMutateAsync(router);
        var reevaluated = await router.EvaluateCycleAsync();
        Assert.False(reevaluated.ShouldSwitch);
        Assert.Contains("healthy", reevaluated.Reason);
        await AssertPreparedRoutingDidNotMutateAsync(router);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutingConfig_StillLowAfterChangeRequiresFreshDecisionThenSwitches(bool atStopBoundary)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        SetRoutingSourceQuota(0.10);
        await using var router = EvidenceRouter(store, clock);

        await ChangePreparedRoutingConfigAsync(router, atStopBoundary,
            () => router.UpdateConfig(router.GetConfig() with { LowQuotaThresholdPercent = 12 }));

        await AssertPreparedRoutingDidNotMutateAsync(router);
        Assert.True((await router.EvaluateCycleAsync()).ShouldSwitch);
        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.Equal(_target!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("workload")]
    [InlineData("polling")]
    [InlineData("disabled")]
    public async Task RoutingConfig_OtherDecisionInputsAlsoRejectPreparedDecision(string change)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        SetRoutingSourceQuota(0.10);
        await using var router = EvidenceRouter(store, clock);

        await ChangePreparedRoutingConfigAsync(router, atStopBoundary: true, () =>
        {
            var config = router.GetConfig();
            router.UpdateConfig(change switch
            {
                "minimum" => config with { MinimumCandidateQuotaPercent = 40 },
                "workload" => config with { WorkloadModelKey = "other-model" },
                "polling" => config with { PollingIntervalMs = 15000 },
                "disabled" => config with { AutoSwitchEnabled = false },
                _ => throw new ArgumentException("Unknown synthetic configuration change.", nameof(change))
            });
        });

        await AssertPreparedRoutingDidNotMutateAsync(router);
    }

    [Fact]
    public async Task RoutingConfig_IdenticalSaveDoesNotInvalidatePreparedDecision()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        SetRoutingSourceQuota(0.10);
        await using var router = EvidenceRouter(store, clock);

        await ChangePreparedRoutingConfigAsync(router, atStopBoundary: true,
            () => router.UpdateConfig(router.GetConfig() with { }));

        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.Equal(_target!.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Theory]
    [InlineData(0.15, true)]
    [InlineData(0.150001, false)]
    public async Task RoutingConfig_SourceThresholdEqualityRemainsEligible(double sourceQuota, bool shouldSwitch)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        SetRoutingSourceQuota(sourceQuota);
        await using var router = EvidenceRouter(store, clock);

        var decision = await router.EvaluateCycleAsync();

        Assert.Equal(shouldSwitch, decision.ShouldSwitch);
        Assert.Equal(shouldSwitch ? 1 : 0, _process.LaunchCount);
        Assert.Equal(shouldSwitch ? 1 : 0, _accounts.FinalizeCalls);
    }

    [Fact]
    public async Task RoutingConfig_ChangeDuringManualAdmissionDoesNotApplyAutomaticPressureRules()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        SetRoutingSourceQuota(0.90);
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        await using var router = new NativeAutoRouter(_accounts, _vault, _adapter, coordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store, timeProvider: clock);
        _process.BeforeStopProof = () =>
        {
            router.UpdateConfig(router.GetConfig() with { LowQuotaThresholdPercent = 5 });
            return Task.CompletedTask;
        };

        var token = router.NotifyManualSwitchStarted(_target!.Id);
        var result = await coordinator.SwitchAsync(_target.Id);
        await router.NotifyManualSwitchCompletedAsync(token, result);

        Assert.True(result.Success);
        Assert.DoesNotContain("TARGET_QUOTA_VERIFIED", result.StagesCompleted);
        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.Equal(_target.Id, router.GetStatus().ActiveAccountId);
        Assert.Equal(RoutingSafetyGateState.Cooldown, router.GetStatus().State);
    }
}
