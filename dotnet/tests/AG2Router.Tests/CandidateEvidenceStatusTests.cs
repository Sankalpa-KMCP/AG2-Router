using AG2Router.AG2.Routing;
using AG2Router.AG2.Persistence;
using System.IO;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed partial class TargetQuotaVerificationSwitchTests
{
    [Theory]
    [InlineData(0.8, 119, "USABLE", true)]
    [InlineData(0.8, 120, "STALE", false)]
    [InlineData(0.8, -1, "INVALID", false)]
    [InlineData(null, 1, "UNKNOWN", false)]
    [InlineData(0.0, 1, "EXHAUSTED", true)]
    [InlineData(0.2, 1, "BELOW_MINIMUM", true)]
    public async Task CandidateEvidenceStatus_UsesDurableRoutingTruth(double? fraction, int ageMinutes, string expected, bool numeric)
    {
        await SeedAccountsAsync();
        var clock = new EvidenceTestClock();
        var store = new DurableQuotaObservationStore(Path.Combine(_tempDir, "status-observations.json"));
        await store.RecordObservationsAsync(_target!.Id,
            [new(_target.Id, EvidenceModel, fraction, "2000-01-01T00:00:00Z", clock.Now.AddMinutes(-ageMinutes), "synthetic")]);
        await using var router = new NativeAutoRouter(_accounts, _vault, _adapter, CreateCoordinator(),
            new RouterConfigDto(WorkloadModelKey: EvidenceModel), quotaObservationStore: store, timeProvider: clock);
        var status = await router.GetCandidateEvidenceStatusAsync();
        Assert.True(status.Available);
        Assert.Equal(EvidenceModel, status.ModelKey);
        var row = Assert.Single(status.Candidates);
        Assert.Equal(_target.Id, row.AccountId);
        Assert.Equal(expected, row.State);
        Assert.Equal(numeric ? fraction : null, row.RemainingFraction);
        Assert.Equal(ageMinutes * 60, row.AgeSeconds);
        Assert.Equal(0, _process.LaunchCount);
    }

    [Fact]
    public async Task CandidateEvidenceStatus_DoesNotUseWrongModelOrFabricateMissingObservation()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouter(store, clock);
        router.UpdateConfig(new RouterConfigDto(WorkloadModelKey: "different-model"));
        Assert.Equal("NOT_OBSERVED", Assert.Single((await router.GetCandidateEvidenceStatusAsync()).Candidates).State);
        router.UpdateConfig(new RouterConfigDto());
        Assert.Equal("UNCONFIGURED", Assert.Single((await router.GetCandidateEvidenceStatusAsync()).Candidates).State);
    }
}
