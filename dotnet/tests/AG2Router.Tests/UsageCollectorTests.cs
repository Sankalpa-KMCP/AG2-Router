using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Discovery;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Rpc;
using AG2Router.AG2.Usage;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Usage collection cycle tests: multi-instance merge, change gating, typed privacy-minimal
/// parsing, attribution continuity (baseline, forward, switch, restart gap, unknown accounts),
/// collector lifecycle, and failure isolation. All state lives in synthetic temp directories.
/// </summary>
public class UsageCollectorTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class TestTempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"ag2_u2_{Guid.NewGuid():N}");

        public TestTempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }

    private sealed class StubRpcClient : IUsageRpcClient, IAG2RpcClient
    {
        public sealed record EndpointState
        {
            public string? IdentityEmail { get; set; }
            public Dictionary<string, RawTrajectorySummary> Trajectories { get; } = new();
            public Dictionary<string, string> GeneratorMetadataRawJson { get; } = new(StringComparer.Ordinal);
            public Func<CancellationToken, Task>? Gate { get; set; }
            public bool ThrowOnList { get; set; }
            public bool FailIdentityAfterFirstObservation { get; set; }
            public int IdentityObservations { get; set; }
        }

        public Dictionary<int, EndpointState> Endpoints { get; } = new();
        public int GeneratorMetadataCalls { get; private set; }
        public List<string> RequestedCascades { get; } = new();

        /// <summary>Test-only fault injection: the named cascade's metadata fetch throws once scheduled.</summary>
        public string? FailMetadataForCascade { get; set; }

        public EndpointState Endpoint(int port) =>
            Endpoints.TryGetValue(port, out var state)
                ? state
                : Endpoints[port] = new EndpointState();

        public Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(Endpoints.ContainsKey(port));

        public async Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
        {
            var state = Endpoint(port);
            if (state.ThrowOnList) throw new InvalidOperationException("synthetic list failure");
            if (state.Gate is not null) await state.Gate(cancellationToken).ConfigureAwait(false);
            return new RawTrajectoriesResponse { TrajectorySummaries = new Dictionary<string, RawTrajectorySummary>(state.Trajectories) };
        }

        public async Task<RawUserStatusResponse?> GetUserStatusAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
        {
            var state = Endpoint(port);
            if (state.Gate is not null) await state.Gate(cancellationToken).ConfigureAwait(false);
            state.IdentityObservations++;
            if (state.FailIdentityAfterFirstObservation && state.IdentityObservations > 1)
                throw new InvalidOperationException("synthetic identity observation failure");
            return new RawUserStatusResponse
            {
                UserStatus = state.IdentityEmail is null ? null : new RawUserStatus { Email = state.IdentityEmail },
            };
        }

        public async Task<IReadOnlyList<RawGeneratorMetadataEntry>> GetCascadeTrajectoryGeneratorMetadataAsync(
            int port, string protocol, string csrfToken, string cascadeId, CancellationToken cancellationToken = default)
        {
            var state = Endpoint(port);
            GeneratorMetadataCalls++;
            RequestedCascades.Add(cascadeId);
            if (FailMetadataForCascade == cascadeId)
                throw new InvalidOperationException("synthetic transient metadata fetch failure");
            if (state.Gate is not null) await state.Gate(cancellationToken).ConfigureAwait(false);
            if (!state.GeneratorMetadataRawJson.TryGetValue(cascadeId, out var rawJson))
                return [];
            using var document = JsonDocument.Parse(rawJson);
            return UsageRpcPayloads.ExtractEntries(document);
        }

    }

    private static RawTrajectorySummary Summary(string cascadeId, string status = "ACTIVE", int steps = 3, string modified = "2026-10-04T10:00:00Z") =>
        new() { CascadeId = cascadeId, Status = status, StepCount = steps, LastModifiedTime = modified };

    private static string EntryJson(string cascadeId, string responseId, string input, string output, string? cache = null, string? responseModel = "claude-sonnet-4-5", string? responseOut = null, string? thinkingOut = null) =>
        (char)123 + $"\"cascadeId\":\"{cascadeId}\",\"usage\":{{\"responseId\":\"{responseId}\",\"inputTokens\":\"{input}\",\"outputTokens\":\"{output}\"" +
        (responseOut is null ? "" : $",\"responseOutputTokens\":\"{responseOut}\"") +
        (thinkingOut is null ? "" : $",\"thinkingOutputTokens\":\"{thinkingOut}\"") +
        (cache is null ? "" : $",\"cacheReadTokens\":\"{cache}\"") +
        "},\"chatModel\":{" + $"\"responseModel\":{(responseModel is null ? "null" : $"\"{responseModel}\"")},\"model\":\"placeholder-model\"}}}}";

    private sealed record Harness : IDisposable
    {
        public TestTempDirectory Temp = new();
        public StubRpcClient Rpc = new();
        public InMemoryAccountStore Accounts = new();
        public TimeProvider Time = new FixedTimeProvider(Observed);
        public List<string> Logs = new();

        public DurableUsageCallLedger CreateLedger() =>
            new(System.IO.Path.Combine(Temp.Path, "usage"));

        public UsageCollectorStateStore CreateStateStore() =>
            new(System.IO.Path.Combine(Temp.Path, "usage"), Time);

        public UsageCollectionService CreateService(
            IUsageCallLedger? ledger = null,
            UsageCollectorStateStore? state = null,
            UsageInstanceDiscovery? discovery = null)
        {
            return new UsageCollectionService(
                ledger ?? CreateLedger(),
                state ?? CreateStateStore(),
                discovery ?? CreateDiscovery(Rpc.Endpoints.Keys.ToArray()),
                Rpc,
                Accounts,
                Time,
                message => Logs.Add(message));
        }

        public StaticInspector? LastInspector { get; private set; }

        public UsageInstanceDiscovery CreateDiscovery(params int[] ports)
        {
            var processes = ports
                .Select(pid => new DiscoveredProcessRaw(pid, "language_server.exe", "--standalone --csrf_token tok",
                    @"C:\antigravity\resources\bin\language_server.exe", DateTime.UtcNow))
                .ToArray();
            LastInspector = new StaticInspector(processes);
            return new UsageInstanceDiscovery(LastInspector, Rpc);
        }

        public void Dispose() => Temp.Dispose();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public Func<DateTimeOffset> NowProvider { get; set; } = () => now;
        public void Advance(TimeSpan by) => NowProvider = () => now += by;
        public override DateTimeOffset GetUtcNow() => NowProvider();
    }

    private sealed class StaticInspector(params DiscoveredProcessRaw[] processes) : IProcessInspector
    {
        private List<DiscoveredProcessRaw> _processes = processes.ToList();
        public bool FailFindProcesses { get; set; }

        public void SetEndpoints(params int[] ports)
        {
            _processes = ports.Select(pid => new DiscoveredProcessRaw(pid, "language_server.exe",
                "--standalone --csrf_token tok",
                @"C:\antigravity\resources\bin\language_server.exe", DateTime.UtcNow)).ToList();
        }

        public Task<IReadOnlyList<DiscoveredProcessRaw>> FindProcessesAsync(CancellationToken cancellationToken = default)
        {
            if (FailFindProcesses)
                throw new InvalidOperationException("synthetic process enumeration failure");
            return Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(_processes.ToList());
        }

        public Task<IReadOnlyList<int>> GetListeningPortsAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<int>>(new[] { processId });

        public bool IsPidAlive(int processId, DateTime? expectedStartTime = null) => true;
    }

    private static async Task<UsageCallRecord> SingleCallAsync(IUsageCallLedger ledger)
    {
        var all = await ledger.GetAllCallsAsync();
        return Assert.Single(all);
    }

    #region Transient per-cascade metadata fetch retry (F4)

    [Fact]
    public async Task F4_TransientMetadataFetchFailure_IsRetriedNextCycle_AndRecordedExactlyOnce()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var endpoint = harness.Rpc.Endpoint(40010);
        endpoint.IdentityEmail = "user@example.com";
        endpoint.Trajectories["cascade-f4"] = Summary("cascade-f4", "ACTIVE", steps: 3, modified: "2026-10-04T10:00:00Z");
        endpoint.GeneratorMetadataRawJson["cascade-f4"] = "[" + EntryJson("cascade-f4", "resp-f4-1", "10", "5") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);

        // Cycle 1: the trajectory signature is new, but the per-cascade metadata fetch fails.
        harness.Rpc.FailMetadataForCascade = "cascade-f4";
        var failedCycle = await service.RunCollectionCycleAsync();
        Assert.Empty(await ledger.GetAllCallsAsync());
        Assert.Equal("One or more conversations could not be inspected.", failedCycle.LastError);

        // Cycle 2: the SAME signature must be retried and, on success, recorded exactly once.
        harness.Rpc.FailMetadataForCascade = null;
        await service.RunCollectionCycleAsync();
        var record = await SingleCallAsync(ledger);
        Assert.Equal(10, record.InputTokens);
        Assert.Equal(5, record.OutputTokens);

        // Cycle 3: the unchanged signature is not fetched again — no repeated RPC, no duplicates.
        int fetchesAfterRecovery = harness.Rpc.GeneratorMetadataCalls;
        await service.RunCollectionCycleAsync();
        Assert.Equal(fetchesAfterRecovery, harness.Rpc.GeneratorMetadataCalls);
        Assert.Single(await ledger.GetAllCallsAsync());

        // Cycle 4: a genuinely changed signature is still fetched (change gating intact).
        endpoint.Trajectories["cascade-f4"] = Summary("cascade-f4", "ACTIVE", steps: 4, modified: "2026-10-04T10:05:00Z");
        endpoint.GeneratorMetadataRawJson["cascade-f4"] = "[" + EntryJson("cascade-f4", "resp-f4-2", "20", "8") + "]";
        await service.RunCollectionCycleAsync();
        Assert.Equal(fetchesAfterRecovery + 1, harness.Rpc.GeneratorMetadataCalls);
        Assert.Equal(2, (await ledger.GetAllCallsAsync()).Count);
    }

    #endregion

    #region Attribution continuity (J1–J10)

    [Fact]
    public async Task J1_FirstScan_HistoricalUnknown_AndUnattributed_EvenWithAccountActive()
    {
        using var harness = new Harness();
        var account = await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40", cache: "500") + "]";

        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();

        var record = await SingleCallAsync(ledger);
        Assert.Equal(UsageTimeAttribution.HistoricalUnknown, record.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, record.AccountAttributionBasis);
        Assert.Null(record.AccountId);
        Assert.True(service.BaselineEstablished);
    }

    [Fact]
    public async Task J2_ForwardCycle_AfterBaseline_AttributesObservationTimeToVerifiedAccount()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();

        // A new call appears in the same conversation under continuous observation; the
        // trajectory's change marker advances with it.
        rpc.Trajectories["c1"] = Summary("c1", steps: 4);
        rpc.GeneratorMetadataRawJson["c1"] = "[" +
            EntryJson("c1", "r1", "100", "40") + "," +
            EntryJson("c1", "r2", "200", "80", cache: "900") + "]";
        var second = await service.RunCollectionCycleAsync();

        var all = await ledger.GetAllCallsAsync();
        Assert.Equal(2, all.Count);
        var forward = all.Single(call => call.CallKey == UsageCallKeys.Compute("c1", "r2"));
        Assert.Equal(UsageTimeAttribution.ObservationTime, forward.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.VerifiedObservation, forward.AccountAttributionBasis);
        Assert.False(string.IsNullOrWhiteSpace(forward.AccountId));
        Assert.True(second.BaselineEstablished);
    }

    [Fact]
    public async Task J3_IdentityChangesMidCycle_NewCallsStayConservative()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("a@example.com"));
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("b@example.com"));
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "a@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();

        // Before this cycle's fetch the identity reads as B; after the fetch the identity
        // observation fails entirely, so before and after cannot be proven to agree.
        rpc.IdentityEmail = "b@example.com";
        rpc.Trajectories["c1"] = Summary("c1", steps: 4);
        rpc.GeneratorMetadataRawJson["c1"] = "[" +
            EntryJson("c1", "r1", "100", "40") + "," + EntryJson("c1", "r2", "300", "10") + "]";
        rpc.FailIdentityAfterFirstObservation = true;
        await service.RunCollectionCycleAsync();

        var forwardCall = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("c1", "r2"));
        // Account stays Unattributed (no proven identity), but the call was genuinely observed
        // forward, so the observation time is honest rather than faked as historical.
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, forwardCall.AccountAttributionBasis);
        Assert.Null(forwardCall.AccountId);
        Assert.Equal(UsageTimeAttribution.ObservationTime, forwardCall.TimeAttribution);
    }

    [Fact]
    public async Task J4_UnknownAccount_ForwardCalls_Unattributed()
    {
        using var harness = new Harness();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "ghost@example.com"; // not a managed account
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();

        rpc.Trajectories["c1"] = Summary("c1", steps: 4);
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "," + EntryJson("c1", "r2", "1", "1") + "]";
        await service.RunCollectionCycleAsync();

        var forward = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("c1", "r2"));
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, forward.AccountAttributionBasis);
        Assert.Equal(UsageTimeAttribution.ObservationTime, forward.TimeAttribution);
    }

    [Fact]
    public async Task J5_TwoInstancesDifferentAccounts_EachVerified_ForwardAttributionPerEndpoint()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("a@example.com"));
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("b@example.com"));
        var rpcA = harness.Rpc.Endpoint(40001);
        var rpcB = harness.Rpc.Endpoint(40002);
        rpcA.IdentityEmail = "a@example.com";
        rpcB.IdentityEmail = "b@example.com";
        rpcA.Trajectories["cA"] = Summary("cA");
        rpcB.Trajectories["cB"] = Summary("cB");
        rpcA.GeneratorMetadataRawJson["cA"] = "[" + EntryJson("cA", "rA", "10", "1") + "]";
        rpcB.GeneratorMetadataRawJson["cB"] = "[" + EntryJson("cB", "rB", "20", "2") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger, discovery: harness.CreateDiscovery(40001, 40002));
        await service.RunCollectionCycleAsync(); // baseline

        rpcA.Trajectories["cA"] = Summary("cA", steps: 4);
        rpcB.Trajectories["cB"] = Summary("cB", steps: 4);
        rpcA.GeneratorMetadataRawJson["cA"] = "[" + EntryJson("cA", "rA", "10", "1") + "," + EntryJson("cA", "rA2", "11", "1") + "]";
        rpcB.GeneratorMetadataRawJson["cB"] = "[" + EntryJson("cB", "rB", "20", "2") + "," + EntryJson("cB", "rB2", "21", "1") + "]";
        await service.RunCollectionCycleAsync();

        var all = await ledger.GetAllCallsAsync();
        Assert.Equal(4, all.Count);
        var a2 = all.Single(call => call.CallKey == UsageCallKeys.Compute("cA", "rA2"));
        var b2 = all.Single(call => call.CallKey == UsageCallKeys.Compute("cB", "rB2"));
        Assert.Equal((await harness.Accounts.GetAccountByEmailAsync("a@example.com"))!.Id, a2.AccountId);
        Assert.Equal((await harness.Accounts.GetAccountByEmailAsync("b@example.com"))!.Id, b2.AccountId);
        Assert.Equal(UsageTimeAttribution.ObservationTime, a2.TimeAttribution);
        Assert.Equal(UsageTimeAttribution.ObservationTime, b2.TimeAttribution);
    }

    [Fact]
    public async Task J6_J7_RestartGap_NewCallsConservative_ThenBaselineRestoresForward()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";

        // Process generation 1: baseline + one forward cycle.
        var ledger = harness.CreateLedger();
        var state = harness.CreateStateStore();
        var service1 = harness.CreateService(ledger: ledger, state: state);
        await service1.RunCollectionCycleAsync();
        rpc.Trajectories["c1"] = Summary("c1", steps: 4);
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "," + EntryJson("c1", "r2", "150", "50") + "]";
        await service1.RunCollectionCycleAsync();
        await service1.DisposeAsync();

        // Process generation 2 after an offline gap: brand-new conversation appears.
        rpc.Trajectories["c2"] = Summary("c2");
        rpc.GeneratorMetadataRawJson["c2"] = "[" + EntryJson("c2", "g1", "500", "100") + "]";
        var service2 = harness.CreateService(ledger: ledger, state: state);
        await service2.RunCollectionCycleAsync();

        // Gap call: not yet in ledger at restart → HistoricalUnknown + Unattributed, not "today".
        var gapCall = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("c2", "g1"));
        Assert.Equal(UsageTimeAttribution.HistoricalUnknown, gapCall.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, gapCall.AccountAttributionBasis);

        // J7: a new call observed in a later forward cycle is ObservationTime again.
        rpc.Trajectories["c2"] = Summary("c2", steps: 4);
        rpc.GeneratorMetadataRawJson["c2"] = "[" + EntryJson("c2", "g1", "500", "100") + "," + EntryJson("c2", "g2", "510", "100") + "]";
        await service2.RunCollectionCycleAsync();
        var resumed = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("c2", "g2"));
        Assert.Equal(UsageTimeAttribution.ObservationTime, resumed.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.VerifiedObservation, resumed.AccountAttributionBasis);
    }

    [Fact]
    public async Task J9_CorruptCheckpoint_FailClosed_NoFakeAttribution()
    {
        using var harness = new Harness();
        var ledgerDir = System.IO.Path.Combine(harness.Temp.Path, "usage");
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";

        var ledger = harness.CreateLedger();
        var state = harness.CreateStateStore();
        var service = harness.CreateService(ledger: ledger, state: state);
        await service.RunCollectionCycleAsync();

        // Corrupt the durable checkpoint after a verified baseline cycle.
        string statePath = System.IO.Path.Combine(ledgerDir, "usage-collector-state.json");
        await File.WriteAllTextAsync(statePath, "{ corrupt");

        rpc.Trajectories["c1"] = Summary("c1", steps: 4);
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "," + EntryJson("c1", "r2", "77", "7") + "]";
        var diagnostics = await service.RunCollectionCycleAsync();

        // Corruption is surfaced, never silently repaired or hidden.
        Assert.NotNull(diagnostics.LastError);
        Assert.Contains("checkpoint", diagnostics.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checkpoint unavailable", string.Join(Environment.NewLine, harness.Logs), StringComparison.OrdinalIgnoreCase);
        // Attribution in this cycle is NOT fake: it rests on per-endpoint continuity evidence
        // (genuine before/after agreement captured in cycle one), independent of the corrupt file.
        var forward = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("c1", "r2"));
        Assert.Equal(UsageAccountAttributionBasis.VerifiedObservation, forward.AccountAttributionBasis);
        Assert.Equal(UsageTimeAttribution.ObservationTime, forward.TimeAttribution);
        var accountId = forward.AccountId;
        Assert.False(string.IsNullOrWhiteSpace(accountId));
        Assert.Equal((await harness.Accounts.GetAccountByEmailAsync("user@example.com"))!.Id, accountId);

        // Crucial L3 invariant: corrupt checkpoint bytes must NOT be overwritten by the successful cycle save!
        Assert.Equal("{ corrupt", await File.ReadAllTextAsync(statePath));
        Assert.Contains("Usage checkpoint save failed", string.Join(Environment.NewLine, harness.Logs));

        // Subsequent cycle: still detects corrupt checkpoint, does NOT destroy it, continues recording ledger
        var cycle3Diag = await service.RunCollectionCycleAsync();
        Assert.NotNull(cycle3Diag.LastError);
        Assert.Contains("checkpoint", cycle3Diag.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{ corrupt", await File.ReadAllTextAsync(statePath));
    }

    [Fact]
    public async Task J10_RemovedAccountHistory_RemainsReadable()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("gone@example.com"));
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "gone@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();
        rpc.Trajectories["c1"] = Summary("c1", steps: 4);
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "," + EntryJson("c1", "r2", "5", "5") + "]";
        await service.RunCollectionCycleAsync();
        var attributed = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("c1", "r2"));
        var removedAccountId = attributed.AccountId!;

        await harness.Accounts.RemoveAccountAsync(removedAccountId);

        var stillThere = (await ledger.GetAllCallsAsync()).Where(call => call.AccountId == removedAccountId);
        Assert.NotEmpty(stillThere);
    }

    #endregion

    #region Collection mechanics (I1–I7)

    [Fact]
    public async Task I2_I3_DisjointAndOverlappingInstances_MergeThroughGlobalDedup()
    {
        using var harness = new Harness();
        var rpcA = harness.Rpc.Endpoint(40001);
        var rpcB = harness.Rpc.Endpoint(40002);
        rpcA.Trajectories["shared"] = Summary("shared");
        rpcB.Trajectories["shared"] = Summary("shared");
        rpcB.Trajectories["onlyB"] = Summary("onlyB");
        rpcA.GeneratorMetadataRawJson["shared"] = "[" + EntryJson("shared", "r1", "100", "40") + "]";
        rpcB.GeneratorMetadataRawJson["shared"] = "[" + EntryJson("shared", "r1", "100", "40") + "]";
        rpcB.GeneratorMetadataRawJson["onlyB"] = "[" + EntryJson("onlyB", "rB", "7", "2") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger, discovery: harness.CreateDiscovery(40001, 40002));

        await service.RunCollectionCycleAsync();

        var all = await ledger.GetAllCallsAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, call => call.CallKey == UsageCallKeys.Compute("shared", "r1"));
    }

    [Fact]
    public async Task I4_ChangeGating_UnchangedTrajectorySkipsGM_ChangedRefetched()
    {
        using var harness = new Harness();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();
        int callsAfterFirst = harness.Rpc.GeneratorMetadataCalls;

        // Unchanged summary: no new GM fetch.
        await service.RunCollectionCycleAsync();
        Assert.Equal(callsAfterFirst, harness.Rpc.GeneratorMetadataCalls);

        // Changed step count: refetch.
        rpc.Trajectories["c1"] = Summary("c1", steps: 5);
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "," + EntryJson("c1", "r2", "1", "1") + "]";
        await service.RunCollectionCycleAsync();
        Assert.Equal(callsAfterFirst + 1, harness.Rpc.GeneratorMetadataCalls);
        Assert.Contains("c1", harness.Rpc.RequestedCascades);
    }

    [Fact]
    public async Task I4_AmbiguousMarker_AlwaysRefetches()
    {
        using var harness = new Harness();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.Trajectories["c1"] = new RawTrajectorySummary { CascadeId = "c1", Status = "ACTIVE" };
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();
        int callsAfterFirst = harness.Rpc.GeneratorMetadataCalls;

        await service.RunCollectionCycleAsync();

        Assert.Equal(callsAfterFirst + 1, harness.Rpc.GeneratorMetadataCalls);
    }

    [Fact]
    public async Task I6_TokenParsing_BadEntriesSkipped_ValidPersisted()
    {
        using var harness = new Harness();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = null;
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" +
            EntryJson("c1", "ok1", "100", "40") + "," +
            EntryJson("c1", "bad", "not-a-number", "40") + "," +
            EntryJson("c1", "negative", "-5", "40") + "," +
            EntryJson("c1", "big", "9007199254740993", "1") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);

        await service.RunCollectionCycleAsync();

        var keys = (await ledger.GetAllCallsAsync()).Select(static call => call.CallKey).ToList();
        Assert.Contains(UsageCallKeys.Compute("c1", "ok1"), keys);
        Assert.Contains(UsageCallKeys.Compute("c1", "big"), keys); // int64 beyond double precision still parses
        Assert.DoesNotContain(UsageCallKeys.Compute("c1", "bad"), keys);
        Assert.DoesNotContain(UsageCallKeys.Compute("c1", "negative"), keys);
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public async Task I6_MissingModel_GoesToUnknownBucket_MissingCacheStaysNull()
    {
        using var harness = new Harness();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40", responseModel: null) + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();

        var record = await SingleCallAsync(ledger);
        Assert.Null(record.ResponseModelKey);
        Assert.Null(record.CacheReadTokens);
        Assert.Equal(140, record.ConversationTokens);
    }

    [Fact]
    public async Task I7_PromptAdjacentPayload_NeverReachesDurableStorage()
    {
        using var harness = new Harness();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.Trajectories["c1"] = Summary("c1");
        const string sentinelPrompt = "SECRET-PROMPT-CONTENT-XYZ";
        const string sentinelSystem = "SECRET-SYSTEM-PROMPT-XYZ";
        const string sentinelSchema = "SECRET-TOOL-SCHEMA-XYZ";
        rpc.GeneratorMetadataRawJson["c1"] = (char)123 +
            "\"systemPrompt\":\"" + sentinelSystem + "\"," +
            "\"promptSections\":[{\"content\":\"" + sentinelPrompt + "\"}]," +
            "\"tools\":[{\"jsonSchemaString\":\"" + sentinelSchema + "\"}]," +
            "\"entries\":[" + EntryJson("c1", "r1", "100", "40") + "]}";

        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);
        await service.RunCollectionCycleAsync();

        var record = await SingleCallAsync(ledger);
        Assert.Equal(140, record.ConversationTokens);
        string segmentText = File.ReadAllText(System.IO.Path.Combine(
            ledger.LedgerDirectoryPath, "usage-calls-2026-10.json"));
        Assert.DoesNotContain(sentinelPrompt, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinelSystem, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinelSchema, segmentText, StringComparison.Ordinal);
        Assert.DoesNotContain("systemPrompt", segmentText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("promptSections", segmentText, StringComparison.OrdinalIgnoreCase);
        foreach (var logLine in harness.Logs)
        {
            Assert.DoesNotContain(sentinelPrompt, logLine, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinelSystem, logLine, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinelSchema, logLine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task I1_OneInstanceFails_OthersStillCollect()
    {
        using var harness = new Harness();
        var rpcA = harness.Rpc.Endpoint(40001);
        var rpcB = harness.Rpc.Endpoint(40002);
        rpcA.ThrowOnList = true;
        rpcB.Trajectories["cB"] = Summary("cB");
        rpcB.GeneratorMetadataRawJson["cB"] = "[" + EntryJson("cB", "rB", "20", "2") + "]";
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger, discovery: harness.CreateDiscovery(40001, 40002));

        var diagnostics = await service.RunCollectionCycleAsync();

        Assert.Single(await ledger.GetAllCallsAsync());
        Assert.Equal(2, diagnostics.InstancesDiscovered);
        Assert.Equal(1, diagnostics.InstancesHealthy);
        Assert.NotNull(diagnostics.LastError);
    }

    [Fact]
    public async Task I_Performance_SecondUnchangedPoll_IsMateriallyCheaper()
    {
        using var harness = new Harness();
        var rpc = harness.Rpc.Endpoint(40001);
        for (int i = 0; i < 40; i++)
        {
            rpc.Trajectories[$"c{i}"] = Summary($"c{i}", steps: i + 1);
            var entries = string.Join(",", Enumerable.Range(0, 10)
                .Select(j => EntryJson($"c{i}", $"r{j}", "100", "40")));
            rpc.GeneratorMetadataRawJson[$"c{i}"] = "[" + entries + "]";
        }
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);

        await service.RunCollectionCycleAsync();
        int firstPollFetches = harness.Rpc.GeneratorMetadataCalls;
        await service.RunCollectionCycleAsync();
        int secondPollFetches = harness.Rpc.GeneratorMetadataCalls - firstPollFetches;

        Assert.Equal(40, firstPollFetches);
        Assert.Equal(0, secondPollFetches);
        Assert.Equal(400, (await ledger.GetAllCallsAsync()).Count);
    }

    #endregion

    #region C2 per-endpoint continuity (ATTR1-ATTR9)

    private async Task<UsageCallRecord> RunTwoInstanceScenarioAsync(
        int[] cycle1Ports, int[] cycle2Ports, string cycle2NewCallsFor)
    {
        // Cycle 1: endpoints in cycle1Ports run baseline. Cycle 2: endpoints in cycle2Ports;
        // the conversation named by cycle2NewCallsFor gains a second call.
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        foreach (var port in cycle1Ports.Union(cycle2Ports))
        {
            var rpc = harness.Rpc.Endpoint(port);
            rpc.IdentityEmail = "user@example.com";
        }
        var firstPort = cycle1Ports[0];
        harness.Rpc.Endpoint(firstPort).Trajectories["conv"] = Summary("conv");
        harness.Rpc.Endpoint(firstPort).GeneratorMetadataRawJson["conv"] =
            "[" + EntryJson("conv", "r1", "100", "40") + "]";

        var ledger = harness.CreateLedger();
        var discovery = harness.CreateDiscovery(cycle1Ports);
        var service = harness.CreateService(ledger: ledger, discovery: discovery);
        await service.RunCollectionCycleAsync();

        // Cycle 2 setup: identities stable, discovery per cycle2Ports, new call present.
        harness.LastInspector!.SetEndpoints(cycle2Ports);
        var secondPort = cycle2Ports.Contains(firstPort) ? firstPort : cycle2Ports[0];
        harness.Rpc.Endpoint(secondPort).Trajectories["conv"] = Summary("conv", steps: 4);
        harness.Rpc.Endpoint(secondPort).GeneratorMetadataRawJson["conv"] =
            "[" + EntryJson("conv", "r1", "100", "40") + "," + EntryJson("conv", "r2", "200", "80") + "]";
        await service.RunCollectionCycleAsync();

        var all = await ledger.GetAllCallsAsync();
        return all.Single(call => call.CallKey == UsageCallKeys.Compute("conv", "r2"));
    }

    [Fact]
    public async Task ATTR1_ContinuouslyObservedInstance_EarnsObservationTime()
    {
        var forward = await RunTwoInstanceScenarioAsync(new[] { 40001 }, new[] { 40001 }, "conv");

        Assert.Equal(UsageTimeAttribution.ObservationTime, forward.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.VerifiedObservation, forward.AccountAttributionBasis);
        Assert.False(string.IsNullOrWhiteSpace(forward.AccountId));
    }

    [Fact]
    public async Task ATTR2_NewInstanceAppearing_AfterGlobalBaseline_BaselinesItsBacklog()
    {
        // Cycle 1: only instance A. Cycle 2: instance B appears with pre-existing calls.
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpcA = harness.Rpc.Endpoint(40001);
        rpcA.IdentityEmail = "user@example.com";
        rpcA.Trajectories["convA"] = Summary("convA");
        rpcA.GeneratorMetadataRawJson["convA"] = "[" + EntryJson("convA", "rA", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var discovery = harness.CreateDiscovery(40001);
        var service = harness.CreateService(ledger: ledger, discovery: discovery);
        await service.RunCollectionCycleAsync();

        // Instance B appears with an old conversation already containing calls.
        harness.LastInspector!.SetEndpoints(40001, 40002);
        var rpcB = harness.Rpc.Endpoint(40002);
        rpcB.IdentityEmail = "user@example.com";
        rpcB.Trajectories["convB"] = Summary("convB");
        rpcB.GeneratorMetadataRawJson["convB"] = "[" + EntryJson("convB", "rB", "500", "50") + "]";
        await service.RunCollectionCycleAsync();

        var bCall = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("convB", "rB"));
        Assert.Equal(UsageTimeAttribution.HistoricalUnknown, bCall.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, bCall.AccountAttributionBasis);
        Assert.Null(bCall.AccountId);
    }

    [Fact]
    public async Task ATTR3_NewInstance_SecondCycle_EarnsObservationTimeForTrulyNewCalls()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpcA = harness.Rpc.Endpoint(40001);
        rpcA.IdentityEmail = "user@example.com";
        rpcA.Trajectories["convA"] = Summary("convA");
        rpcA.GeneratorMetadataRawJson["convA"] = "[" + EntryJson("convA", "rA", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var discovery = harness.CreateDiscovery(40001);
        var service = harness.CreateService(ledger: ledger, discovery: discovery);
        await service.RunCollectionCycleAsync();

        harness.LastInspector!.SetEndpoints(40001, 40002);
        var rpcB = harness.Rpc.Endpoint(40002);
        rpcB.IdentityEmail = "user@example.com";
        rpcB.Trajectories["convB"] = Summary("convB");
        rpcB.GeneratorMetadataRawJson["convB"] = "[" + EntryJson("convB", "rB", "500", "50") + "]";
        await service.RunCollectionCycleAsync(); // B baseline

        rpcB.Trajectories["convB"] = Summary("convB", steps: 4);
        rpcB.GeneratorMetadataRawJson["convB"] = "[" + EntryJson("convB", "rB", "500", "50") + "," + EntryJson("convB", "rB2", "510", "60") + "]";
        await service.RunCollectionCycleAsync(); // B forward

        var bForward = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("convB", "rB2"));
        Assert.Equal(UsageTimeAttribution.ObservationTime, bForward.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.VerifiedObservation, bForward.AccountAttributionBasis);
        var bBacklog = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("convB", "rB"));
        Assert.Equal(UsageTimeAttribution.HistoricalUnknown, bBacklog.TimeAttribution);
    }

    [Fact]
    public async Task ATTR4_5_ReturningInstance_Rebaselined_WhileSiblingContinuesForward()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpcA = harness.Rpc.Endpoint(40001);
        var rpcB = harness.Rpc.Endpoint(40002);
        foreach (var rpc in new[] { rpcA, rpcB })
        {
            rpc.IdentityEmail = "user@example.com";
        }
        rpcA.Trajectories["convA"] = Summary("convA");
        rpcA.GeneratorMetadataRawJson["convA"] = "[" + EntryJson("convA", "rA", "100", "40") + "]";
        rpcB.Trajectories["convB"] = Summary("convB");
        rpcB.GeneratorMetadataRawJson["convB"] = "[" + EntryJson("convB", "rB", "500", "50") + "]";
        var ledger = harness.CreateLedger();
        var discovery = harness.CreateDiscovery(40001, 40002);
        var service = harness.CreateService(ledger: ledger, discovery: discovery);
        await service.RunCollectionCycleAsync(); // both baseline

        // Both forward (second cycle, stable identity, new calls).
        harness.LastInspector!.SetEndpoints(40001, 40002);
        rpcA.Trajectories["convA"] = Summary("convA", steps: 4);
        rpcA.GeneratorMetadataRawJson["convA"] = "[" + EntryJson("convA", "rA", "100", "40") + "," + EntryJson("convA", "rA2", "110", "40") + "]";
        rpcB.Trajectories["convB"] = Summary("convB", steps: 4);
        rpcB.GeneratorMetadataRawJson["convB"] = "[" + EntryJson("convB", "rB", "500", "50") + "," + EntryJson("convB", "rB2", "510", "50") + "]";
        await service.RunCollectionCycleAsync();

        // B disappears; A continues alone with a new call.
        harness.LastInspector.SetEndpoints(40001);
        rpcA.Trajectories["convA"] = Summary("convA", steps: 5);
        rpcA.GeneratorMetadataRawJson["convA"] = "[" +
            EntryJson("convA", "rA", "100", "40") + "," + EntryJson("convA", "rA2", "110", "40") + "," + EntryJson("convA", "rA3", "120", "40") + "]";
        await service.RunCollectionCycleAsync();

        // B returns with unseen calls: re-baselined, NOT ObservationTime; A keeps ObservationTime.
        harness.LastInspector.SetEndpoints(40001, 40002);
        rpcB.Trajectories["convB"] = Summary("convB", steps: 5);
        rpcB.GeneratorMetadataRawJson["convB"] = "[" +
            EntryJson("convB", "rB", "500", "50") + "," + EntryJson("convB", "rB2", "510", "50") + "," + EntryJson("convB", "rB3", "520", "50") + "]";
        await service.RunCollectionCycleAsync();

        var all = await ledger.GetAllCallsAsync();
        var a3 = all.Single(call => call.CallKey == UsageCallKeys.Compute("convA", "rA3"));
        var b3 = all.Single(call => call.CallKey == UsageCallKeys.Compute("convB", "rB3"));
        Assert.Equal(UsageTimeAttribution.ObservationTime, a3.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.VerifiedObservation, a3.AccountAttributionBasis);
        Assert.Equal(UsageTimeAttribution.HistoricalUnknown, b3.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, b3.AccountAttributionBasis);
    }

    [Fact]
    public async Task ATTR6_SuccessfulDiscoveryWithoutInstance_BreaksItsContinuity()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpcA = harness.Rpc.Endpoint(40001);
        var rpcB = harness.Rpc.Endpoint(40002);
        foreach (var rpc in new[] { rpcA, rpcB })
        {
            rpc.IdentityEmail = "user@example.com";
            rpc.Trajectories["conv"] = Summary("conv");
            rpc.GeneratorMetadataRawJson["conv"] = "[" + EntryJson("conv", "r1", "100", "40") + "]";
        }
        var ledger = harness.CreateLedger();
        var discovery = harness.CreateDiscovery(40001, 40002);
        var service = harness.CreateService(ledger: ledger, discovery: discovery);
        await service.RunCollectionCycleAsync(); // both baseline (two separate conversations)

        // Successful discovery reports only A: B's continuity must break.
        harness.LastInspector!.SetEndpoints(40001);
        rpcA.Trajectories["conv"] = Summary("conv", steps: 4);
        rpcA.GeneratorMetadataRawJson["conv"] = "[" + EntryJson("conv", "r1", "100", "40") + "," + EntryJson("conv", "rA2", "110", "40") + "]";
        await service.RunCollectionCycleAsync();

        harness.LastInspector.SetEndpoints(40001, 40002);
        rpcB.Trajectories["conv"] = Summary("conv", steps: 5);
        rpcB.GeneratorMetadataRawJson["conv"] = "[" + EntryJson("conv", "r1", "100", "40") + "," + EntryJson("conv", "rB2", "210", "40") + "]";
        await service.RunCollectionCycleAsync();

        var all = await ledger.GetAllCallsAsync();
        var a2 = all.Single(call => call.CallKey == UsageCallKeys.Compute("conv", "rA2"));
        var b2 = all.Single(call => call.CallKey == UsageCallKeys.Compute("conv", "rB2"));
        Assert.Equal(UsageTimeAttribution.ObservationTime, a2.TimeAttribution);
        Assert.Equal(UsageTimeAttribution.HistoricalUnknown, b2.TimeAttribution);
    }

    [Fact]
    public async Task ATTR7_DiscoveryFailure_DoesNotBreakContinuity()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpcA = harness.Rpc.Endpoint(40001);
        rpcA.IdentityEmail = "user@example.com";
        rpcA.Trajectories["conv"] = Summary("conv");
        rpcA.GeneratorMetadataRawJson["conv"] = "[" + EntryJson("conv", "r1", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var discovery = harness.CreateDiscovery(40001);
        var service = harness.CreateService(ledger: ledger, discovery: discovery);
        await service.RunCollectionCycleAsync();
        await service.RunCollectionCycleAsync(); // A established + forward

        // One broken cycle: the inspector fails entirely (ambiguous, not proof of absence).
        // Collector failures degrade to diagnostics, so the cycle reports failure instead of
        // throwing — and continuity must survive it untouched.
        harness.LastInspector!.FailFindProcesses = true;
        var brokenCycle = await service.RunCollectionCycleAsync();
        harness.LastInspector.FailFindProcesses = false;
        Assert.False(brokenCycle.LastSuccessfulCollectionAtUtc.HasValue);
        Assert.NotNull(brokenCycle.LastError);

        // A new call in the next cycle is still forward: continuity survived the failure.
        rpcA.Trajectories["conv"] = Summary("conv", steps: 4);
        rpcA.GeneratorMetadataRawJson["conv"] = "[" + EntryJson("conv", "r1", "100", "40") + "," + EntryJson("conv", "r2", "110", "40") + "]";
        await service.RunCollectionCycleAsync();
        var forward = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("conv", "r2"));
        Assert.Equal(UsageTimeAttribution.ObservationTime, forward.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.VerifiedObservation, forward.AccountAttributionBasis);
    }

    [Fact]
    public async Task ATTR8_RestartedLanguageServer_NewProcessIdentity_IsRebaselined()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["conv"] = Summary("conv");
        rpc.GeneratorMetadataRawJson["conv"] = "[" + EntryJson("conv", "r1", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var discovery = harness.CreateDiscovery(40001);
        var service = harness.CreateService(ledger: ledger, discovery: discovery);
        await service.RunCollectionCycleAsync();
        rpc.Trajectories["conv"] = Summary("conv", steps: 4);
        rpc.GeneratorMetadataRawJson["conv"] = "[" + EntryJson("conv", "r1", "100", "40") + "," + EntryJson("conv", "r2", "110", "40") + "]";
        await service.RunCollectionCycleAsync();

        // The LS restarts: same port, brand-new PID — a new observation source identity.
        const int newPid = 45001;
        rpc.Trajectories["conv"] = Summary("conv", steps: 5);
        rpc.GeneratorMetadataRawJson["conv"] = "[" +
            EntryJson("conv", "r1", "100", "40") + "," + EntryJson("conv", "r2", "110", "40") + "," + EntryJson("conv", "r3", "120", "40") + "]";
        harness.LastInspector!.SetEndpoints(newPid);
        harness.Rpc.Endpoints[newPid] = harness.Rpc.Endpoints[40001];

        await service.RunCollectionCycleAsync();

        var all = await ledger.GetAllCallsAsync();
        var r3 = all.Single(call => call.CallKey == UsageCallKeys.Compute("conv", "r3"));
        Assert.Equal(UsageTimeAttribution.HistoricalUnknown, r3.TimeAttribution);
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, r3.AccountAttributionBasis);
    }

    [Fact]
    public async Task ATTR9_StableIdentityDoesNotAttribute_NewInstanceBacklog()
    {
        using var harness = new Harness();
        await harness.Accounts.AddAccountAsync(new CreateAccountInput("user@example.com"));
        var rpcA = harness.Rpc.Endpoint(40001);
        rpcA.IdentityEmail = "user@example.com";
        rpcA.Trajectories["convA"] = Summary("convA");
        rpcA.GeneratorMetadataRawJson["convA"] = "[" + EntryJson("convA", "rA", "100", "40") + "]";
        var ledger = harness.CreateLedger();
        var discovery = harness.CreateDiscovery(40001);
        var service = harness.CreateService(ledger: ledger, discovery: discovery);
        await service.RunCollectionCycleAsync();

        // B appears with backlog while the sandwich identity (before/after) is stable for BOTH
        // endpoints: the backlog must still be Unattributed — B has no established continuity.
        harness.LastInspector!.SetEndpoints(40001, 40002);
        var rpcB = harness.Rpc.Endpoint(40002);
        rpcB.IdentityEmail = "user@example.com";
        rpcB.Trajectories["convB"] = Summary("convB");
        rpcB.GeneratorMetadataRawJson["convB"] = "[" + EntryJson("convB", "rB", "500", "50") + "]";
        await service.RunCollectionCycleAsync();

        var bBacklog = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("convB", "rB"));
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, bBacklog.AccountAttributionBasis);
        Assert.Null(bBacklog.AccountId);
        // ...while A's own forward call in the same cycle is properly attributed.
        rpcA.Trajectories["convA"] = Summary("convA", steps: 4);
        rpcA.GeneratorMetadataRawJson["convA"] = "[" + EntryJson("convA", "rA", "100", "40") + "," + EntryJson("convA", "rA2", "110", "40") + "]";
        await service.RunCollectionCycleAsync();
        var a2 = (await ledger.GetAllCallsAsync()).Single(call => call.CallKey == UsageCallKeys.Compute("convA", "rA2"));
        Assert.Equal(UsageAccountAttributionBasis.VerifiedObservation, a2.AccountAttributionBasis);
    }

    #endregion

    #region Lifecycle (K)

    [Fact]
    public async Task K_DisposeDuringBlockedRpc_CompletesBounded_AndStopsCollection()
    {
        using var harness = new Harness();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int gateEntries = 0;
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        rpc.Gate = cancellationToken =>
        {
            Interlocked.Increment(ref gateEntries);
            // Real RPCs honor cancellation; the gate stays pending only until shutdown cancels it.
            return Task.Delay(Timeout.Infinite, cancellationToken);
        };

        var service = harness.CreateService();
        service.Interval = TimeSpan.FromMilliseconds(20);
        service.Start();

        // Wait until a cycle is genuinely in flight inside the blocked RPC.
        var spin = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref gateEntries) == 0)
        {
            Assert.True(spin.Elapsed < TimeSpan.FromSeconds(10), "Cycle never reached the blocked RPC.");
            await Task.Delay(10);
        }

        // Bounded disposal must not deadlock on the in-flight cycle.
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        blocked.SetResult();
    }

    [Fact]
    public async Task K_RepeatedDispose_IsSafe()
    {
        using var harness = new Harness();
        var service = harness.CreateService();
        await service.DisposeAsync();
        await service.DisposeAsync();
    }

    [Fact]
    public async Task K_ConcurrentCycles_NeverOverlap()
    {
        using var harness = new Harness();
        int cycleEntries = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        rpc.Gate = _ =>
        {
            Interlocked.Increment(ref cycleEntries);
            return release.Task;
        };
        var ledger = harness.CreateLedger();
        var service = harness.CreateService(ledger: ledger);

        var first = service.RunCollectionCycleAsync();
        await Task.Yield();
        var second = await service.RunCollectionCycleAsync(); // must skip, not queue
        Assert.Equal(1, cycleEntries);
        release.SetResult();
        await first;
        Assert.NotNull(second);
    }

    private sealed class ConflictOnIngestLedger(DurableUsageCallLedger inner) : IUsageCallLedger
    {
        public string LedgerDirectoryPath => inner.LedgerDirectoryPath;

        public Task<UsageIngestResult> IngestBatchAsync(IReadOnlyList<UsageCallRecord> calls, CancellationToken cancellationToken = default) =>
            Task.FromException<UsageIngestResult>(new UsageLedgerConflictException(UsageCallKeys.Compute("c1", "r1"), "synthetic conflict"));

        public Task<IReadOnlyList<UsageCallRecord>> GetAllCallsAsync(CancellationToken cancellationToken = default) =>
            inner.GetAllCallsAsync(cancellationToken);

        public Task<IReadOnlyList<UsageCallRecord>> GetCallsByAccountAsync(string accountId, CancellationToken cancellationToken = default) =>
            inner.GetCallsByAccountAsync(accountId, cancellationToken);

        public Task<IReadOnlyList<UsageCallRecord>> GetUnattributedCallsAsync(CancellationToken cancellationToken = default) =>
            inner.GetUnattributedCallsAsync(cancellationToken);

        public Task<IReadOnlyList<UsageCallRecord>> GetObservationTimeCallsBetweenAsync(DateTimeOffset fromUtcInclusive, DateTimeOffset toUtcExclusive, CancellationToken cancellationToken = default) =>
            inner.GetObservationTimeCallsBetweenAsync(fromUtcInclusive, toUtcExclusive, cancellationToken);

        public Task<IReadOnlyList<UsageCallRecord>> GetCallsByModelAsync(string responseModelKey, CancellationToken cancellationToken = default) =>
            inner.GetCallsByModelAsync(responseModelKey, cancellationToken);
    }

    [Fact]
    public async Task COL1_NonCooperativeCycle_DisposalReturns_CycleCompletesWithoutODE()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        // The hook parks until the TEST releases it. The test releases only after observing
        // that the drain has completed disposal — a state that pre-fix ordering reaches while
        // the cycle is parked BEFORE its Release (completion signaled first), and that post-fix
        // ordering reaches only AFTER the cycle's Release. Parking at the completion-signal
        // position therefore makes the ownership race deterministic:
        // pre-fix  -> parked hook returns -> Release on a disposed semaphore -> ODE.
        // post-fix -> Release already happened before the signal; the parked hook returns to
        //            a completed handoff and the cycle completes without ODE.
        var hookReachedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeAfterHandoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        // The gate ignores cancellation entirely: the worst-case non-cooperative dependency.
        rpc.Gate = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var service = harness.CreateService();
        service.DisposalWait = TimeSpan.FromMilliseconds(200);
        service.AfterCompletionSignalForTest = () =>
        {
            hookReachedSignal.TrySetResult();
            resumeAfterHandoff.Task.Wait(TimeSpan.FromSeconds(10));
        };

        // 1-2. Start the cycle and park it inside the non-cooperative dependency.
        var cycle = service.RunCollectionCycleAsync();
        // 3. The cycle is provably inside the blocked dependency.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 4-5. Disposal returns within its bounded wait; the cycle still owns the semaphore.
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.ResourcesDisposedForTest, "Resources must stay alive while the cycle is stalled.");

        // 7. Release the blocked dependency; the cycle unwinds (via disposal cancellation) and
        //    enters its finally, where the ownership handoff happens.
        release.SetResult();
        await hookReachedSignal.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The drain completes disposal at the completion signal. Pre-fix, this happens while
        // the cycle is parked before its Release; post-fix, after it.
        if (service.DrainTaskForTest is { } drain)
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(service.ResourcesDisposedForTest);

        // 8-9. Let the cycle finish. It must complete WITHOUT ObjectDisposedException: the
        //      semaphore was released by the cycle BEFORE disposal was authorized.
        resumeAfterHandoff.SetResult();
        try
        {
            var completed = await cycle;
            Assert.NotNull(completed);
        }
        catch (OperationCanceledException)
        {
            // Expected unwind path after disposal cancellation.
        }
        catch (ObjectDisposedException ex)
        {
            Assert.Fail($"Ownership race: semaphore was disposed beneath the active cycle. {ex.Message}");
        }

        // 10-11. Drain finished; resources are disposed exactly once.
        Assert.True(service.ResourcesDisposedForTest);
    }

    [Fact]
    public async Task COL2_CooperativeCancellation_DrainsImmediately()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        rpc.Gate = cancellationToken =>
        {
            entered.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        };
        var service = harness.CreateService();
        service.DisposalWait = TimeSpan.FromSeconds(5);

        var cycle = service.RunCollectionCycleAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
    }

    [Fact]
    public async Task COL3_LateCycle_AfterDispose_DrainsAndFaultsAreObserved()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        rpc.Gate = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var service = harness.CreateService();
        service.DisposalWait = TimeSpan.FromMilliseconds(200);

        _ = service.RunCollectionCycleAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // The cycle is still owned; resources stay alive. Releasing the gate lets the cycle
        // hit the disposal cancellation and unwind; the drain then disposes resources once.
        Assert.False(service.ResourcesDisposedForTest);
        release.SetResult();
        if (service.DrainTaskForTest is { } drain)
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(service.ResourcesDisposedForTest);

        // Non-cancellation faults from a cycle are observed through the sanitized log even
        // when the cycle's own task is detached.
        var faultGate = new TaskCompletionSource();
        var rpc2 = harness.Rpc.Endpoint(40002);
        rpc2.Trajectories["c2"] = Summary("c2");
        rpc2.GeneratorMetadataRawJson["c2"] = "[" + EntryJson("c2", "r2", "100", "40") + "]";
        rpc2.Gate = _ => faultGate.Task;
        var service2 = harness.CreateService(ledger: harness.CreateLedger(), discovery: harness.CreateDiscovery(40002));
        service2.DisposalWait = TimeSpan.FromSeconds(5);
        var faultingCycle = service2.RunCollectionCycleAsync();
        faultGate.SetException(new InvalidOperationException("late-cycle-fault-marker"));
        await faultingCycle.WaitAsync(TimeSpan.FromSeconds(10)); // isolation: fault logged, cycle completes
        Assert.Contains("late-cycle-fault-marker", string.Join(Environment.NewLine, harness.Logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task COL4_RepeatedDispose_WhileDrainPending_IsSafe()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        rpc.Gate = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var service = harness.CreateService();
        service.DisposalWait = TimeSpan.FromMilliseconds(200);

        _ = service.RunCollectionCycleAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var firstDrain = service.DrainTaskForTest;

        // Second disposal while the drain is still pending: must not double-dispose.
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(firstDrain, service.DrainTaskForTest);

        release.SetResult();
        if (service.DrainTaskForTest is { } drain)
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(service.ResourcesDisposedForTest);
    }

    [Fact]
    public async Task COL5_NoNewCycleAdmitted_AfterDisposal()
    {
        using var harness = new Harness();
        var service = harness.CreateService();
        await service.DisposeAsync();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            () => service.RunCollectionCycleAsync());
    }

    [Fact]
    public async Task COL6_Dispose_WithNoActiveCycle_CompletesImmediately()
    {
        using var harness = new Harness();
        var service = harness.CreateService();

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.ResourcesDisposedForTest);
        Assert.Null(service.DrainTaskForTest);
    }

    [Fact]
    public async Task COL7_ResourcesDisposed_OnlyAfterActiveOwnershipEnds()
    {
        using var harness = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        var rpc = harness.Rpc.Endpoint(40001);
        rpc.IdentityEmail = "user@example.com";
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        rpc.Gate = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };
        var service = harness.CreateService();
        service.DisposalWait = TimeSpan.FromMilliseconds(200);

        _ = service.RunCollectionCycleAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // While the stalled cycle still owns the semaphore, resources must NOT be disposed.
        Assert.False(service.ResourcesDisposedForTest);

        release.SetResult();
        if (service.DrainTaskForTest is { } drain)
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(service.ResourcesDisposedForTest);
    }

    [Fact]
    public async Task K_LedgerConflict_SurfacedNotRetried()
    {
        using var harness = new Harness();
        var realLedger = harness.CreateLedger();
        var ledger = new ConflictOnIngestLedger(realLedger);

        var rpc = harness.Rpc.Endpoint(40001);
        rpc.Trajectories["c1"] = Summary("c1");
        rpc.GeneratorMetadataRawJson["c1"] = "[" + EntryJson("c1", "r1", "100", "40") + "]";
        var service = harness.CreateService(ledger: ledger);

        var diagnostics = await service.RunCollectionCycleAsync();

        Assert.False(diagnostics.IntegrityAvailable);
        Assert.NotNull(diagnostics.LastError);
        Assert.Empty(await realLedger.GetAllCallsAsync()); // nothing was fabricated
    }

    #endregion
}
