using System.IO;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Usage;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Aggregation semantics over the durable usage ledger: scopes, headline accounting, cache
/// separation, model breakdown, observation-time buckets, and overflow safety.
/// </summary>
public class UsageAggregationServiceTests
{
    private static readonly DateTimeOffset October = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ag2_u3_{Guid.NewGuid():N}");

        public TestTempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    private static UsageCallRecord Call(
        string cascadeId,
        long input,
        long output,
        long? cache = null,
        long? responseOut = null,
        long? thinkingOut = null,
        string? model = "claude-sonnet-4-5",
        string? accountId = "acc_a",
        DateTimeOffset? observed = null,
        UsageTimeAttribution timeBasis = UsageTimeAttribution.ObservationTime) =>
        new(
            UsageCallKeys.Compute(cascadeId, "response-1"),
            UsageCallKeys.CanonicalizeResponseModel(model),
            null,
            input,
            output,
            responseOut,
            thinkingOut,
            cache,
            observed ?? October,
            timeBasis,
            accountId is null ? UsageAccountAttributionBasis.Unattributed : UsageAccountAttributionBasis.VerifiedObservation,
            accountId,
            "SyntheticTest");

    private sealed record Fixture : IDisposable
    {
        public TestTempDirectory Temp = new();
        public DurableUsageCallLedger Ledger { get; } = default!;
        public UsageAggregationService Aggregation { get; }

        public Fixture()
        {
            Ledger = new DurableUsageCallLedger(System.IO.Path.Combine(Temp.Path, "usage"));
            Aggregation = new UsageAggregationService(Ledger);
        }

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public async Task L1_AllAccounts_IncludesAttributedAndUnattributed()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([
            Call("a", 100, 40, accountId: "acc_a"),
            Call("b", 10, 5, accountId: null),
        ]);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.All);

