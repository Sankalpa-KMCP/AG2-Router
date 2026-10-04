using AG2Router.AG2.Discovery;
using AG2Router.AG2.Rpc;
using AG2Router.AG2.Usage;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Multi-instance discovery for usage collection: every validated language-server instance is
/// enumerated with unchanged provenance rules, duplicates collapse, vanished processes are
/// skipped, and untrusted candidates are rejected exactly as for telemetry.
/// </summary>
public class UsageInstanceDiscoveryTests
{
    private static DiscoveredProcessRaw Process(int pid, string name = "language_server.exe", string? path = null, string? commandLine = null) =>
        new(pid, name, commandLine ?? "--standalone --csrf_token tok", path ?? $@"C:\antigravity\resources\bin\language_server.exe", DateTime.UtcNow);

    private sealed class StaticInspector(params DiscoveredProcessRaw[] processes) : IProcessInspector
    {
        public Func<int, IReadOnlyList<int>>? PortResolver { get; set; }

        public Task<IReadOnlyList<DiscoveredProcessRaw>> FindProcessesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(processes);

        public Task<IReadOnlyList<int>> GetListeningPortsAsync(int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PortResolver?.Invoke(processId) ?? (IReadOnlyList<int>)new[] { 40000 + processId % 1000 });

        public bool IsPidAlive(int processId, DateTime? expectedStartTime = null) => true;
    }

    private sealed class ProbeClient : IAG2RpcClient
    {
        public HashSet<int> HealthyPorts { get; } = new();

        public Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthyPorts.Contains(port));

        public Task<RawUserStatusResponse?> GetUserStatusAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<RawUserStatusResponse?>(null);

        public Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default) =>
            Task.FromResult<RawTrajectoriesResponse?>(null);

        public Task<IReadOnlyList<RawGeneratorMetadataEntry>> GetCascadeTrajectoryGeneratorMetadataAsync(
            int port, string protocol, string csrfToken, string cascadeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RawGeneratorMetadataEntry>>([]);
    }

    [Fact]
    public async Task Discover_NoProcesses_ReturnsEmpty()
    {
        var discovery = new UsageInstanceDiscovery(new StaticInspector(), new ProbeClient());
        var endpoints = await discovery.DiscoverAsync();
        Assert.Empty(endpoints);
    }

    [Fact]
    public async Task Discover_SingleValidInstance_ReturnsOneEndpoint()
    {
        var probe = new ProbeClient();
        probe.HealthyPorts.Add(41001);
        var discovery = new UsageInstanceDiscovery(
            new StaticInspector(Process(11)) { PortResolver = _ => new[] { 41001, 41002 } },
            probe);

        var endpoints = await discovery.DiscoverAsync();

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(11, endpoint.ProcessId);
        Assert.Equal(41001, endpoint.Port);
        Assert.Equal("https", endpoint.Protocol);
    }

    [Fact]
    public async Task Discover_MultipleValidInstances_ReturnsAll()
    {
        var probe = new ProbeClient();
        probe.HealthyPorts.Add(41001);
        probe.HealthyPorts.Add(42001);
        var discovery = new UsageInstanceDiscovery(
            new StaticInspector(Process(11), Process(22)) { PortResolver = pid => new[] { pid == 11 ? 41001 : 42001 } },
            probe);

        var endpoints = await discovery.DiscoverAsync();

        Assert.Equal(2, endpoints.Count);
        Assert.Equal(11, endpoints[0].ProcessId);
        Assert.Equal(22, endpoints[1].ProcessId);
    }

    [Fact]
    public async Task Discover_UntrustedCandidates_Rejected()
    {
        // A same-named process outside Antigravity provenance is rejected exactly as for telemetry.
        var untrusted = new DiscoveredProcessRaw(33, "language_server.exe", "--standalone",
            @"C:\evil\tools\language_server.exe", DateTime.UtcNow);
        var discovery = new UsageInstanceDiscovery(new StaticInspector(untrusted), new ProbeClient());

        var endpoints = await discovery.DiscoverAsync();

        Assert.Empty(endpoints);
    }

    [Fact]
    public async Task Discover_DuplicateProcessEntries_Deduped()
    {
        var probe = new ProbeClient();
        probe.HealthyPorts.Add(41001);
        var discovery = new UsageInstanceDiscovery(
            new StaticInspector(Process(11), Process(11)) { PortResolver = _ => new[] { 41001 } },
            probe);

        var endpoints = await discovery.DiscoverAsync();

        Assert.Single(endpoints);
    }

    [Fact]
    public async Task Discover_ProcessVanishesDuringPortLookup_SkipsIt_KeepsOthers()
    {
        var probe = new ProbeClient();
        probe.HealthyPorts.Add(42001);
        var inspector = new StaticInspector(Process(11), Process(22))
        {
            PortResolver = pid => pid == 11
                ? throw new InvalidOperationException("Process has exited.")
                : new[] { 42001 },
        };
        var discovery = new UsageInstanceDiscovery(inspector, probe);

        var endpoints = await discovery.DiscoverAsync();

        var endpoint = Assert.Single(endpoints);
        Assert.Equal(22, endpoint.ProcessId);
    }

    [Fact]
    public async Task Discover_MissingCsrfToken_SkipsEndpoint()
    {
        var noToken = new DiscoveredProcessRaw(44, "language_server.exe", "--standalone",
            @"C:\antigravity\resources\bin\language_server.exe", DateTime.UtcNow);
        var probe = new ProbeClient();
        probe.HealthyPorts.Add(44001);
        var discovery = new UsageInstanceDiscovery(
            new StaticInspector(noToken) { PortResolver = _ => new[] { 44001 } },
            probe);

        Assert.Empty(await discovery.DiscoverAsync());
    }
}
