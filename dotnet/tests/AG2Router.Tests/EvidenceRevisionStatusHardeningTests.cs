using System.Globalization;
using System.IO;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Switching;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed partial class TargetQuotaVerificationSwitchTests
{
    private static DateTimeOffset RevisionTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private async Task<DurableQuotaObservationStore> SeedRevisionDocumentAsync(string timestamp)
    {
        var path = Path.Combine(_tempDir, "offset-revision.json");
        var document = new QuotaObservationsDocument(2, RevisionTime(timestamp),
            [new("revision-target", EvidenceModel, 0.8, null,
                RevisionTime("2026-10-06T00:00:00Z"), "synthetic")]);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));
        return new DurableQuotaObservationStore(path);
    }

    [Theory]
    [InlineData("2026-10-07T00:00:00Z", "2026-10-07T01:00:00Z")]
    // The review counterexample has identical local ticks but different UTC instants.
    [InlineData("2026-10-07T05:30:00+05:30", "2026-10-07T05:30:00Z")]
    [InlineData("2026-10-07T01:00:00-05:00", "2026-10-07T07:00:00Z")]
    [InlineData("2026-10-07T00:00:00Z", "2026-10-07T06:30:00+05:30")]
    [InlineData("2026-10-07T05:30:00+05:30", "2026-10-06T23:00:00Z")]
    [InlineData("2026-10-07T05:30:00+05:30", "2026-10-07T00:00:00Z")]
    [InlineData("2026-10-07T00:00:00Z", "2026-10-07T05:30:00+05:30")]
    [InlineData("9999-12-31T23:59:59.9999999+05:30", "2026-10-07T00:00:00Z")]
    public async Task EvidenceRevision_OffsetMutation_StrictlyAdvancesAbsoluteInstant(string previous, string proposed)
    {
        var store = await SeedRevisionDocumentAsync(previous);
        Assert.Null(store.KnownObservationRevision);
        var before = await store.GetObservationSnapshotAsync();
        Assert.Equal(RevisionTime(previous).UtcDateTime.Ticks, before.Revision);

        await store.InvalidateObservationsForAccountAsync("revision-target", RevisionTime(proposed));

        // Check synchronous visibility before any read can mask an uncommitted revision.
        Assert.True(store.KnownObservationRevision > before.Revision);
        var reader = new DurableQuotaObservationStore(store.GetFilePath());
        var after = await reader.GetObservationSnapshotAsync();
        Assert.True(after.Revision > before.Revision);
        Assert.Equal(store.KnownObservationRevision, after.Revision);
        Assert.Null(Assert.Single(after.Observations).RemainingFraction);
        var persisted = JsonSerializer.Deserialize<QuotaObservationsDocument>(await File.ReadAllTextAsync(store.GetFilePath()))!;
        Assert.Equal(TimeSpan.Zero, persisted.UpdatedAt.Offset);
        Assert.Equal(persisted.UpdatedAt.UtcDateTime.Ticks, after.Revision);
    }

    [Theory]
    [InlineData("2026-10-07T05:30:00+05:30")]
    [InlineData("2026-10-06T19:00:00-05:00")]
    [InlineData("2026-10-07T00:00:00Z")]
    public async Task EvidenceRevision_EquivalentInstants_ReadSameRevisionWithoutWriting(string representation)
    {
        var store = await SeedRevisionDocumentAsync(representation);
        var bytes = await File.ReadAllBytesAsync(store.GetFilePath());
        long expected = RevisionTime("2026-10-07T00:00:00Z").UtcDateTime.Ticks;
        Assert.Equal(expected, (await store.GetObservationSnapshotAsync()).Revision);
        Assert.Equal(expected, await store.GetObservationRevisionAsync());
        await store.GetAllObservationsAsync();
        Assert.Equal(expected, await new DurableQuotaObservationStore(store.GetFilePath()).GetObservationRevisionAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.GetFilePath()));
    }

    [Fact]
    public async Task EvidenceRevision_RapidClampedWrites_AdvanceEachCommit_AndNoOpDoesNot()
    {
        var store = await SeedRevisionDocumentAsync("2099-10-07T05:30:00+05:30");
        var fixedTime = RevisionTime("2026-10-07T00:00:00Z");
        long previous = await store.GetObservationRevisionAsync();
        for (int i = 0; i < 8; i++)
        {
            await store.RecordObservationsAsync("revision-target",
                [new("revision-target", EvidenceModel, 0.8, null, fixedTime.AddTicks(i), "synthetic")]);
            Assert.Equal(previous + 1, store.KnownObservationRevision);
            previous++;
        }
        var second = new DurableQuotaObservationStore(store.GetFilePath());
        await second.InvalidateObservationsForAccountAsync("revision-target", fixedTime);
        Assert.Equal(previous + 1, await store.GetObservationRevisionAsync());
        var snapshot = await store.GetObservationSnapshotAsync();
        await store.InvalidateObservationsForAccountAsync("revision-target", fixedTime);
        await store.InvalidateObservationsForAccountAsync("absent", fixedTime);
        await store.InvalidateIfUnchangedAsync(snapshot.Observations.Single() with { Source = "different" }, fixedTime);
        Assert.Equal(snapshot.Revision, store.KnownObservationRevision);
        Assert.Equal(snapshot.Revision, await second.GetObservationRevisionAsync());
    }

    [Fact]
    public async Task EvidenceRevision_MaximumInstant_FailsBeforeCommitRatherThanWraps()
    {
        var store = await SeedRevisionDocumentAsync("9999-12-31T23:59:59.9999999Z");
        var before = await store.GetObservationSnapshotAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.InvalidateObservationsForAccountAsync("revision-target", DateTimeOffset.MaxValue));
        Assert.Equal(before.Revision, store.KnownObservationRevision);
        var after = await store.GetObservationSnapshotAsync();
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Observations, after.Observations);
    }

    [Fact]
    public async Task EvidenceRevision_MissingTimestamp_RetainsHistoricalDefault()
    {
        var path = Path.Combine(_tempDir, "missing-time.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":2,\"observations\":[]}");
        var store = new DurableQuotaObservationStore(path);
        Assert.Equal(0, await store.GetObservationRevisionAsync());
        await store.RecordObservationsAsync("revision-target",
            [new("revision-target", EvidenceModel, 0.8, null, DateTimeOffset.UtcNow, "synthetic")]);
        Assert.True(store.KnownObservationRevision > 0);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"not-a-timestamp\"")]
    [InlineData("\"2026-10-07 00:00:00Z\"")]
    [InlineData("\"2026-10-07T00:00:00z\"")]
    public async Task EvidenceRevision_InvalidTimestamp_FailsClosed(string timestampJson)
    {
        var path = Path.Combine(_tempDir, "invalid-time.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":2,\"updatedAt\":" + timestampJson + ",\"observations\":[]}");
        var store = new DurableQuotaObservationStore(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetObservationSnapshotAsync());
        Assert.Null(store.KnownObservationRevision);
    }

    [Fact]
    public async Task PoolStatus_SynchronousCoordinatorFailure_RejectsCacheWithoutAsyncStatusRead()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = NonSwitchingEvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        Assert.True(router.GetStatus().PoolStatus!.HasUsableCandidate);
        _process.OnReplacement = () => _adapter.Identity = null;
        var coordinator = CreateCoordinator(TimeSpan.FromMilliseconds(50), store, clock);
        var result = await coordinator.SwitchAutomaticallyAsync(_target!.Id, _source!.Id, () => true, EvidenceModel, 30);
        Assert.Equal(SwitchResultCodes.SwitchFailedRolledBack, result.Code);
        Assert.Null(router.GetStatus().PoolStatus);
        Assert.Equal(_source.Id, await _accounts.GetActiveAccountIdAsync());
    }

    [Fact]
    public async Task PoolStatus_SynchronousCommitAndFreshEvaluation_RestoreCurrentCache()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = NonSwitchingEvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        Assert.True(router.GetStatus().PoolStatus!.HasUsableCandidate);
        await store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);
        Assert.Null(router.GetStatus().PoolStatus);
        await router.EvaluateCycleAsync();
        Assert.False(router.GetStatus().PoolStatus!.HasUsableCandidate);
        clock.Now = clock.Now.AddSeconds(1);
        await store.RecordObservationsAsync(_target.Id,
            [new(_target.Id, EvidenceModel, 0.8, null, clock.Now, "ActiveGetUserStatus")]);
        Assert.Null(router.GetStatus().PoolStatus);
        await router.EvaluateCycleAsync();
        Assert.True(router.GetStatus().PoolStatus!.HasUsableCandidate);
    }

    [Fact]
    public async Task PoolStatus_NoOpAndFailedPersistence_DoNotPretendEvidenceCommitted()
    {
        var (seed, clock) = await SeedDurableRoutingAsync();
        var writer = new RevisionControlledWriter();
        var store = new DurableQuotaObservationStore(seed.GetFilePath(), writer);
        await using var router = NonSwitchingEvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        var before = router.GetStatus().PoolStatus;
        Assert.True(before!.HasUsableCandidate);
        var revision = store.KnownObservationRevision;
        await store.InvalidateObservationsForAccountAsync("absent", clock.Now);
        Assert.Same(before, router.GetStatus().PoolStatus);
        writer.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now));
        Assert.Equal(revision, store.KnownObservationRevision);
        Assert.Same(before, router.GetStatus().PoolStatus);
        Assert.Equal(revision, await new DurableQuotaObservationStore(seed.GetFilePath()).GetObservationRevisionAsync());
        Assert.Equal(0.8, (await store.GetObservationAsync(_target!.Id, EvidenceModel))!.RemainingFraction);
    }

    [Fact]
    public async Task PoolStatus_IndependentWriter_IsDetectedByAsyncDurableRead()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = NonSwitchingEvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        Assert.True(router.GetStatus().PoolStatus!.HasUsableCandidate);
        long? known = store.KnownObservationRevision;
        var external = new DurableQuotaObservationStore(store.GetFilePath());
        await external.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);
        // The synchronous property is explicitly instance-local, not a disk freshness proof.
        Assert.Equal(known, store.KnownObservationRevision);
        var status = await router.GetCandidateEvidenceStatusAsync();
        Assert.Equal("UNKNOWN", Assert.Single(status.Candidates, c => c.AccountId == _target.Id).State);
        Assert.Null(status.PoolStatus);
        Assert.Null(router.GetStatus().PoolStatus);
    }

    [Fact]
    public async Task PoolStatus_CommitNotVisibleUntilAtomicWriterCompletes()
    {
        var (seed, clock) = await SeedDurableRoutingAsync();
        var writer = new RevisionControlledWriter();
        var store = new DurableQuotaObservationStore(seed.GetFilePath(), writer);
        await using var router = NonSwitchingEvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        var cached = router.GetStatus().PoolStatus;
        var revision = store.KnownObservationRevision;
        writer.Block = true;
        var mutation = store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);
        await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(revision, store.KnownObservationRevision);
            Assert.Same(cached, router.GetStatus().PoolStatus);
        }
        finally { writer.Release.TrySetResult(); }
        await mutation;
        Assert.True(store.KnownObservationRevision > revision);
        Assert.Null(router.GetStatus().PoolStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PoolStatus_MutationBetweenRevisionReadAndStateLock_RejectsOldStatus(bool candidateAccessor)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        var hooked = new RevisionReadHookStore(store);
        await using var router = NonSwitchingEvidenceRouter(hooked, clock);
        await router.EvaluateCycleAsync();
        Assert.True(router.GetStatus().PoolStatus!.HasUsableCandidate);
        hooked.AfterRead = () => store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);
        if (candidateAccessor)
            Assert.Null((await router.GetCandidateEvidenceStatusAsync()).PoolStatus);
        else
            await router.EvaluateCycleAsync();
        Assert.Null(router.GetStatus().PoolStatus);
    }

    private sealed class RevisionReadHookStore(IQuotaObservationStore inner) : IQuotaObservationStore
    {
        public Func<Task>? AfterRead { get; set; }
        public long? KnownObservationRevision => inner.KnownObservationRevision;
        public async Task<long> GetObservationRevisionAsync(CancellationToken cancellationToken = default)
        {
            long revision = await inner.GetObservationRevisionAsync(cancellationToken);
            if (AfterRead is { } hook)
            {
                AfterRead = null;
                await hook();
            }
            return revision;
        }
        public Task<QuotaObservationSnapshot> GetObservationSnapshotAsync(CancellationToken cancellationToken = default) =>
            inner.GetObservationSnapshotAsync(cancellationToken);
        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetAllObservationsAsync(CancellationToken cancellationToken = default) =>
            inner.GetAllObservationsAsync(cancellationToken);
        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetObservationsForAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            inner.GetObservationsForAccountAsync(accountId, cancellationToken);
        public Task<AccountModelQuotaObservation?> GetObservationAsync(string accountId, string modelKey, CancellationToken cancellationToken = default) =>
            inner.GetObservationAsync(accountId, modelKey, cancellationToken);
        public Task RecordObservationsAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations, CancellationToken cancellationToken = default) =>
            inner.RecordObservationsAsync(accountId, observations, cancellationToken);
        public Task RecordCompleteSnapshotAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations,
            DateTimeOffset observedAtUtc, string source, CancellationToken cancellationToken = default) =>
            inner.RecordCompleteSnapshotAsync(accountId, observations, observedAtUtc, source, cancellationToken);
    }

    [Fact]
    public async Task PoolStatus_NullRouterDto_IsSafeForTrayQuickStatusAndSerialization()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = NonSwitchingEvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        await store.InvalidateObservationsForAccountAsync(_target!.Id, clock.Now);
        var status = router.GetStatus();
        Assert.Null(status.PoolStatus);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("PoolStatus").ValueKind);
        Assert.Contains("Auto: IDLE", AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", status.PoolStatus));
        var (text, tip) = AG2Router.App.Views.QuickStatusWindow.FormatAutoSwitchStatus(true, status.PoolStatus);
        Assert.Equal("Active", text);
        Assert.Null(tip);
    }

    private sealed class RevisionControlledWriter : IDurableFileWriter
    {
        public bool Fail { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Fail) throw new IOException("Synthetic atomic persistence failure.");
            if (Block)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await new DurableFileWriter().WriteAtomicAsync(destinationPath, content, cancellationToken);
        }
    }
}
