using System.IO;
using AG2Router.AG2.Persistence;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Durable usage-ledger tests: idempotent insertion, conflict fail-closed semantics, batch
/// atomicity, restart durability, corruption and version fail-closed behavior, concurrency,
/// cancellation, privacy, removed-account durability, and scale. All state lives in
/// synthetic temporary directories; no live Antigravity or user data is touched.
/// </summary>
public class UsageCallLedgerTests
{
    private static readonly DateTimeOffset October = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset September = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset November = new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);

    private sealed class TestTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ag2_test_usage_{Guid.NewGuid():N}");

        public TestTempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors on temp dir
            }
        }
    }

    private sealed class ThrowingFileWriter : IDurableFileWriter
    {
        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken) =>
            throw new OperationCanceledException("synthetic durable write failure");
    }

    private sealed class ThrowingLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new IOException("synthetic lease disposal failure");
    }

    private static UsageCallRecord Call(
        string cascadeId,
        long inputTokens = 100,
        long outputTokens = 40,
        long? cacheReadTokens = null,
        DateTimeOffset? firstObservedAtUtc = null,
        UsageTimeAttribution timeAttribution = UsageTimeAttribution.ObservationTime,
        UsageAccountAttributionBasis accountAttributionBasis = UsageAccountAttributionBasis.VerifiedObservation,
        string? accountId = "acc_synthetic01",
        string? responseModel = "claude-sonnet-4-5")
    {
        long? responseOutput = outputTokens >= 10 ? outputTokens - 10 : 0;
        long? thinkingOutput = outputTokens >= 10 ? 10 : outputTokens;
        return new UsageCallRecord(
            UsageCallKeys.Compute(cascadeId, "response-1"),
            UsageCallKeys.CanonicalizeResponseModel(responseModel),
            "anthropic",
            inputTokens,
            outputTokens,
            responseOutputTokens: responseOutput,
            thinkingOutputTokens: thinkingOutput,
            cacheReadTokens,
            firstObservedAtUtc ?? October,
            timeAttribution,
            accountAttributionBasis,
            accountId,
            "SyntheticTest");
    }

    private static DurableUsageCallLedger CreateLedger(TestTempDirectory temp) =>
        new(System.IO.Path.Combine(temp.Path, "usage"));

    private static string SegmentPathOf(TestTempDirectory temp, string segmentId) =>
        System.IO.Path.Combine(temp.Path, "usage", $"usage-calls-{segmentId}.json");

    #region G. Insert

    [Fact]
    public async Task Insert_NewCall_PersistsAndRoundTripsExactly()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var call = Call("cascade-g1", inputTokens: 3372, outputTokens: 500, cacheReadTokens: 16307);

        var result = await ledger.IngestBatchAsync([call]);

        Assert.Equal(1, result.InsertedCount);
        Assert.Equal(0, result.DuplicateCount);
        var all = await ledger.GetAllCallsAsync();
        var record = Assert.Single(all);
        Assert.Equal(call, record);
        Assert.Equal(3872, record.ConversationTokens);
        Assert.True(File.Exists(SegmentPathOf(temp, "2026-10")));
    }

    [Fact]
    public async Task Insert_PartitionsByObservationMonth_WithoutConfusingPartitionWithEventTime()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);

        await ledger.IngestBatchAsync([
            Call("cascade-sep", firstObservedAtUtc: September),
            Call("cascade-oct", firstObservedAtUtc: October),
        ]);

        Assert.True(File.Exists(SegmentPathOf(temp, "2026-09")));
        Assert.True(File.Exists(SegmentPathOf(temp, "2026-10")));
        Assert.False(File.Exists(SegmentPathOf(temp, "2026-11")));
        Assert.Equal(2, (await ledger.GetAllCallsAsync()).Count);
    }

    #endregion

    #region H. Duplicate identical insert

    [Fact]
    public async Task DuplicateIdenticalInsert_IsIdempotentNoOp_WithStableBytes()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var original = Call("cascade-h1");
        await ledger.IngestBatchAsync([original]);
        string segmentPath = SegmentPathOf(temp, "2026-10");
        byte[] bytesBefore = await File.ReadAllBytesAsync(segmentPath);

        // Same call key, same accounting payload, different observation instant.
        var reObserved = Call("cascade-h1", firstObservedAtUtc: October.AddHours(3));
        var result = await ledger.IngestBatchAsync([reObserved]);

        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(segmentPath));
        var all = await ledger.GetAllCallsAsync();
        var record = Assert.Single(all);
        Assert.Equal(original, record);
    }

    [Fact]
    public async Task CrossSegmentDuplicate_DoesNotCreateSecondRecord_OrSecondSegment()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var septemberCall = Call("cascade-h2", firstObservedAtUtc: September);
        await ledger.IngestBatchAsync([septemberCall]);
        byte[] septemberBytes = await File.ReadAllBytesAsync(SegmentPathOf(temp, "2026-09"));

        // The same call is re-observed two months later under an identical payload.
        var result = await ledger.IngestBatchAsync([
            Call("cascade-h2", firstObservedAtUtc: November),
        ]);

        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(septemberBytes, await File.ReadAllBytesAsync(SegmentPathOf(temp, "2026-09")));
        Assert.False(File.Exists(SegmentPathOf(temp, "2026-11")));
        var record = Assert.Single(await ledger.GetAllCallsAsync());
        Assert.Equal(September, record.FirstObservedAtUtc);
    }

    #endregion

    #region I. Duplicate conflicting insert

    [Fact]
    public async Task ConflictingInsert_FailsClosed_AndPreservesOriginalRecord()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var original = Call("cascade-i1", inputTokens: 100);
        await ledger.IngestBatchAsync([original]);
        string segmentPath = SegmentPathOf(temp, "2026-10");
        byte[] bytesBefore = await File.ReadAllBytesAsync(segmentPath);

        var conflicting = Call("cascade-i1", inputTokens: 999);
        var conflict = await Assert.ThrowsAsync<UsageLedgerConflictException>(
            () => ledger.IngestBatchAsync([conflicting]));

        Assert.Equal(original.CallKey, conflict.CallKey);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(segmentPath));
        var record = Assert.Single(await ledger.GetAllCallsAsync());
        Assert.Equal(100, record.InputTokens);
    }

    [Fact]
    public async Task ConflictingAttribution_IsAConflict_NotASilentUpgrade()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var unattributed = Call("cascade-i2",
            accountAttributionBasis: UsageAccountAttributionBasis.Unattributed, accountId: null);
        await ledger.IngestBatchAsync([unattributed]);

        // Re-observing a historical unattributed call while another account is active must
        // never silently manufacture attribution.
        await Assert.ThrowsAsync<UsageLedgerConflictException>(() => ledger.IngestBatchAsync([
            Call("cascade-i2", accountAttributionBasis: UsageAccountAttributionBasis.VerifiedObservation, accountId: "acc_synthetic01"),
        ]));

        var record = Assert.Single(await ledger.GetAllCallsAsync());
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, record.AccountAttributionBasis);
        Assert.Null(record.AccountId);
    }

    #endregion

    #region J. Batch atomicity

    [Fact]
    public async Task Batch_ContainingConflict_CommitsNothing()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var existing = Call("cascade-j1");
        await ledger.IngestBatchAsync([existing]);
        string segmentPath = SegmentPathOf(temp, "2026-10");
        byte[] bytesBefore = await File.ReadAllBytesAsync(segmentPath);

        var conflict = await Assert.ThrowsAsync<UsageLedgerConflictException>(() => ledger.IngestBatchAsync([
            Call("cascade-j2"),
            Call("cascade-j1", inputTokens: 12345),
            Call("cascade-j3"),
        ]));

        Assert.Equal(existing.CallKey, conflict.CallKey);
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(segmentPath));
        Assert.Single(await ledger.GetAllCallsAsync());
    }

    [Fact]
    public async Task Batch_SpanningMonths_CommitsAllSegments_WhenValid()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);

        var result = await ledger.IngestBatchAsync([
            Call("cascade-j-sep", firstObservedAtUtc: September),
            Call("cascade-j-oct", firstObservedAtUtc: October),
            Call("cascade-j-nov", firstObservedAtUtc: November),
        ]);

        Assert.Equal(3, result.InsertedCount);
        Assert.Equal(3, (await ledger.GetAllCallsAsync()).Count);
    }

    [Fact]
    public async Task Batch_WithInternalDuplicate_CollapsesToSingleInsert()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);

        var result = await ledger.IngestBatchAsync([
            Call("cascade-j-dup"),
            Call("cascade-j-dup"),
        ]);

        Assert.Equal(1, result.InsertedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Single(await ledger.GetAllCallsAsync());
    }

    [Fact]
    public async Task EmptyBatch_IsAcceptedNoOp()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);

        var result = await ledger.IngestBatchAsync([]);

        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Empty(await ledger.GetAllCallsAsync());
    }

    #endregion

    #region K. Restart

    [Fact]
    public async Task Restart_NewStoreInstance_PreservesExactAccounting()
    {
        using var temp = new TestTempDirectory();
        var calls = new List<UsageCallRecord>
        {
            Call("cascade-k1", inputTokens: 3372, outputTokens: 500, cacheReadTokens: 16307, firstObservedAtUtc: September),
            Call("cascade-k2", inputTokens: 10, outputTokens: 3, firstObservedAtUtc: October),
            Call("cascade-k3",
                firstObservedAtUtc: November,
                timeAttribution: UsageTimeAttribution.HistoricalUnknown,
                accountAttributionBasis: UsageAccountAttributionBasis.Unattributed,
                accountId: null),
        };
        await CreateLedger(temp).IngestBatchAsync(calls);

        var reopened = CreateLedger(temp);
        var restored = await reopened.GetAllCallsAsync();

        Assert.Equal(calls.OrderBy(static c => c.CallKey, StringComparer.Ordinal), restored.OrderBy(static c => c.CallKey, StringComparer.Ordinal));
        // Re-ingesting after restart stays idempotent because the dedup index is rebuilt.
        var reIngest = await reopened.IngestBatchAsync(calls);
        Assert.Equal(0, reIngest.InsertedCount);
        Assert.Equal(3, reIngest.DuplicateCount);
        Assert.Equal(3, (await reopened.GetAllCallsAsync()).Count);
    }

    #endregion

    #region L/M. Corruption + unsupported schema

    private static async Task<string> WriteCommittedLedgerAsync(TestTempDirectory temp)
    {
        var ledger = CreateLedger(temp);
        await ledger.IngestBatchAsync([Call("cascade-corrupt")]);
        string segmentPath = SegmentPathOf(temp, "2026-10");
        return segmentPath;
    }

    [Fact]
    public async Task CorruptSegment_FailsClosed_PreservesCorruptBytes_BlocksWrites()
    {
        using var temp = new TestTempDirectory();
        string segmentPath = await WriteCommittedLedgerAsync(temp);
        const string corruptBytes = "{{{ not json at all";
        await File.WriteAllTextAsync(segmentPath, corruptBytes);

        var ledger = CreateLedger(temp);
        await Assert.ThrowsAsync<UsageLedgerCorruptionException>(() => ledger.GetAllCallsAsync());
        await Assert.ThrowsAsync<UsageLedgerCorruptionException>(() => ledger.IngestBatchAsync([Call("cascade-fresh")]));
        Assert.Equal(corruptBytes, await File.ReadAllTextAsync(segmentPath));
    }

    [Fact]
    public async Task DuplicateCallKeyWithinSegment_IsCorruption_NotSilentMerge()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var call = Call("cascade-dupkey");
        await ledger.IngestBatchAsync([call]);
        string segmentPath = SegmentPathOf(temp, "2026-10");
        byte[] bytesBefore = await File.ReadAllBytesAsync(segmentPath);

        // Two structurally valid JSON records sharing one call key: the ledger fails closed
        // instead of silently merging or last-writer-winning.
        string callJson =
            "{\"callKey\":\"" + call.CallKey + "\"," +
            "\"responseModelKey\":\"claude-sonnet-4-5\"," +
            "\"provider\":\"anthropic\"," +
            "\"inputTokens\":100,\"outputTokens\":40," +
            "\"responseOutputTokens\":30,\"thinkingOutputTokens\":10," +
            "\"firstObservedAtUtc\":\"2026-10-04T12:00:00+00:00\"," +
            "\"timeAttribution\":\"ObservationTime\"," +
            "\"accountAttributionBasis\":\"VerifiedObservation\"," +
            "\"accountId\":\"acc_synthetic01\"," +
            "\"source\":\"SyntheticTest\"}";
        string duplicated =
            "{\"magic\":\"AG2USAGELDGR\",\"schemaVersion\":1,\"segmentMonth\":\"2026-10\"," +
            "\"updatedAt\":\"2026-10-04T12:00:00+00:00\",\"calls\":[" + callJson + "," + callJson + "]}";
        await File.WriteAllTextAsync(segmentPath, duplicated);

        await Assert.ThrowsAsync<UsageLedgerCorruptionException>(() => ledger.GetAllCallsAsync());
        await Assert.ThrowsAsync<UsageLedgerCorruptionException>(() => ledger.IngestBatchAsync([Call("cascade-dupkey")]));
        Assert.Equal(duplicated, await File.ReadAllTextAsync(segmentPath));
        Assert.NotEqual(bytesBefore, await File.ReadAllBytesAsync(segmentPath));
    }

    [Fact]
    public async Task UnsupportedSchemaVersion_FailsClosed_WithoutOverwrite()
    {
        using var temp = new TestTempDirectory();
        string segmentPath = await WriteCommittedLedgerAsync(temp);
        byte[] bytesBefore = await File.ReadAllBytesAsync(segmentPath);

        // Rewrite the same document body with a future schema version.
        string text = await File.ReadAllTextAsync(segmentPath);
        string future = text.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        Assert.NotEqual(text, future);
        await File.WriteAllTextAsync(segmentPath, future);

        var ledger = CreateLedger(temp);
        await Assert.ThrowsAsync<UsageLedgerCorruptionException>(() => ledger.GetAllCallsAsync());
        await Assert.ThrowsAsync<UsageLedgerCorruptionException>(() => ledger.IngestBatchAsync([Call("cascade-future")]));
        Assert.Equal(future, await File.ReadAllTextAsync(segmentPath));
        Assert.NotEqual(bytesBefore, await File.ReadAllBytesAsync(segmentPath));
    }

    [Fact]
    public async Task ForeignMagic_FailsClosed()
    {
        using var temp = new TestTempDirectory();
        string segmentPath = await WriteCommittedLedgerAsync(temp);
        string text = await File.ReadAllTextAsync(segmentPath);
        await File.WriteAllTextAsync(segmentPath, text.Replace("AG2USAGELDGR", "NOTOURMAGIC", StringComparison.Ordinal));

        await Assert.ThrowsAsync<UsageLedgerCorruptionException>(() => CreateLedger(temp).GetAllCallsAsync());
    }

    [Fact]
    public async Task SegmentMonthMismatch_FailsClosed()
    {
        using var temp = new TestTempDirectory();
        string segmentPath = await WriteCommittedLedgerAsync(temp);
        string text = await File.ReadAllTextAsync(segmentPath);
        await File.WriteAllTextAsync(segmentPath, text.Replace("\"segmentMonth\": \"2026-10\"", "\"segmentMonth\": \"2026-09\"", StringComparison.Ordinal));

        await Assert.ThrowsAsync<UsageLedgerCorruptionException>(() => CreateLedger(temp).GetAllCallsAsync());
    }

    #endregion

    #region N. Concurrency

    [Fact]
    public async Task ConcurrentDistinctInsertions_ProduceExactCounts()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);

        var batches = Enumerable.Range(0, 8).Select(worker =>
            Enumerable.Range(0, 25).Select(index => Call($"cascade-n-{worker}-{index}")).ToList()).ToList();
        var results = await Task.WhenAll(batches.Select(batch => ledger.IngestBatchAsync(batch)));

        Assert.All(results, result => Assert.Equal(25, result.InsertedCount));
        Assert.Equal(200, (await ledger.GetAllCallsAsync()).Count);
    }

    [Fact]
    public async Task ConcurrentSameCallDuplicateContention_InsertsExactlyOnce()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var call = Call("cascade-n-dup");

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => ledger.IngestBatchAsync([call])));

        Assert.Equal(1, results.Sum(static result => result.InsertedCount));
        Assert.Equal(5, results.Sum(static result => result.DuplicateCount));
        Assert.Single(await ledger.GetAllCallsAsync());
    }

    [Fact]
    public async Task ConcurrentConflictingContention_KeepsExactlyOneRecord()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(async worker =>
        {
            try
            {
                return await ledger.IngestBatchAsync([Call("cascade-n-conflict", inputTokens: worker == 2 ? 500 : 100)]);
            }
            catch (UsageLedgerConflictException)
            {
                return null;
            }
        }));

        Assert.Equal(1, outcomes.Count(static outcome => outcome is not null && outcome.InsertedCount == 1));
        Assert.Single(await ledger.GetAllCallsAsync());
    }

    [Fact]
    public async Task LeaseDisposalFailure_StillReleasesSegmentLock()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        ledger.AcquireSegmentLeaseAsync = (_, _) => Task.FromResult<IAsyncDisposable>(new ThrowingLease());

        // The synthetic disposal failure is captured and dropped; the segment PathLock must
        // still be released, proven by a follow-up ingest completing within a bounded budget.
        await ledger.IngestBatchAsync([Call("cascade-n-lease")]);

        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var followUp = await ledger.IngestBatchAsync([Call("cascade-n-lease-2")], bounded.Token);
        Assert.Equal(1, followUp.InsertedCount);
        Assert.Equal(2, (await ledger.GetAllCallsAsync()).Count);
    }

    #endregion

    #region O. Cancellation / failed durable write

    [Fact]
    public async Task PreCancelledBatch_PersistsNothing()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ledger.IngestBatchAsync([Call("cascade-o1")], cts.Token));

        Assert.False(Directory.Exists(System.IO.Path.Combine(temp.Path, "usage")),
            "A cancelled batch must not create any ledger segment file.");
    }

    [Fact]
    public async Task FailedDurableWrite_KeepsPriorLedgerCoherent_AndStoreUsable()
    {
        using var temp = new TestTempDirectory();
        var ledgerDirectory = System.IO.Path.Combine(temp.Path, "usage");
        var committed = Call("cascade-o2");
        await new DurableUsageCallLedger(ledgerDirectory).IngestBatchAsync([committed]);

        var failing = new DurableUsageCallLedger(ledgerDirectory, fileWriter: new ThrowingFileWriter());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => failing.IngestBatchAsync([Call("cascade-o3")]));

        var healthy = new DurableUsageCallLedger(ledgerDirectory);
        var record = Assert.Single(await healthy.GetAllCallsAsync());
        Assert.Equal(committed, record);

        var recovery = await healthy.IngestBatchAsync([Call("cascade-o3")]);
        Assert.Equal(1, recovery.InsertedCount);
        Assert.Equal(2, (await healthy.GetAllCallsAsync()).Count);
    }

    #endregion

    #region P. Privacy

    [Fact]
    public async Task DurableBytes_ContainNoSensitiveContent_OrRawIdentifiers()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        const string samplePrompt = "Write me a poem about dragons SECRET-PROMPT-XYZ";
        const string sampleSystemPrompt = "You are a helpful assistant SYSTEM-PROMPT-XYZ";
        const string sampleEmail = "user@example.com";
        const string sampleCsrf = "csrf-token-abc123";
        const string sampleTitle = "My Secret Conversation Title";

        var call = new UsageCallRecord(
            UsageCallKeys.Compute(
                $"cascade-SECRET-PROMPT-XYZ-{sampleTitle}",
                "response-SECRETEMAIL-" + sampleEmail),
            UsageCallKeys.CanonicalizeResponseModel("claude-sonnet-4-5"),
            "anthropic",
            100,
            40,
            30,
            10,
            null,
            October,
            UsageTimeAttribution.ObservationTime,
            UsageAccountAttributionBasis.VerifiedObservation,
            "acc_synthetic01",
            "CascadeConversationCall");
        await ledger.IngestBatchAsync([call]);

        string segmentText = await File.ReadAllTextAsync(SegmentPathOf(temp, "2026-10"));
        Assert.Contains(call.CallKey, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain(samplePrompt, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain(sampleSystemPrompt, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain(sampleEmail, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain(sampleCsrf, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain(sampleTitle, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-PROMPT-XYZ", segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETEMAIL", segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("cascade-", segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("response-", segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("systemPrompt", segmentText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("promptSections", segmentText, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Q. Removed-account durability

    [Fact]
    public async Task HistoricalUsage_SurvivesAccountRemoval()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var removed = Call("cascade-q1", accountId: "acc_removed00000");
        await ledger.IngestBatchAsync([removed]);

        // The ledger is a durable historical id reference; it never consults the live account
        // store, so usage attributed to an account that later disappears remains readable.
        var reopened = CreateLedger(temp);
        var records = await reopened.GetCallsByAccountAsync("acc_removed00000");
        var record = Assert.Single(records);
        Assert.Equal(removed, record);
        Assert.Equal(removed.ConversationTokens, record.ConversationTokens);
    }

    #endregion

    #region R. Scale

    [Fact]
    public async Task ThousandsOfRecords_Ingest_Deduplicate_AndReopen()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        const int batches = 10;
        const int perBatch = 500;

        var allCalls = new List<List<UsageCallRecord>>(batches);
        for (int batch = 0; batch < batches; batch++)
        {
            allCalls.Add(Enumerable.Range(0, perBatch)
                .Select(index => Call($"cascade-scale-{batch:d2}-{index:d4}", inputTokens: index, outputTokens: batch))
                .ToList());
        }

        long inserted = 0;
        foreach (var batch in allCalls)
            inserted += (await ledger.IngestBatchAsync(batch)).InsertedCount;
        Assert.Equal(batches * perBatch, inserted);
        Assert.Equal(batches * perBatch, (await ledger.GetAllCallsAsync()).Count);

        long duplicates = 0;
        foreach (var batch in allCalls)
            duplicates += (await ledger.IngestBatchAsync(batch)).DuplicateCount;
        Assert.Equal(batches * perBatch, duplicates);
        Assert.Equal(batches * perBatch, (await ledger.GetAllCallsAsync()).Count);

        var reopened = CreateLedger(temp);
        Assert.Equal(batches * perBatch, (await reopened.GetAllCallsAsync()).Count);
        var spot = await reopened.GetCallsByModelAsync("claude-sonnet-4-5");
        Assert.Equal(batches * perBatch, spot.Count);
        var zeroRecord = Assert.Single(spot, static call => call.InputTokens == 0 && call.OutputTokens == 0);
        Assert.Equal(UsageCallKeys.Compute("cascade-scale-00-0000", "response-1"), zeroRecord.CallKey);
    }

    #endregion

    #region F-01: successive-append cache integrity (L1-L6)

    [Fact]
    public async Task L1_ReObservingFirstBatchKey_UnderAnotherMonth_DoesNotDuplicate()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);

        // Batch 1: A into October. Batch 2: B into the SAME October segment (rewrite) — the
        // post-write cache must retain A even though only B was inserted by this batch.
        await ledger.IngestBatchAsync([Call("f01-a", firstObservedAtUtc: October)]);
        await ledger.IngestBatchAsync([Call("f01-b", firstObservedAtUtc: October)]);

        // Batch 3: A re-observed under a different observation month. The ledger must still
        // recognize A from October and refuse a cross-month duplicate.
        var result = await ledger.IngestBatchAsync([Call("f01-a", firstObservedAtUtc: November)]);

        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(1, result.DuplicateCount);
        var all = await ledger.GetAllCallsAsync();
        Assert.Equal(2, all.Count); // A and B, both in October
        Assert.Single(all, call => call.CallKey == UsageCallKeys.Compute("f01-a", "response-1"));
        string novemberSegment = System.IO.Path.Combine(temp.Path, "usage", "usage-calls-2026-11.json");
        Assert.False(File.Exists(novemberSegment),
            "The November segment must not be created for a cross-month duplicate.");
    }

    [Fact]
    public async Task L2_SuccessiveAppends_RetainEveryPriorKey()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var batches = new[]
        {
            new[] { Call("f01-b1", firstObservedAtUtc: October) },
            new[] { Call("f01-b2", firstObservedAtUtc: October) },
            new[] { Call("f01-b3", firstObservedAtUtc: October) },
            new[] { Call("f01-b4", firstObservedAtUtc: October) },
        };

        foreach (var batch in batches)
        {
            var result = await ledger.IngestBatchAsync(batch);
            Assert.Equal(1, result.InsertedCount);
        }

        Assert.Equal(4, (await ledger.GetAllCallsAsync()).Count);
        foreach (var batch in batches)
        {
            var duplicate = await ledger.IngestBatchAsync(batch);
            Assert.Equal(0, duplicate.InsertedCount);
            Assert.Equal(1, duplicate.DuplicateCount);
        }
        Assert.Equal(4, (await ledger.GetAllCallsAsync()).Count);
    }

    [Fact]
    public async Task L3_AfterSuccessiveBatches_DuplicateAndConflictChecksStillRecognizeEarliestRecords()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var first = Call("f01-first", inputTokens: 100, firstObservedAtUtc: October);
        await ledger.IngestBatchAsync([first]);
        await ledger.IngestBatchAsync([Call("f01-second", firstObservedAtUtc: October)]);
        await ledger.IngestBatchAsync([Call("f01-third", firstObservedAtUtc: October)]);

        // Identical re-observation of the FIRST batch's record remains an idempotent no-op.
        var duplicate = await ledger.IngestBatchAsync([Call("f01-first", inputTokens: 100, firstObservedAtUtc: November)]);
        Assert.Equal(0, duplicate.InsertedCount);
        Assert.Equal(1, duplicate.DuplicateCount);

        // Conflicting re-observation of the FIRST batch's record still fails closed.
        await Assert.ThrowsAsync<UsageLedgerConflictException>(
            () => ledger.IngestBatchAsync([Call("f01-first", inputTokens: 424242, firstObservedAtUtc: November)]));

        var record = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == first.CallKey);
        Assert.Equal(100, record.InputTokens);
    }

    [Fact]
    public async Task L4_ColdRestart_AfterSuccessiveAppends_NoCorruption_ExactCallSet()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        var expected = new List<UsageCallRecord>();
        for (int i = 0; i < 4; i++)
        {
            var call = Call($"f01-cold{i}", firstObservedAtUtc: October);
            expected.Add(call);
            await ledger.IngestBatchAsync([call]);
        }

        var reopened = CreateLedger(temp);
        var restored = await reopened.GetAllCallsAsync();
        Assert.Equal(
            expected.OrderBy(static call => call.CallKey, StringComparer.Ordinal),
            restored.OrderBy(static call => call.CallKey, StringComparer.Ordinal));

        // The reopened ledger remains fully writable (no fail-closed corruption state).
        var post = await reopened.IngestBatchAsync([Call("f01-cold-post", firstObservedAtUtc: November)]);
        Assert.Equal(1, post.InsertedCount);
        Assert.Equal(5, (await reopened.GetAllCallsAsync()).Count);
    }

    [Fact]
    public async Task L5_MultiProcess_WriterAAppends_WriterBSeesOlderKeyUnderAnotherMonth_NoDuplicate()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"ag2_f01_mp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            string ledgerDir = Path.Combine(testDir, "usage");
            var writerA = new DurableUsageCallLedger(ledgerDir);
            await writerA.IngestBatchAsync([Call("f01-mp", firstObservedAtUtc: October)]);
            await writerA.IngestBatchAsync([Call("f01-mp-2", firstObservedAtUtc: October)]);

            // Independent writer instance (own caches) re-observes the FIRST batch's key under
            // November: global dedup must hold despite A's successive appends.
            var writerB = new DurableUsageCallLedger(ledgerDir);
            var result = await writerB.IngestBatchAsync([Call("f01-mp", firstObservedAtUtc: November)]);

            Assert.Equal(0, result.InsertedCount);
            Assert.Equal(1, result.DuplicateCount);
            var all = await writerB.GetAllCallsAsync();
            Assert.Equal(2, all.Count);
            Assert.Single(all, call => call.CallKey == UsageCallKeys.Compute("f01-mp", "response-1"));
        }
        finally
        {
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task L6_CachedKeyCount_MatchesSegmentContents_AfterAppends()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        await ledger.IngestBatchAsync([Call("f01-l6-1", firstObservedAtUtc: October)]);
        await ledger.IngestBatchAsync([Call("f01-l6-2", firstObservedAtUtc: October)]);
        await ledger.IngestBatchAsync([Call("f01-l6-3", firstObservedAtUtc: October)]);

        var snapshot = ledger.SegmentKeyCacheSnapshot();
        var octoberKeys = Assert.IsType<List<string>>(snapshot["2026-10"]).ToList();

        Assert.Equal(3, octoberKeys.Count);
        Assert.Contains(UsageCallKeys.Compute("f01-l6-1", "response-1"), octoberKeys);
        Assert.Contains(UsageCallKeys.Compute("f01-l6-2", "response-1"), octoberKeys);
        Assert.Contains(UsageCallKeys.Compute("f01-l6-3", "response-1"), octoberKeys);
    }

    #endregion

    #region Query contract

    [Fact]
    public async Task QueryContract_FiltersAttribution_Model_AndObservationTime()
    {
        using var temp = new TestTempDirectory();
        var ledger = CreateLedger(temp);
        await ledger.IngestBatchAsync([
            Call("cascade-q-a", firstObservedAtUtc: October),
            Call("cascade-q-b",
                firstObservedAtUtc: October.AddHours(1),
                accountAttributionBasis: UsageAccountAttributionBasis.Unattributed,
                accountId: null),
            Call("cascade-q-c",
                firstObservedAtUtc: October.AddHours(2),
                timeAttribution: UsageTimeAttribution.HistoricalUnknown,
                accountAttributionBasis: UsageAccountAttributionBasis.Unattributed,
                accountId: null,
                responseModel: "claude-opus-4-6"),
            Call("cascade-q-d", firstObservedAtUtc: November),
        ]);

        Assert.Equal(2, (await ledger.GetCallsByAccountAsync("acc_synthetic01")).Count);
        Assert.Equal(2, (await ledger.GetUnattributedCallsAsync()).Count);
        Assert.Equal(3, (await ledger.GetCallsByModelAsync("claude-sonnet-4-5")).Count);
        Assert.Single(await ledger.GetCallsByModelAsync("claude-opus-4-6"));

        // Observation-time trends only admit ObservationTime records within the range;
        // HistoricalUnknown records contribute to totals but never to time buckets.
        var window = await ledger.GetObservationTimeCallsBetweenAsync(October, October.AddHours(2));
        Assert.Equal(2, window.Count);
        Assert.All(window, static call => Assert.Equal(UsageTimeAttribution.ObservationTime, call.TimeAttribution));
        Assert.Empty(await ledger.GetObservationTimeCallsBetweenAsync(October.AddHours(2), November));

        await Assert.ThrowsAsync<ArgumentException>(
            () => ledger.GetCallsByAccountAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(
            () => ledger.GetCallsByModelAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.GetObservationTimeCallsBetweenAsync(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), October));
    }

    #endregion
}
