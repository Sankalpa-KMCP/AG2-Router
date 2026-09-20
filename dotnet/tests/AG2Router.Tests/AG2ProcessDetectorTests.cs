using AG2Router.AG2.Discovery;
using AG2Router.AG2.Rpc;
using Xunit;

namespace AG2Router.Tests;

public class MockProcessInspector : IProcessInspector
{
    public Func<Task<IReadOnlyList<DiscoveredProcessRaw>>>? FindProcessesFunc { get; set; }
    public Func<int, Task<IReadOnlyList<int>>>? GetListeningPortsFunc { get; set; }
    public Func<int, DateTime?, bool>? IsPidAliveFunc { get; set; }

    public int FindProcessesCallCount { get; private set; }

    public Task<IReadOnlyList<DiscoveredProcessRaw>> FindProcessesAsync(CancellationToken cancellationToken = default)
    {
        FindProcessesCallCount++;
        return FindProcessesFunc != null
            ? FindProcessesFunc()
            : Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(Array.Empty<DiscoveredProcessRaw>());
    }

    public Task<IReadOnlyList<int>> GetListeningPortsAsync(int processId, CancellationToken cancellationToken = default)
    {
        return GetListeningPortsFunc != null
            ? GetListeningPortsFunc(processId)
            : Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
    }

    public bool IsPidAlive(int processId, DateTime? expectedStartTime = null)
    {
        return IsPidAliveFunc == null || IsPidAliveFunc(processId, expectedStartTime);
    }
}

public class MockAG2RpcClient : IAG2RpcClient
{
    public Func<int, string, string, Task<bool>>? ProbePortFunc { get; set; }
    public Func<int, string, string, Task<RawUserStatusResponse?>>? GetUserStatusFunc { get; set; }
    public Func<int, string, string, Task<RawTrajectoriesResponse?>>? GetAllCascadeTrajectoriesFunc { get; set; }

    public Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        return ProbePortFunc != null
            ? ProbePortFunc(port, protocol, csrfToken)
            : Task.FromResult(true);
    }

    public Task<RawUserStatusResponse?> GetUserStatusAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        return GetUserStatusFunc != null
            ? GetUserStatusFunc(port, protocol, csrfToken)
            : Task.FromResult<RawUserStatusResponse?>(new RawUserStatusResponse());
    }

    public Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        return GetAllCascadeTrajectoriesFunc != null
            ? GetAllCascadeTrajectoriesFunc(port, protocol, csrfToken)
            : Task.FromResult<RawTrajectoriesResponse?>(new RawTrajectoriesResponse());
    }
}

public class AG2ProcessDetectorTests
{
    private readonly DiscoveredProcessRaw _validProc = new(
        ProcessId: 12345,
        Name: "language_server.exe",
        CommandLine: @"C:\antigravity\resources\bin\language_server.exe --standalone --csrf_token 11111111-2222-3333-4444-555555555555",
        ExecutablePath: @"C:\antigravity\resources\bin\language_server.exe",
        StartTime: DateTime.UtcNow.AddHours(-1)
    );

