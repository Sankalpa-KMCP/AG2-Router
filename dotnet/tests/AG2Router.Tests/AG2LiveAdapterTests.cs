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

    [Fact]
    public async Task GetActivityStateAsync_WhenRpcPayloadIsMissing_ReturnsUnknown()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 51768 })
        };
        var rpc = new MockAG2RpcClient
        {
            GetAllCascadeTrajectoriesFunc = (_, _, _) => Task.FromResult<RawTrajectoriesResponse?>(null)
        };
        var adapter = new AG2LiveAdapter(new AG2ProcessDetector(inspector, rpc), rpc);

        var activity = await adapter.GetActivityStateAsync();

        Assert.Equal("UNKNOWN", activity.State);
    }

    [Fact]
    public async Task AccountQuotaObservationBindsIdentityAndQuotaToOneRpcPayload()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 51768 })
        };
        int userStatusCalls = 0;
        var rpc = new MockAG2RpcClient
        {
            GetUserStatusFunc = (_, _, _) =>
            {
                Interlocked.Increment(ref userStatusCalls);
                return Task.FromResult<RawUserStatusResponse?>(new RawUserStatusResponse
                {
                    UserStatus = new RawUserStatus { Email = "source@example.com" }
                });
            }
        };
        var adapter = new AG2LiveAdapter(new AG2ProcessDetector(inspector, rpc), rpc);

        var observation = await adapter.GetAccountQuotaObservationAsync();

        Assert.Equal("source@example.com", observation.Account?.Email);
        Assert.NotNull(observation.Quota);
        Assert.Equal(1, userStatusCalls);
    }

    [Fact]
    public async Task RequestedModelRemainsUnknownWithoutAuthoritativeRuntimeField()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => throw new InvalidOperationException(
                "Requested model lookup must not probe the live process")
        };
        var rpc = new MockAG2RpcClient();
        var adapter = new AG2LiveAdapter(new AG2ProcessDetector(inspector, rpc), rpc);

        var observation = await adapter.GetRequestedModelAsync();

        Assert.Null(observation.Account);
        Assert.Null(observation.ModelOrTier);
    }

    [Fact]
    public async Task GetRequestedModelAsync_WithConfiguredModel_ReturnsActiveAccountAndCanonicalModel_WhenActiveAccountAuthenticated()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { _validProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 51768 })
        };
        var rpc = new MockAG2RpcClient
        {
            GetUserStatusFunc = (_, _, _) => Task.FromResult<RawUserStatusResponse?>(new RawUserStatusResponse
            {
                UserStatus = new RawUserStatus { Email = "active@example.com" }
            })
        };
        var adapter = new AG2LiveAdapter(
            new AG2ProcessDetector(inspector, rpc),
            rpc,
            configuredModelProvider: () => "  GEMINI-2.5-PRO  ");

        var observation = await adapter.GetRequestedModelAsync();

        Assert.NotNull(observation.Account);
        Assert.Equal("active@example.com", observation.Account.Email);
        Assert.Equal("gemini-2.5-pro", observation.ModelOrTier);
    }

    [Fact]
    public async Task GetRequestedModelAsync_WithConfiguredModel_ReturnsNull_WhenActiveAccountOffline()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(Array.Empty<DiscoveredProcessRaw>())
        };
        var rpc = new MockAG2RpcClient();
        var adapter = new AG2LiveAdapter(
            new AG2ProcessDetector(inspector, rpc),
            rpc,
            configuredModelProvider: () => "gemini-2.5-pro");

        var observation = await adapter.GetRequestedModelAsync();

        Assert.Null(observation.Account);
        Assert.Null(observation.ModelOrTier);
    }

    [Fact]
    public async Task GetRequestedModelAsync_WithWhitespaceConfiguredModel_ReturnsNull()
    {
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => throw new InvalidOperationException(
                "Whitespace model key lookup must not probe the live process")
        };
        var rpc = new MockAG2RpcClient();
        var adapter = new AG2LiveAdapter(
            new AG2ProcessDetector(inspector, rpc),
            rpc,
            configuredModelProvider: () => "   \t\n  ");

        var observation = await adapter.GetRequestedModelAsync();

        Assert.Null(observation.Account);
        Assert.Null(observation.ModelOrTier);
    }
}