        Assert.Equal(2, summary.Totals.Calls);
        Assert.Equal(155, summary.Totals.ConversationTokens);
        Assert.NotNull(summary.Unattributed);
        Assert.Equal(1, summary.Unattributed.Calls);
        Assert.Equal(15, summary.Unattributed.ConversationTokens);
    }

    [Fact]
    public async Task L2_AccountScope_IncludesOnlyThatAccount()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([
            Call("a", 100, 40, accountId: "acc_a"),
            Call("b", 999, 999, accountId: "acc_b"),
        ]);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.ForAccount("acc_a"));

        Assert.Equal(1, summary.Totals.Calls);
        Assert.Equal(140, summary.Totals.ConversationTokens);
    }

    [Fact]
    public async Task L3_UnattributedScope_IsExact()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([
            Call("a", 100, 40, accountId: "acc_a"),
            Call("b", 10, 5, accountId: null),
        ]);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.Unattributed);

        Assert.Equal(1, summary.Totals.Calls);
        Assert.Equal(15, summary.Totals.ConversationTokens);
        Assert.Null(summary.Unattributed);
    }

    [Fact]
    public async Task L4_ConversationTokens_ExcludeCache()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([Call("a", 3372, 500, cache: 16307)]);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.All);

        Assert.Equal(3872, summary.Totals.ConversationTokens);
        Assert.Equal(16307, summary.Totals.CacheReadTokens);
    }

    [Fact]
    public async Task L5_L6_CacheReportingCoverage_Preserved()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([
            Call("a", 100, 40, cache: 500),
            Call("b", 10, 5, cache: null),
        ]);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.All);

        Assert.Equal(500, summary.Totals.CacheReadTokens);
        Assert.Equal(1, summary.Totals.CacheReportedCalls);
        Assert.Equal(1, summary.Totals.CacheUnknownCalls);
    }

    [Fact]
    public async Task L7_L8_ModelGrouping_Canonical_WithExplicitUnknownBucket()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([
            Call("a", 100, 40, model: " Claude-Sonnet-4-5 "),
            Call("b", 10, 5, model: null),
        ]);

        var breakdown = await fixture.Aggregation.GetModelBreakdownAsync(UsageScope.All);

        Assert.Equal(2, breakdown.Models.Count);
        var known = breakdown.Models.Single(static model => !model.IsUnknownModel);
        Assert.Equal("claude-sonnet-4-5", known.ModelKey);
        Assert.Equal(140, known.ConversationTokens);
        var unknown = breakdown.Models.Single(static model => model.IsUnknownModel);
        Assert.Null(unknown.ModelKey);
        Assert.Equal(15, unknown.ConversationTokens);
    }

    [Fact]
    public async Task L9_HistoricalUnknown_InTotals_ExcludedFromTimeline()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([
            Call("hist", 100, 40, timeBasis: UsageTimeAttribution.HistoricalUnknown),
            Call("fwd", 10, 5, timeBasis: UsageTimeAttribution.ObservationTime),
        ]);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.All);
        Assert.Equal(2, summary.Totals.Calls);
        Assert.Equal(1, summary.HistoricalUnknown.Calls);
        Assert.Equal(140, summary.HistoricalUnknown.ConversationTokens);

        var timeseries = await fixture.Aggregation.GetTimeSeriesAsync(UsageScope.All, "7d");
        Assert.Equal(1, summary.HistoricalUnknown.Calls);
        Assert.Single(timeseries.Buckets);
        Assert.Equal(15, timeseries.Buckets[0].ConversationTokens);
        Assert.Equal(1, timeseries.HistoricalUnknown.Calls);
    }

    [Fact]
    public async Task L10_ObservationTime_BucketsAtUtcBoundaries()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([
            Call("h1", 1, 1, observed: new DateTimeOffset(2026, 10, 4, 23, 59, 0, TimeSpan.Zero)),
            Call("h2", 2, 2, observed: new DateTimeOffset(2026, 10, 5, 0, 1, 0, TimeSpan.Zero)),
        ]);

        var timeseries = await fixture.Aggregation.GetTimeSeriesAsync(UsageScope.All, "7d");

        Assert.Equal(2, timeseries.Buckets.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero), timeseries.Buckets[0].BucketStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), timeseries.Buckets[1].BucketStartUtc);
    }

    [Fact]
    public async Task L11_EmptyLedger_YieldsHonestZeros()
    {
        using var fixture = new Fixture();

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.All);
        var timeseries = await fixture.Aggregation.GetTimeSeriesAsync(UsageScope.All, "24h");
        var breakdown = await fixture.Aggregation.GetModelBreakdownAsync(UsageScope.All);

        Assert.Equal(0, summary.Totals.Calls);
        Assert.Empty(summary.Accounts);
        Assert.Empty(timeseries.Buckets);
        Assert.Empty(breakdown.Models);
    }

    [Fact]
    public async Task L12_LargeTotals_AccumulateExact_WithoutOverflowAtRealisticScale()
    {
        using var fixture = new Fixture();
        var calls = new List<UsageCallRecord>();
        for (int i = 0; i < 1000; i++)
        {
            calls.Add(Call($"c{i}", input: 1_000_000_000, output: 1_000_000_000, cache: 500_000_000));
        }
        await fixture.Ledger.IngestBatchAsync(calls);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.All);

        Assert.Equal(1000, summary.Totals.Calls);
        Assert.Equal(2_000_000_000_000, summary.Totals.ConversationTokens);
        Assert.Equal(1_000_000_000_000, summary.Totals.InputTokens);
        Assert.Equal(500_000_000_000, summary.Totals.CacheReadTokens);
    }

    [Fact]
    public async Task L13_DeletedAccountHistoricalId_RemainsQueryable()
    {
        using var fixture = new Fixture();
        await fixture.Ledger.IngestBatchAsync([Call("a", 100, 40, accountId: "acc_gone00000")]);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.ForAccount("acc_gone00000"));

        Assert.Equal(1, summary.Totals.Calls);
        Assert.Equal(140, summary.Totals.ConversationTokens);
    }

    [Fact]
    public async Task L14_OutputMismatch_CountedWithoutChangingHeadline()
    {
        using var fixture = new Fixture();
        // Provider output 500, components 400 + 80 = 480: mismatch is preserved, never repaired.
        await fixture.Ledger.IngestBatchAsync([
            Call("a", 100, 500, responseOut: 400, thinkingOut: 80),
        ]);

        var summary = await fixture.Aggregation.GetSummaryAsync(UsageScope.All);

        Assert.Equal(1, summary.Totals.OutputMismatchCalls);
        Assert.Equal(600, summary.Totals.ConversationTokens);
        Assert.Equal(500, summary.Totals.OutputTokens);
    }

    [Fact]
    public async Task InvalidScope_Throws()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Aggregation.GetSummaryAsync(UsageScope.ForAccount("   ")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Aggregation.GetTimeSeriesAsync(UsageScope.All, "48h"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Aggregation.GetTimeSeriesAsync(UsageScope.ForAccount("x"), "not-a-range"));
    }
}
