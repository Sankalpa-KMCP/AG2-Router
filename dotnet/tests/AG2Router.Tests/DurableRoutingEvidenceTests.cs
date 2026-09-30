using System.IO;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

internal sealed class EvidenceTestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan duration) => Now += duration;
}

public sealed partial class TargetQuotaVerificationSwitchTests
{
    private const string EvidenceModel = "claude-3-5-sonnet";

    [Fact]
    public async Task Evidence_SuccessfulCompleteTargetSnapshot_MissingOtherModelBecomesDurableUnknown()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await store.RecordObservationsAsync(_target!.Id,
            [new(_target.Id, "model-b", 0.9, "past-reset", clock.Now.AddMinutes(-5), "ActiveGetUserStatus")]);
        await using var router = EvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.True(_globalTimeline.LastIndexOf("VERIFY_QUOTA") < _globalTimeline.IndexOf("TRY_FINALIZE"));
        var reloaded = new DurableQuotaObservationStore(store.GetFilePath());
        var missing = await reloaded.GetObservationAsync(_target.Id, "model-b");
        Assert.NotNull(missing);
        Assert.Null(missing.RemainingFraction);
        Assert.Null(missing.ResetTime);
        Assert.Equal(clock.Now, missing.ObservedAtUtc);
        Assert.Equal(0.8, (await reloaded.GetObservationAsync(_target.Id, EvidenceModel))!.RemainingFraction);
        var accounts = await _accounts.ListAccountsAsync();
        var selection = CandidateSelector.SelectBestCandidate(_source!.Id, 0.05, accounts,
            new Dictionary<string, double>(), new RouterConfigDto(WorkloadModelKey: "model-b"),
            vaultedAccountIds: new HashSet<string>([_source.Id, _target.Id]), relevantModelKeys: ["model-b"],
            candidateModelObservations: new Dictionary<string, AccountModelQuotaObservation> { [_target.Id] = missing },
            evaluationTimeUtc: clock.Now);
        Assert.False(selection.ShouldSwitch);
        Assert.False(Assert.Single(selection.Candidates).IsEligible);
    }

    private QuotaSnapshotDto EvidenceQuota(double? fraction, bool exhausted = false, string model = EvidenceModel) =>
        new("synthetic", [new ModelQuotaDto(model, model, fraction, null, exhausted)], null, null);

    private async Task<(DurableQuotaObservationStore Store, EvidenceTestClock Clock)> SeedDurableRoutingAsync()
    {
        await SeedAccountsAsync();
        var clock = new EvidenceTestClock();
        var path = Path.Combine(_tempDir, "quota-observations.json");
        var writer = new DurableQuotaObservationStore(path);
        await writer.RecordObservationsAsync(_target!.Id,
            [new(_target.Id, EvidenceModel, 0.8, null, clock.Now.AddMinutes(-90), "ActiveGetUserStatus")]);
        _adapter.RequestedModel = EvidenceModel;
        _adapter.TargetQuota = EvidenceQuota(0.8);
        _adapter.QuotaObservationBehavior = _ => Task.FromResult(new AG2Router.Core.Contracts.AccountQuotaObservation(
            _adapter.Identity, _adapter.Identity?.Email == _source!.Email ? EvidenceQuota(0.05) : _adapter.TargetQuota));
        // A new instance must reload the persisted evidence; no SetObservedQuota.
        return (new DurableQuotaObservationStore(path), clock);
    }

    private NativeAutoRouter EvidenceRouter(DurableQuotaObservationStore store, EvidenceTestClock clock) =>
        new(_accounts, _vault, _adapter, CreateCoordinator(quotaObservationStore: store, timeProvider: clock),
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store, timeProvider: clock);

    [Fact]
    public async Task Evidence_RestartPersistedCandidate_ReachesRealCoordinatorAndPrecommitLiveVerification()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouter(store, clock);
        var decision = await router.EvaluateCycleAsync();
        Assert.True(decision.ShouldSwitch);
        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(1, _accounts.FinalizeCalls);
        Assert.Equal(_target!.Id, await _accounts.GetActiveAccountIdAsync());
        Assert.True(_globalTimeline.LastIndexOf("VERIFY_QUOTA") < _globalTimeline.IndexOf("TRY_FINALIZE"));
        Assert.Contains("succeeded", router.GetStatus().LastDecisionReason);
    }

    [Fact]
    public async Task Evidence_PersistedCandidateStillCurrentAfterNormalStabilization()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouter(store, clock);
        var token = router.NotifyManualSwitchStarted(_source!.Id);
        await router.NotifyManualSwitchCompletedAsync(token, new NativeSwitchResult("synthetic", true,
            SwitchResultCodes.Success, NativeSwitchStates.Complete, _source.Id, _source.Email,
            null, null, "synthetic", [], "synthetic", "synthetic"));
        clock.Advance(TimeSpan.FromSeconds(31));
        await router.EvaluateCycleAsync();
        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(1, _accounts.FinalizeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evidence_ExpiryBetweenSelectionAndAdmission_BlocksMutation(bool atStopBoundary)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouter(store, clock);
        if (atStopBoundary) _process.BeforeStopProof = () => { clock.Advance(TimeSpan.FromMinutes(30)); return Task.CompletedTask; };
        else router.BeforeGatePublicationAsync = () => { clock.Advance(TimeSpan.FromMinutes(30)); return Task.CompletedTask; };
        await router.EvaluateCycleAsync();
        Assert.Equal(0, _process.LaunchCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.DoesNotContain("PROCESS_STOP", _globalTimeline);
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(null)]
    public async Task Evidence_NewerSnapshot_CancelsExactSelectedObservation(double? replacementFraction)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await using var router = EvidenceRouter(store, clock);
        _process.BeforeStopProof = async () => await store.RecordObservationsAsync(_target!.Id,
            [new(_target.Id, EvidenceModel, replacementFraction, null, clock.Now, "ActiveGetUserStatus")]);
        await router.EvaluateCycleAsync();
        Assert.Equal(0, _process.LaunchCount);
        Assert.DoesNotContain("PROCESS_STOP", _globalTimeline);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(121)]
    [InlineData(-1)]
    public async Task Evidence_StaleOrFuturePersistedCandidate_CannotReachMutation(int ageMinutes)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        // Replace the task-owned fixture to test both older and future timestamps.
        await File.WriteAllTextAsync(store.GetFilePath(), JsonSerializer.Serialize(new QuotaObservationsDocument(2, clock.Now,
            [new(_target!.Id, EvidenceModel, 0.8, null, clock.Now.AddMinutes(-ageMinutes), "ActiveGetUserStatus")])));
        await using var router = EvidenceRouter(store, clock);
        Assert.False((await router.EvaluateCycleAsync()).ShouldSwitch);
        Assert.Equal(0, _process.LaunchCount);
    }

    [Fact]
    public async Task Evidence_InvalidationPersistenceFailure_StillRollsBackAndQuarantinesAdmission()
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        _adapter.TargetQuota = EvidenceQuota(null);
        var coordinator = CreateCoordinator(quotaObservationStore: new RefusingInvalidationStore(store), timeProvider: clock);
        await using var router = new NativeAutoRouter(_accounts, _vault, _adapter, coordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: EvidenceModel),
            quotaObservationStore: store, timeProvider: clock);
        await router.EvaluateCycleAsync();
        Assert.Equal(1, _process.RestoreCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
        Assert.True(coordinator.GetStatus().QuarantineActive);
        Assert.False(coordinator.CanAdmitSwitch(out _));
        Assert.False(router.GetConfig().AutoSwitchEnabled);
        Assert.True(File.Exists(_journal.JournalFilePath));
        var restartedCoordinator = CreateCoordinator(quotaObservationStore: store, timeProvider: clock);
        await restartedCoordinator.ReconcileStartupJournalAsync();
        Assert.False(restartedCoordinator.CanAdmitSwitch(out _));
    }

    private sealed class RefusingInvalidationStore(IQuotaObservationStore inner) : IQuotaObservationStore
    {
        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetAllObservationsAsync(CancellationToken cancellationToken = default) => inner.GetAllObservationsAsync(cancellationToken);
        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetObservationsForAccountAsync(string accountId, CancellationToken cancellationToken = default) => inner.GetObservationsForAccountAsync(accountId, cancellationToken);
        public Task<AccountModelQuotaObservation?> GetObservationAsync(string accountId, string modelKey, CancellationToken cancellationToken = default) => inner.GetObservationAsync(accountId, modelKey, cancellationToken);
        public Task RecordObservationsAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations, CancellationToken cancellationToken = default) => inner.RecordObservationsAsync(accountId, observations, cancellationToken);
        public Task RecordCompleteSnapshotAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations, DateTimeOffset observedAtUtc, string source, CancellationToken cancellationToken = default) => inner.RecordCompleteSnapshotAsync(accountId, observations, observedAtUtc, source, cancellationToken);
        public Task InvalidateIfUnchangedAsync(AccountModelQuotaObservation expected, DateTimeOffset invalidatedAtUtc, CancellationToken cancellationToken = default) => Task.FromException(new IOException("Synthetic invalidation fault"));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("absent")]
    [InlineData("unavailable")]
    public async Task Evidence_ActiveUnknownOrAbsentModel_SupersedesOlderHealthyEvidence(string kind)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        await store.RecordObservationsAsync(_source!.Id,
            [new(_source.Id, EvidenceModel, 0.9, null, clock.Now.AddMinutes(-5), "ActiveGetUserStatus")]);
        _adapter.QuotaObservationBehavior = _ => Task.FromResult(new AG2Router.Core.Contracts.AccountQuotaObservation(
            _adapter.Identity, kind == "absent" ? EvidenceQuota(0.9, model: "other-model") :
                kind == "unavailable" ? null : EvidenceQuota(null)));
        await using var router = EvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        var unknown = await store.GetObservationAsync(_source.Id, EvidenceModel);
        Assert.NotNull(unknown);
        Assert.Null(unknown.RemainingFraction);
        Assert.Equal(clock.Now, unknown.ObservedAtUtc);
        Assert.Equal(0, _process.LaunchCount);
    }

    [Theory]
    [InlineData("exhausted")]
    [InlineData("low")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("unavailable")]
    public async Task Evidence_DisprovenLiveTarget_IsDurablyInvalidatedAndCannotRequalifyAfterCooldown(string failure)
    {
        var (store, clock) = await SeedDurableRoutingAsync();
        _adapter.TargetQuota = failure switch
        {
            "exhausted" => EvidenceQuota(0, true),
            "low" => EvidenceQuota(0.1),
            "unknown" => EvidenceQuota(null),
            "missing" => EvidenceQuota(0.9, model: "other-model"),
            "invalid" => EvidenceQuota(double.NaN),
            _ => null
        };
        await using var router = EvidenceRouter(store, clock);
        await router.EvaluateCycleAsync();
        Assert.Equal(1, _process.LaunchCount);
        Assert.Equal(1, _process.RestoreCount);
        Assert.Equal(0, _accounts.FinalizeCalls);
        var rejection = await new DurableQuotaObservationStore(store.GetFilePath()).GetObservationAsync(_target!.Id, EvidenceModel);
        Assert.NotNull(rejection);
        Assert.Null(rejection.RemainingFraction);
        Assert.Equal("LiveTargetVerificationRejected", rejection.Source);
        clock.Advance(TimeSpan.FromSeconds(61));
        var decision = await router.EvaluateCycleAsync();
        Assert.False(decision.ShouldSwitch);
        Assert.Equal(1, _process.LaunchCount);
        Assert.Contains(decision.Candidates, c => c.Account.Id == _target.Id && !c.IsEligible);
        // An independent new live observation is required to restore eligibility.
        await store.RecordObservationsAsync(_target.Id, [new(_target.Id, EvidenceModel, 0.8, null, clock.Now, "ActiveGetUserStatus")]);
        _adapter.TargetQuota = EvidenceQuota(0.8);
        await router.EvaluateCycleAsync();
        Assert.Equal(2, _process.LaunchCount);
        Assert.Equal(1, _accounts.FinalizeCalls);
    }
}

