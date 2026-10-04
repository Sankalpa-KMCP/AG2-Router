using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Usage;
using AG2Router.App.Server;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Native usage HTTP API: summary/timeseries/models over a real loopback server, scope and
/// range validation, sanitized integrity errors, privacy, and honest zeros.
/// </summary>
public class LoopbackUsageApiTests
{
    private static readonly DateTimeOffset October = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ag2_u3api_{Guid.NewGuid():N}");

        public TestTempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    private static UsageCallRecord Call(string cascadeId, long input, long output, string? accountId = "acc_a", long? cache = null) =>
        new(
            UsageCallKeys.Compute(cascadeId, "response-1"),
            "claude-sonnet-4-5",
            null,
            input,
            output,
            null, null,
            cache,
            October,
            UsageTimeAttribution.ObservationTime,
            accountId is null ? UsageAccountAttributionBasis.Unattributed : UsageAccountAttributionBasis.VerifiedObservation,
            accountId,
            "CascadeConversationCall");

    private sealed class FailingLedger : IUsageCallLedger
    {
        public string LedgerDirectoryPath => string.Empty;

        public Task<UsageIngestResult> IngestBatchAsync(IReadOnlyList<UsageCallRecord> calls, CancellationToken cancellationToken = default) =>
            throw new UsageLedgerCorruptionException("synthetic corruption");

        public Task<IReadOnlyList<UsageCallRecord>> GetAllCallsAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<UsageCallRecord>>(new UsageLedgerCorruptionException("synthetic corruption"));

        public Task<IReadOnlyList<UsageCallRecord>> GetCallsByAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<UsageCallRecord>>(new UsageLedgerCorruptionException("synthetic corruption"));

        public Task<IReadOnlyList<UsageCallRecord>> GetUnattributedCallsAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<UsageCallRecord>>(new UsageLedgerCorruptionException("synthetic corruption"));

        public Task<IReadOnlyList<UsageCallRecord>> GetObservationTimeCallsBetweenAsync(DateTimeOffset fromUtcInclusive, DateTimeOffset toUtcExclusive, CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<UsageCallRecord>>(new UsageLedgerCorruptionException("synthetic corruption"));

        public Task<IReadOnlyList<UsageCallRecord>> GetCallsByModelAsync(string responseModelKey, CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<UsageCallRecord>>(new UsageLedgerCorruptionException("synthetic corruption"));
    }

    [Fact]
    public async Task Summary_ReturnsTotalsCollector_AndAccountBreakdown()
    {
        using var temp = new TestTempDirectory();
        var ledger = new DurableUsageCallLedger(Path.Combine(temp.Path, "usage"));
        await ledger.IngestBatchAsync([
            Call("a", 3372, 500, accountId: "acc_a", cache: 16307),
            new UsageCallRecord(
                UsageCallKeys.Compute("b", "response-1"), "claude-sonnet-4-5", null, 10, 5,
                null, null, null, October, UsageTimeAttribution.HistoricalUnknown,
                UsageAccountAttributionBasis.Unattributed, null, "CascadeConversationCall"),
        ]);
        var aggregation = new UsageAggregationService(ledger);
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0, usageAggregation: aggregation,
                usageCollectorStatusProvider: () => new UsageCollectorDiagnostics
                {
                    BaselineEstablished = true,
                    InstancesDiscovered = 2,
                    InstancesHealthy = 2,
                });
            using var client = new HttpClient();

            var response = await client.GetAsync($"{server.BoundUrl}/api/usage/summary?scope=all");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var summary = json.RootElement.GetProperty("summary");
            Assert.Equal(2, summary.GetProperty("totals").GetProperty("calls").GetInt32());
            Assert.Equal(3887, summary.GetProperty("totals").GetProperty("conversationTokens").GetInt32());
            Assert.Equal(16307, summary.GetProperty("totals").GetProperty("cacheReadTokens").GetInt32());
            Assert.Equal(1, summary.GetProperty("historicalUnknown").GetProperty("calls").GetInt32());
            Assert.Equal(2, summary.GetProperty("accounts").GetArrayLength());
            Assert.True(summary.GetProperty("accounts")[0].GetProperty("isUnattributed").GetBoolean()
                || summary.GetProperty("accounts")[1].GetProperty("isUnattributed").GetBoolean());
            var collector = json.RootElement.GetProperty("collector");
            Assert.True(collector.GetProperty("baselineEstablished").GetBoolean());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Timeseries_AndModels_ReturnBucketsAndBreakdown()
    {
        using var temp = new TestTempDirectory();
        var ledger = new DurableUsageCallLedger(Path.Combine(temp.Path, "usage"));
        await ledger.IngestBatchAsync([Call("a", 100, 40)]);
        var aggregation = new UsageAggregationService(ledger);
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0, usageAggregation: aggregation);
            using var client = new HttpClient();

            var timeseries = await client.GetAsync($"{server.BoundUrl}/api/usage/timeseries?range=7d&scope=all");
            Assert.Equal(HttpStatusCode.OK, timeseries.StatusCode);
            using var tsJson = JsonDocument.Parse(await timeseries.Content.ReadAsStringAsync());
            Assert.Equal("7d", tsJson.RootElement.GetProperty("timeseries").GetProperty("range").GetString());
            Assert.Single(tsJson.RootElement.GetProperty("timeseries").GetProperty("buckets").EnumerateArray());

            var models = await client.GetAsync($"{server.BoundUrl}/api/usage/models?scope=all");
            Assert.Equal(HttpStatusCode.OK, models.StatusCode);
            using var modelsJson = JsonDocument.Parse(await models.Content.ReadAsStringAsync());
            Assert.Single(modelsJson.RootElement.GetProperty("models").GetProperty("models").EnumerateArray());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AccountScope_QueriesWork()
    {
        using var temp = new TestTempDirectory();
        var ledger = new DurableUsageCallLedger(Path.Combine(temp.Path, "usage"));
        await ledger.IngestBatchAsync([
            Call("a", 100, 40, accountId: "acc_a"),
            Call("b", 999, 999, accountId: "acc_b"),
        ]);
        var aggregation = new UsageAggregationService(ledger);
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0, usageAggregation: aggregation);
            using var client = new HttpClient();

            var response = await client.GetAsync($"{server.BoundUrl}/api/usage/summary?scope=account:acc_a");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("totals").GetProperty("calls").GetInt32());

