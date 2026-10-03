using System.IO;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Switching;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed partial class TargetQuotaVerificationSwitchTests
{
    private static TaskCompletionSource AdmissionBarrier() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RouterConfigDto AdmissionUpdate(RouterConfigDto config, string change) => change switch
    {
        "disabled" => config with { AutoSwitchEnabled = false },
        "threshold" => config with { LowQuotaThresholdPercent = 5 },
        "minimum" => config with { MinimumCandidateQuotaPercent = 90 },
        "workload" => config with { WorkloadModelKey = "other-model" },
        "polling" => config with { PollingIntervalMs = 15000 },
        "identical" => config with { },
        _ => throw new ArgumentException("Unknown synthetic configuration change.", nameof(change))
    };

    [Theory]
    [InlineData("disabled")]
    [InlineData("threshold")]
    [InlineData("minimum")]
    [InlineData("workload")]
    [InlineData("polling")]
    [InlineData("identical")]
    public async Task RoutingAdmission_ConfigSaveAfterSuccessfulProof_WaitsForInterruptionButNotProcessExit(string change)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        SetRoutingSourceQuota(0.10);
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        string configPath = Path.Combine(_tempDir, "routing-config.json");
        await using var router = new NativeAutoRouter(_accounts, _vault, _adapter, coordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            configFilePath: configPath, quotaObservationStore: store, timeProvider: clock);
        var original = router.UpdateConfig(router.GetConfig());
        string originalBytes = File.ReadAllText(configPath);
        var update = AdmissionUpdate(original, change);
        var proofSucceeded = AdmissionBarrier();
        var resumeProof = AdmissionBarrier();
        var configContended = AdmissionBarrier();
        var stopIssued = AdmissionBarrier();
        var resumeExit = AdmissionBarrier();
        _process.AfterStopProof = async () =>
        {
            proofSucceeded.TrySetResult();
            await resumeProof.Task;
        };
        _process.AfterStopIssued = async () =>
        {
            stopIssued.TrySetResult();
            await resumeExit.Task;
        };
        router.ConfigAdmissionContended = () => configContended.TrySetResult();
        Task<RouterConfigDto>? saving = null;
        var evaluation = router.EvaluateCycleAsync();
        try
        {
            await proofSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("PROCESS_STOP", _globalTimeline);
            saving = Task.Run(() => router.UpdateConfig(update));
            // This notification follows a failed nonblocking acquisition, not a scheduling delay.
            await configContended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(saving.IsCompleted);
            Assert.Equal(original, router.GetConfig());
            Assert.Equal(originalBytes, File.ReadAllText(configPath));
            Assert.Equal(0, _accounts.FinalizeCalls);
            Assert.DoesNotContain(_globalTimeline, entry => entry.StartsWith("WINCRED_WRITE:"));

            resumeProof.TrySetResult();
            await stopIssued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("PROCESS_STOP", _globalTimeline);
            Assert.Equal(update, await saving.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(update, router.GetConfig());
            Assert.Equal(update, JsonSerializer.Deserialize<RouterConfigDto>(File.ReadAllText(configPath)));
            Assert.False(evaluation.IsCompleted);
            Assert.Equal(0, _accounts.FinalizeCalls);
            Assert.DoesNotContain(_globalTimeline, entry => entry.StartsWith("WINCRED_WRITE:"));
        }
        finally
        {
            resumeProof.TrySetResult();
            resumeExit.TrySetResult();
            if (saving != null) await saving.WaitAsync(TimeSpan.FromSeconds(5));
            await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(coordinator.GetStatus().LastResult!.Success);
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.Equal(_target!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.Equal(update, router.GetConfig());
    }

    [Fact]
    public async Task RoutingAdmission_CancellationWhileWaiting_ReleasesSwitchOwnershipWithoutMutation()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouter(store, clock);
        using var admission = await router.AcquireInterruptionAdmissionAsync(CancellationToken.None);
        var waiting = AdmissionBarrier();
        router.AutomaticAdmissionContended = () => waiting.TrySetResult();
        using var cancellation = new CancellationTokenSource();
        var evaluation = router.EvaluateCycleAsync(cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertPreparedRoutingDidNotMutateAsync(router);
        Assert.False(File.Exists(_journal.JournalFilePath));
        admission.Dispose();
        await Task.Run(() => router.UpdateConfig(router.GetConfig() with { LowQuotaThresholdPercent = 12 }))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await router.EvaluateCycleAsync()).ShouldSwitch);
        Assert.Equal(1, _accounts.FinalizeCalls);
    }

    [Fact]
    public async Task RoutingAdmission_ProcessGenerationFailureAfterProof_ReleasesWaitingConfigSave()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouter(store, clock);
        var proofSucceeded = AdmissionBarrier();
        var failRefresh = AdmissionBarrier();
        var configContended = AdmissionBarrier();
        _process.AfterStopProof = async () =>
        {
            proofSucceeded.TrySetResult();
            await failRefresh.Task;
            throw new AG2ProcessLifecycleException("Synthetic generation changed after final proof.");
        };
        router.ConfigAdmissionContended = () => configContended.TrySetResult();
        var updated = router.GetConfig() with { AutoSwitchEnabled = false };
        var evaluation = router.EvaluateCycleAsync();
        Task<RouterConfigDto>? saving = null;
        try
        {
            await proofSucceeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            saving = Task.Run(() => router.UpdateConfig(updated));
            await configContended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(saving.IsCompleted);
        }
        finally
        {
            failRefresh.TrySetResult();
            await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
            if (saving != null) Assert.Equal(updated, await saving.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await AssertPreparedRoutingDidNotMutateAsync(router);
        Assert.Equal(updated, router.GetConfig());
    }

    [Fact]
    public async Task RoutingAdmission_PersistenceFailure_ReleasesAdmissionAndPreservesPublishedConfig()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var original = new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel);
        var writer = new AdmissionFailingWriter();
        await using var router = new NativeAutoRouter(_accounts, _vault, _adapter,
            CreateCoordinator(quotaObservationStore: store, timeProvider: clock), original, null,
            Path.Combine(_tempDir, "config.json"), writer, store, clock);
        Assert.Throws<IOException>(() => router.UpdateConfig(original with { AutoSwitchEnabled = false }));
        Assert.Equal(original, router.GetConfig());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (await router.AcquireInterruptionAdmissionAsync(cancellation.Token)) { }
        writer.Fail = false;
        var updated = original with { LowQuotaThresholdPercent = 12 };
        Assert.Equal(updated, await Task.Run(() => router.UpdateConfig(updated)).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RoutingAdmission_ManualSwitch_DoesNotAcquireAutomaticGuard()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var coordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        await using var router = new NativeAutoRouter(_accounts, _vault, _adapter, coordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store, timeProvider: clock);
        using var admission = await router.AcquireInterruptionAdmissionAsync(CancellationToken.None);
        router.AutomaticAdmissionContended = () => throw new InvalidOperationException("Manual switch entered automatic admission.");
        var result = await coordinator.SwitchAsync(_target!.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.DoesNotContain("TARGET_QUOTA_VERIFIED", result.StagesCompleted);
        Assert.Equal(1, _accounts.FinalizeCalls);
    }

    private sealed class AdmissionFailingWriter : IDurableFileWriter
    {
        public bool Fail { get; set; } = true;
        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken) =>
            Fail ? Task.FromException(new IOException("Synthetic config write failure.")) : Task.CompletedTask;
    }
}