public sealed class DurableRoutingEvidenceTests : IDisposable
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteSnapshot_PreservesNewerIndependentEvidenceRegardlessOfWriteOrder(bool independentFirst)
    {
        var at = _clock.Now;
        var newer = new AccountModelQuotaObservation("candidate", "model-b", 0.9, null, at.AddSeconds(1), "IndependentLive");
        await Store.RecordObservationsAsync("candidate", [new("candidate", "model-b", 0.8, null, at.AddSeconds(-1), "OldLive")]);
        if (independentFirst) await Store.RecordObservationsAsync("candidate", [newer]);
        await Store.RecordCompleteSnapshotAsync("candidate", [new("candidate", "model-a", 0.7, null, at, "CompleteLive")], at, "CompleteLive");
        if (!independentFirst) await Store.RecordObservationsAsync("candidate", [newer]);
        Assert.Equal(newer, await Store.GetObservationAsync("candidate", "model-b"));
    }

    [Fact]
    public async Task CompleteSnapshot_UpdatesBothModels_ThenUnknownCannotBeResurrectedByOlderOrEqualHealthyPatch()
    {
        var at = _clock.Now;
        await Store.RecordCompleteSnapshotAsync("candidate", [new("candidate", "model-a", 0.7, null, at, "Live"), new("candidate", "model-b", 0.8, null, at, "Live")], at, "Live");
        Assert.Equal(0.7, (await Store.GetObservationAsync("candidate", "model-a"))!.RemainingFraction);
        Assert.Equal(0.8, (await Store.GetObservationAsync("candidate", "model-b"))!.RemainingFraction);
        var next = at.AddSeconds(1);
        await Store.RecordCompleteSnapshotAsync("candidate", [new("candidate", "model-a", 0.6, null, next, "Live")], next, "Live");
        await Store.RecordObservationsAsync("candidate", [new("candidate", "model-b", 1, null, at, "LateOld")]);
        await Store.RecordObservationsAsync("candidate", [new("candidate", "model-b", 1, null, next, "EqualTime")]);
        Assert.Null((await Store.GetObservationAsync("candidate", "model-b"))!.RemainingFraction);
    }

    [Fact]
    public async Task CompleteSnapshot_RejectsMixedTimes_AndPartialPatchDoesNotEraseOtherModels()
    {
        var at = _clock.Now;
        await Store.RecordObservationsAsync("candidate", [new("candidate", "model-b", 0.9, null, at, "Live")]);
        await Assert.ThrowsAsync<ArgumentException>(() => Store.RecordCompleteSnapshotAsync("candidate",
            [new("candidate", "model-a", 0.7, null, at.AddSeconds(1), "Live")], at, "Live"));
        await Store.RecordObservationsAsync("candidate", [new("candidate", "model-a", 0.7, null, at, "Patch")]);
        Assert.Equal(0.9, (await Store.GetObservationAsync("candidate", "model-b"))!.RemainingFraction);
    }
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ag2_evidence_" + Guid.NewGuid().ToString("N"));
    private readonly EvidenceTestClock _clock = new();
    private DurableQuotaObservationStore Store => new(Path.Combine(_directory, "quota.json"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private AccountModelQuotaObservation Row(double? fraction, DateTimeOffset? time = null, string? reset = null) =>
        new("candidate", " GEMINI-PRO ", fraction, reset, time ?? _clock.Now, "ActiveGetUserStatus");

    [Fact]
    public async Task NewerUnknownSupersedesHealthy_AndDelayedOldHealthyCannotReviveIt()
    {
        await Store.RecordObservationsAsync("candidate", [Row(0.9)]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Store.RecordObservationsAsync("candidate", [Row(null)]);
        await Store.RecordObservationsAsync("candidate", [Row(1, _clock.Now.AddSeconds(-1))]);
        Assert.Null((await Store.GetObservationAsync("candidate", "gemini-pro"))!.RemainingFraction);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(null)]
    public async Task DuplicatePoolOrderingCannotImproveEvidence(double? weakest)
    {
        var rows = new[] { Row(0.9, reset: "2026-10-01T00:00:00Z"), Row(weakest, reset: "2026-10-02T00:00:00Z") };
        await Store.RecordObservationsAsync("candidate", rows);
        var first = await Store.GetObservationAsync("candidate", "gemini-pro");
        await Store.RecordObservationsAsync("candidate", rows.Reverse());
        var second = await Store.GetObservationAsync("candidate", "gemini-pro");
        Assert.Equal(first, second);
        Assert.Equal(weakest, second!.RemainingFraction);
        Assert.Null(second.ResetTime); // No asserted shared reset window.
    }

    [Fact]
    public async Task ConditionalInvalidationPreservesNewerIndependentEvidence()
    {
        var old = Row(0.8);
        await Store.RecordObservationsAsync("candidate", [old]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var fresh = Row(0.6);
        await Store.RecordObservationsAsync("candidate", [fresh]);
        await Store.InvalidateIfUnchangedAsync(old, _clock.Now);
        Assert.Equal(fresh, await Store.GetObservationAsync("candidate", "gemini-pro"));
    }

    [Fact]
    public async Task DifferentlyTimedDuplicatePools_SupersedeHealthyWithUnknownRegardlessOfOrdering()
    {
        await Store.RecordObservationsAsync("candidate", [Row(0.9)]);
        var oldPool = Row(0.8, _clock.Now.AddMinutes(-5));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var newPool = Row(null);
        await Store.RecordObservationsAsync("candidate", [newPool, oldPool]);
        var unknown = await Store.GetObservationAsync("candidate", "gemini-pro");
        Assert.Null(unknown!.RemainingFraction);
        Assert.Equal(_clock.Now, unknown.ObservedAtUtc);
        await Store.RecordObservationsAsync("candidate", [oldPool, newPool]);
        Assert.Equal(unknown, await Store.GetObservationAsync("candidate", "gemini-pro"));
    }

    [Fact]
    public async Task VersionOneDuplicateRowsReloadConservatively_ThenWriteVersionTwoUnknown()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Store.GetFilePath(), JsonSerializer.Serialize(
            new QuotaObservationsDocument(1, _clock.Now, [Row(0), Row(0.9)])));
        Assert.Equal(0, (await Store.GetObservationAsync("candidate", "gemini-pro"))!.RemainingFraction);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Store.RecordObservationsAsync("candidate", [Row(null)]);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Store.GetFilePath()));
        Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Null((await Store.GetObservationAsync("candidate", "gemini-pro"))!.RemainingFraction);
    }

    [Fact]
    public async Task DurableRanking_PreservesReserveQuotaPriorityAndOrdinalTieBreak()
    {
        AccountMetadata Account(string id, int priority, bool reserve = false) =>
            new(id, id + "@example.com", null, priority, reserve, "VALID", true, "synthetic", "synthetic");
        var accounts = new[] { Account("reserve", 0, true), Account("lower", 0), Account("priority", 9), Account("b", 1), Account("a", 1) };
        foreach (var account in accounts)
            await Store.RecordObservationsAsync(account.Id,
                [new(account.Id, "gemini-pro", account.IsReserve ? 0.99 : account.Id == "lower" ? 0.7 : 0.8,
                    null, _clock.Now, "ActiveGetUserStatus")]);
        var evidence = (await Store.GetAllObservationsAsync()).ToDictionary(o => o.AccountId);
        foreach (var ordering in new[] { accounts, accounts.Reverse().ToArray() })
        {
            var selected = CandidateSelector.SelectBestCandidate("current", 0.05, ordering,
                new Dictionary<string, double>(), new RouterConfigDto(),
                relevantModelKeys: ["gemini-pro"], candidateModelObservations: evidence, evaluationTimeUtc: _clock.Now);
            Assert.Equal("a", selected.BestCandidate!.Account.Id);
        }
    }
}