            var unattributed = await client.GetAsync($"{server.BoundUrl}/api/usage/summary?scope=unattributed");
            using var uaJson = JsonDocument.Parse(await unattributed.Content.ReadAsStringAsync());
            Assert.Equal(0, uaJson.RootElement.GetProperty("summary").GetProperty("totals").GetProperty("calls").GetInt32());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidScope_AndRange_Rejected()
    {
        using var temp = new TestTempDirectory();
        var aggregation = new UsageAggregationService(new DurableUsageCallLedger(Path.Combine(temp.Path, "usage")));
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0, usageAggregation: aggregation);
            using var client = new HttpClient();

            var badScope = await client.GetAsync($"{server.BoundUrl}/api/usage/summary?scope=everyone");
            Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);

            var badAccount = await client.GetAsync($"{server.BoundUrl}/api/usage/summary?scope=account:");
            Assert.Equal(HttpStatusCode.BadRequest, badAccount.StatusCode);

            var traversalAccount = await client.GetAsync($"{server.BoundUrl}/api/usage/summary?scope=account:..%2Fx");
            Assert.Equal(HttpStatusCode.BadRequest, traversalAccount.StatusCode);

            var badRange = await client.GetAsync($"{server.BoundUrl}/api/usage/timeseries?range=48h&scope=all");
            Assert.Equal(HttpStatusCode.BadRequest, badRange.StatusCode);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task CorruptLedger_ReturnsSanitized503_WithoutInternalDetail()
    {
        using var temp = new TestTempDirectory();
        var aggregation = new UsageAggregationService(new FailingLedger());
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0, usageAggregation: aggregation);
            using var client = new HttpClient();

            foreach (var path in new[] { "/api/usage/summary?scope=all", "/api/usage/timeseries?range=7d&scope=all", "/api/usage/models?scope=all" })
            {
                var response = await client.GetAsync($"{server.BoundUrl}{path}");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("synthetic", body, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("integrity", body, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ApiResponses_NeverExpose_RawCallIdentifiers_OrPromptContent()
    {
        using var temp = new TestTempDirectory();
        var ledger = new DurableUsageCallLedger(Path.Combine(temp.Path, "usage"));
        // Raw ids only exist inside the hashed key; the sentinel below must not leak.
        var call = new UsageCallRecord(
            UsageCallKeys.Compute("cascade-leak-check", "response-leak-check"),
            "claude-sonnet-4-5", null, 10, 5, null, null, null,
            October, UsageTimeAttribution.ObservationTime,
            UsageAccountAttributionBasis.VerifiedObservation, "acc_a", "CascadeConversationCall");
        await ledger.IngestBatchAsync([call]);
        var aggregation = new UsageAggregationService(ledger);
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0, usageAggregation: aggregation);
            using var client = new HttpClient();

            foreach (var path in new[] { "/api/usage/summary", "/api/usage/models" })
            {
                var body = await client.GetStringAsync($"{server.BoundUrl}{path}?scope=all");
                Assert.DoesNotContain("cascade-leak-check", body, StringComparison.Ordinal);
                Assert.DoesNotContain("response-leak-check", body, StringComparison.Ordinal);
                Assert.DoesNotContain("cascadeId", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("responseId", body, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task PersistedTotals_Viewable_WithoutCollector()
    {
        using var temp = new TestTempDirectory();
        var ledger = new DurableUsageCallLedger(Path.Combine(temp.Path, "usage"));
        await ledger.IngestBatchAsync([Call("a", 100, 40)]);
        var aggregation = new UsageAggregationService(ledger);
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(0, usageAggregation: aggregation,
                usageCollectorStatusProvider: () => null);
            using var client = new HttpClient();

            var response = await client.GetAsync($"{server.BoundUrl}/api/usage/summary");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("totals").GetProperty("calls").GetInt32());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
}
