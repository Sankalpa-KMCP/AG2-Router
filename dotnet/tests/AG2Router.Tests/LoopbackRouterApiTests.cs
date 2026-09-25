using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AG2Router.AG2.Routing;
using AG2Router.App.Server;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using AG2Router.Windows.Lifecycle;
using Xunit;

namespace AG2Router.Tests;

public sealed class LoopbackRouterApiTests : IAsyncDisposable
{
    private readonly LoopbackServer _server = new();
    private readonly HttpClient _client = new();
    private readonly InMemoryRegistryAccessor _registry = new();
    private readonly FakeAutoRouter _autoRouter = new();
    private WindowsRegistryAutostartService? _autostartService;

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private async Task StartAsync()
    {
        if (_server.BoundPort != 0) return;
        _autostartService = new WindowsRegistryAutostartService(_registry, @"C:\Tools\AG2Router.exe");
        await _server.StartAsync(
            requestedPort: 0,
            autoRouter: _autoRouter,
            autostartService: _autostartService
        );
        _client.BaseAddress = new Uri(_server.BoundUrl);
    }

    [Fact]
    public async Task GetConfig_ReturnsCurrentRouterConfig()
    {
        await StartAsync();
        using var response = await _client.GetAsync("/api/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("config").GetProperty("autoSwitchEnabled").GetBoolean());
        Assert.Equal(15, doc.RootElement.GetProperty("config").GetProperty("lowQuotaThresholdPercent").GetInt32());
    }

    [Fact]
    public async Task PostConfig_UpdatesAndReturnsConfig()
    {
        await StartAsync();
        var update = new RouterConfigDto(AutoSwitchEnabled: false, LowQuotaThresholdPercent: 20, MinimumCandidateQuotaPercent: 35);
        using var response = await _client.PostAsJsonAsync("/api/config", update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("config").GetProperty("autoSwitchEnabled").GetBoolean());
        Assert.Equal(20, doc.RootElement.GetProperty("config").GetProperty("lowQuotaThresholdPercent").GetInt32());
        Assert.Equal(35, doc.RootElement.GetProperty("config").GetProperty("minimumCandidateQuotaPercent").GetInt32());
    }

    [Fact]
    public async Task PostConfig_WhenPollingIntervalChanges_InvokesCallback()
    {
        TimeSpan observedInterval = TimeSpan.Zero;
        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(
                requestedPort: 0,
                autoRouter: _autoRouter,
                onPollingIntervalChanged: interval => observedInterval = interval
            );
            using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };

            var update = new RouterConfigDto(PollingIntervalMs: 8000);
            using var response = await client.PostAsJsonAsync("/api/config", update);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(TimeSpan.FromMilliseconds(8000), observedInterval);
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetRouterStatus_ReturnsDetailedState()
    {
        await StartAsync();
        using var response = await _client.GetAsync("/api/router/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("IDLE", doc.RootElement.GetProperty("router").GetProperty("state").GetString());
        Assert.True(doc.RootElement.GetProperty("router").GetProperty("autoSwitchEnabled").GetBoolean());
    }

    [Fact]
    public async Task PostResetRecovery_ClearsManualRecoveryState()
    {
        await StartAsync();
        using var response = await _client.PostAsync("/api/router/reset-recovery", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(1, _autoRouter.ResetManualRecoveryCallCount);
    }

    [Fact]
    public async Task AutostartEndpoints_GetAndPostLifecycle()
    {
        await StartAsync();

        // Initially disabled
        using var get1 = await _client.GetAsync("/api/settings/autostart");
        Assert.Equal(HttpStatusCode.OK, get1.StatusCode);
        using var doc1 = JsonDocument.Parse(await get1.Content.ReadAsStringAsync());
        Assert.False(doc1.RootElement.GetProperty("enabled").GetBoolean());
        Assert.True(doc1.RootElement.GetProperty("supported").GetBoolean());

        // Enable autostart
        using var post1 = await _client.PostAsJsonAsync("/api/settings/autostart", new AutostartRequest(true));
        Assert.Equal(HttpStatusCode.OK, post1.StatusCode);
        using var postDoc1 = JsonDocument.Parse(await post1.Content.ReadAsStringAsync());
        Assert.True(postDoc1.RootElement.GetProperty("enabled").GetBoolean());
        Assert.True(_autostartService!.IsAutostartEnabled());

        // Disable autostart
        using var post2 = await _client.PostAsJsonAsync("/api/settings/autostart", new AutostartRequest(false));
        Assert.Equal(HttpStatusCode.OK, post2.StatusCode);
        using var postDoc2 = JsonDocument.Parse(await post2.Content.ReadAsStringAsync());
        Assert.False(postDoc2.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(_autostartService.IsAutostartEnabled());
    }

    [Fact]
    public async Task Endpoints_RejectDnsRebindingAndForeignOrigin()
    {
        await StartAsync();

        // DNS rebinding host
        using var reboundReq = new HttpRequestMessage(HttpMethod.Get, "/api/config");
        reboundReq.Headers.Host = "attacker.example";
        using var reboundRes = await _client.SendAsync(reboundReq);
        Assert.Equal(HttpStatusCode.Forbidden, reboundRes.StatusCode);

        // Foreign origin on POST
        using var foreignReq = new HttpRequestMessage(HttpMethod.Post, "/api/settings/autostart")
        {
            Content = JsonContent.Create(new AutostartRequest(true))
        };
        foreignReq.Headers.Add("Origin", "http://malicious.example");
        using var foreignRes = await _client.SendAsync(foreignReq);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRes.StatusCode);
    }

    private sealed class FakeAutoRouter : INativeAutoRouter
    {
        private RouterConfigDto _config = new(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        public int ResetManualRecoveryCallCount { get; private set; }

        public RouterConfigDto GetConfig() => _config;

        public RouterConfigDto UpdateConfig(RouterConfigDto updates)
        {
            _config = updates;
            return _config;
        }

        public RouterStatusDto GetStatus() => new(
            State: RoutingSafetyGateState.Idle,
            AutoSwitchEnabled: _config.AutoSwitchEnabled,
            ActiveAccountId: "acc_1",
            ActiveAccountEmail: "user@example.com",
            PendingTargetAccountId: null,
            LastEvaluatedAt: DateTimeOffset.UtcNow.ToString("O"),
            LastDecisionReason: "Fake status",
            Config: _config
        );

        public void ResetManualRecovery()
        {
            ResetManualRecoveryCallCount++;
        }

        public ManualSwitchToken NotifyManualSwitchStarted(string targetAccountId) => new(1);
        public Task NotifyManualSwitchCompletedAsync(ManualSwitchToken token, NativeSwitchResult result) => Task.CompletedTask;

        public Task<SelectionResult> EvaluateCycleAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new SelectionResult(false, "Fake", null, null, null, Array.Empty<CandidateEvaluation>()));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
