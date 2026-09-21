using System.Net;
using System.Net.Http;
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
    [InlineData(SwitchResultCodes.SwitchFailedRollbackFailed, HttpStatusCode.ServiceUnavailable)]
    [InlineData(SwitchResultCodes.Cancelled, HttpStatusCode.RequestTimeout)]
    public async Task SwitchEndpointMapsNonSecretNativeResult(string code, HttpStatusCode expectedStatus)
    {
        await StartAsync();
        _coordinator.NextCode = code;

        using var response = await _client.PostAsync("/api/accounts/acc_target/switch", null);
        Assert.Equal(expectedStatus, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(code, doc.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("synthetic-secret", json);
        Assert.DoesNotContain("csrf", json, StringComparison.OrdinalIgnoreCase);
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

        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null);

        public Task<NativeSwitchResult> SwitchAsync(
            string targetAccountId,
            CancellationToken cancellationToken = default)
        {
            bool success = NextCode == SwitchResultCodes.Success;
            string state = success ? NativeSwitchStates.Complete :
                NextCode == SwitchResultCodes.SwitchFailedRolledBack ? NativeSwitchStates.RolledBack :
                NativeSwitchStates.Failed;
            return Task.FromResult(new NativeSwitchResult(
                "tx_synthetic", success, NextCode, state, targetAccountId,
                "target@example.com", "source", "source@example.com",
                "Synthetic non-secret result", [], DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O")));
        }
    }
}