    [Fact]
    public async Task DiscoverAsync_ColdDiscovery_FindsAndCachesSession()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 51768, 51769 }),
            IsPidAliveFunc = (_, _) => true
        };

        var rpc = new MockAG2RpcClient
        {
            ProbePortFunc = (port, proto, _) => Task.FromResult(port == 51768 && proto == "https")
        };

        var detector = new AG2ProcessDetector(inspector, rpc);

        var result = await detector.DiscoverAsync();

        Assert.True(result.IsRunning);
        Assert.Equal("DISCOVERED", result.Status);
        Assert.NotNull(result.ProcessInfo);
        Assert.Equal(12345, result.ProcessInfo.Pid);
        Assert.Equal(51768, result.ProcessInfo.Port);
        Assert.Equal("https", result.ProcessInfo.Protocol);
        Assert.Equal("1111...5555", result.ProcessInfo.CsrfToken);
        Assert.Equal(1, inspector.FindProcessesCallCount);

        // Second call should hit the in-memory cache without calling FindProcessesAsync
        var cachedResult = await detector.DiscoverAsync();
        Assert.True(cachedResult.IsRunning);
        Assert.Equal("HEALTHY", cachedResult.Status);
        Assert.Equal(1, inspector.FindProcessesCallCount); // Still 1!
    }

    [Fact]
    public async Task DiscoverAsync_OfflineCooldown_ThrottlesEnumeration()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(Array.Empty<DiscoveredProcessRaw>())
        };

        var detector = new AG2ProcessDetector(inspector, offlineCooldown: TimeSpan.FromSeconds(15));

        var result1 = await detector.DiscoverAsync();
        Assert.False(result1.IsRunning);
        Assert.Equal("OFFLINE", result1.Status);
        Assert.Equal(1, inspector.FindProcessesCallCount);

        // Second call within 15s should return cooldown immediately without calling FindProcessesAsync
        var result2 = await detector.DiscoverAsync();
        Assert.False(result2.IsRunning);
        Assert.Equal("OFFLINE", result2.Status);
        Assert.Contains("cooldown active", result2.Message);
        Assert.Equal(1, inspector.FindProcessesCallCount); // Still 1!
    }

    [Fact]
    public async Task DiscoverAsync_WhenPidDies_InvalidatesCacheAndRediscover()
    {
        bool pidAlive = true;
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 51768 }),
            IsPidAliveFunc = (_, _) => pidAlive
        };

        var detector = new AG2ProcessDetector(inspector, new MockAG2RpcClient());

        var res1 = await detector.DiscoverAsync();
        Assert.Equal("DISCOVERED", res1.Status);

        // Now process dies
        pidAlive = false;

        var res2 = await detector.DiscoverAsync();
        Assert.Equal("DISCOVERED", res2.Status); // Performed cold rediscovery
        Assert.Equal(2, inspector.FindProcessesCallCount);
    }

    [Fact]
    public async Task CircuitBreaker_ThreeFailures_InvalidatesSessionCache()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 51768 })
        };

        var detector = new AG2ProcessDetector(inspector, new MockAG2RpcClient(), failureThreshold: 3);

        // Populate session cache
        var _ = await detector.DiscoverAsync();
        Assert.NotNull(detector.GetCachedSession());

        detector.RecordRpcFailure(new Exception("Fail 1"));
        Assert.NotNull(detector.GetCachedSession());

        detector.RecordRpcFailure(new Exception("Fail 2"));
        Assert.NotNull(detector.GetCachedSession());

        detector.RecordRpcFailure(new Exception("Fail 3")); // Trips circuit breaker!
        Assert.Null(detector.GetCachedSession());
    }

    [Fact]
    public async Task PortProbingOrder_TriesHttpsFirst_ThenHttp()
    {
        var probeCalls = new List<(int port, string proto)>();

        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 4000, 5000 })
        };

        var rpc = new MockAG2RpcClient
        {
            ProbePortFunc = (port, proto, _) =>
            {
                probeCalls.Add((port, proto));
                // Only port 5000 over HTTP succeeds
                return Task.FromResult(port == 5000 && proto == "http");
            }
        };

        var detector = new AG2ProcessDetector(inspector, rpc);
        var res = await detector.DiscoverAsync();

        Assert.Equal("DISCOVERED", res.Status);
        Assert.Equal(5000, res.ProcessInfo!.Port);
        Assert.Equal("http", res.ProcessInfo.Protocol);

        // Verify probing order: 4000 https -> 4000 http -> 5000 https -> 5000 http
        Assert.Equal((4000, "https"), probeCalls[0]);
        Assert.Equal((4000, "http"), probeCalls[1]);
        Assert.Equal((5000, "https"), probeCalls[2]);
        Assert.Equal((5000, "http"), probeCalls[3]);
    }

    [Fact]
    public void WindowsProcessInspector_IsPidAlive_HandlesInvalidOrUnrelatedPid()
    {
        var inspector = new WindowsProcessInspector();
        Assert.False(inspector.IsPidAlive(-1));
        Assert.False(inspector.IsPidAlive(9999999));
        // Current test runner process is running, but is not language_server
        Assert.False(inspector.IsPidAlive(Environment.ProcessId));
    }
}
