using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AG2Router.App.Server;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed class LoopbackSwitchApiTests : IAsyncDisposable
{
    private readonly LoopbackServer _server = new();
    private readonly HttpClient _client = new();
    private readonly FakeSwitchCoordinator _coordinator = new();

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private async Task StartAsync()
    {
        if (_server.BoundPort != 0) return;
        await _server.StartAsync(requestedPort: 0, switchCoordinator: _coordinator);
        _client.BaseAddress = new Uri(_server.BoundUrl);
    }

    [Theory]
    [InlineData(SwitchResultCodes.Success, HttpStatusCode.OK)]
    [InlineData(SwitchResultCodes.TargetNotFound, HttpStatusCode.NotFound)]
    [InlineData(SwitchResultCodes.TargetNotVaulted, HttpStatusCode.Conflict)]
    [InlineData(SwitchResultCodes.AlreadyActive, HttpStatusCode.Conflict)]
    [InlineData(SwitchResultCodes.Ag2Busy, HttpStatusCode.Conflict)]
    [InlineData(SwitchResultCodes.TelemetryUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(SwitchResultCodes.UnsafeProcess, HttpStatusCode.Conflict)]
    [InlineData(SwitchResultCodes.SwitchInProgress, HttpStatusCode.Conflict)]
    [InlineData(SwitchResultCodes.SwitchFailedRolledBack, HttpStatusCode.InternalServerError)]
    [InlineData(SwitchResultCodes.SwitchFailedRollbackFailed, HttpStatusCode.InternalServerError)]
    [InlineData(SwitchResultCodes.Cancelled, HttpStatusCode.RequestTimeout)]
    public async Task SwitchEndpointMapsNonSecretNativeResult(string code, HttpStatusCode expectedStatus)
    {
        await StartAsync();
        _coordinator.NextCode = code;

        using var status = await _client.GetAsync("/api/switching/status");
        string token = status.Headers.GetValues("X-AG2-Switch-Token").Single();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/acc_target/switch")
        {
            Content = JsonContent.Create(new ExplicitSwitchRequest(true))
        };
        request.Headers.Add("X-AG2-Switch-Token", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic-secret", json);
        if (code == SwitchResultCodes.SwitchFailedRollbackFailed)
            Assert.True(doc.RootElement.GetProperty("manualRecoveryRequired").GetBoolean());
    }

    [Fact]
    public async Task SwitchEndpointRequiresPerLaunchTokenAndExplicitConfirmation()
    {
        await StartAsync();
        using var missingToken = await _client.PostAsJsonAsync(
            "/api/accounts/acc_target/switch", new ExplicitSwitchRequest(true));
        Assert.Equal(HttpStatusCode.Forbidden, missingToken.StatusCode);

        using var status = await _client.GetAsync("/api/switching/status");
        string token = status.Headers.GetValues("X-AG2-Switch-Token").Single();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/acc_target/switch")
        {
            Content = JsonContent.Create(new ExplicitSwitchRequest(false))
        };
        request.Headers.Add("X-AG2-Switch-Token", token);
        using var noConfirmation = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, noConfirmation.StatusCode);
        Assert.Equal(0, _coordinator.CallCount);
    }

    [Fact]
    public async Task SwitchEndpointRejectsDnsRebindingHostAndForeignOrigin()
    {
        await StartAsync();
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/switching/status");
        rebound.Headers.Host = "attacker.example";
        using var reboundResponse = await _client.SendAsync(rebound);
        Assert.Equal(HttpStatusCode.Forbidden, reboundResponse.StatusCode);

        using var status = await _client.GetAsync("/api/switching/status");
        string token = status.Headers.GetValues("X-AG2-Switch-Token").Single();
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/acc_target/switch")
        {
            Content = JsonContent.Create(new ExplicitSwitchRequest(true))
        };
        foreign.Headers.Add("X-AG2-Switch-Token", token);
        foreign.Headers.Add("Origin", "http://attacker.example");
        using var foreignResponse = await _client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
        Assert.Equal(0, _coordinator.CallCount);
    }

    [Fact]
    public async Task SwitchingStatusUsesCoordinatorState()
    {
        await StartAsync();
        using var response = await _client.GetAsync("/api/switching/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("IDLE", doc.RootElement.GetProperty("status").GetProperty("currentState").GetString());
    }

    private sealed class FakeSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public string NextCode { get; set; } = SwitchResultCodes.Success;
        public int CallCount { get; private set; }

        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null);

        public Task<NativeSwitchResult> SwitchAsync(
            string targetAccountId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            bool success = NextCode == SwitchResultCodes.Success;
            string state = success ? NativeSwitchStates.Complete :
                NextCode == SwitchResultCodes.SwitchFailedRolledBack ? NativeSwitchStates.RolledBack :
                NativeSwitchStates.Failed;
            return Task.FromResult(new NativeSwitchResult(
                "tx_synthetic", success, NextCode, state, targetAccountId,
                "target@example.com", "source", "source@example.com",
                "--csrf_token synthetic-secret", [], DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                ManualRecoveryRequired: NextCode == SwitchResultCodes.SwitchFailedRollbackFailed));
        }
    }
}
