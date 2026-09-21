using System.Net.Http;
using AG2Router.AG2.Adapter;
using AG2Router.AG2.Discovery;
using AG2Router.AG2.Rpc;
using Xunit;

namespace AG2Router.Tests;

public class AG2LiveAdapterTests
{
    private readonly DiscoveredProcessRaw _validProc = new(
        ProcessId: 12345,
        Name: "language_server.exe",
        CommandLine: @"C:\antigravity\resources\bin\language_server.exe --standalone --csrf_token 00000000-0000-0000-0000-000000000000",
        ExecutablePath: @"C:\antigravity\resources\bin\language_server.exe"
    );

    [Fact]
    public async Task GetStatusAsync_WhenConnected_ReturnsConnectedWithActivity()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 51768 })
        };

        var rpc = new MockAG2RpcClient
        {
            GetAllCascadeTrajectoriesFunc = (_, _, _) => Task.FromResult<RawTrajectoriesResponse?>(new RawTrajectoriesResponse
            {
                TrajectorySummaries = new Dictionary<string, RawTrajectorySummary>
                {
                    ["t1"] = new() { Status = "CASCADE_RUN_STATUS_RUNNING" }
                }
            })
        };

        var detector = new AG2ProcessDetector(inspector, rpc);
        var adapter = new AG2LiveAdapter(detector, rpc);

        var status = await adapter.GetStatusAsync();

        Assert.True(status.Connected);
        Assert.Equal("DISCOVERED", status.Status);
        Assert.NotNull(status.Activity);
        Assert.Equal("BUSY", status.Activity.State);
    }

    [Fact]
    public async Task GetCurrentAccountAsync_WhenRpcFails_ReturnsNull()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 51768 })
        };

        var rpc = new MockAG2RpcClient
        {
            GetUserStatusFunc = (_, _, _) => throw new HttpRequestException("Simulated RPC connection failure")
        };

        var detector = new AG2ProcessDetector(inspector, rpc);
        var adapter = new AG2LiveAdapter(detector, rpc);

        var account = await adapter.GetCurrentAccountAsync();

        Assert.Null(account);
    }

    [Fact]
    public async Task GetActivityStateAsync_WhenNoProcessRunning_ReturnsOffline()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(Array.Empty<DiscoveredProcessRaw>())
        };

        var detector = new AG2ProcessDetector(inspector, new MockAG2RpcClient());
        var adapter = new AG2LiveAdapter(detector);

        var activity = await adapter.GetActivityStateAsync();

        Assert.Equal("OFFLINE", activity.State);
        Assert.Equal(0, activity.TotalTrajectories);
    }
}
