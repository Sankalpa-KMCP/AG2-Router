using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
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

    private async Task<string> GetIntentTokenAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/switching/intent");
        request.Headers.Add("X-AG2-Intent-Request", "1");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("X-AG2-Switch-Token").Single();
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

        string token = await GetIntentTokenAsync();
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

        string token = await GetIntentTokenAsync();
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

        string token = await GetIntentTokenAsync();
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
        var status = doc.RootElement.GetProperty("status");
        Assert.Equal("IDLE", status.GetProperty("currentState").GetString());
        Assert.False(status.GetProperty("quarantineActive").GetBoolean());
        Assert.Equal("NONE", status.GetProperty("journalRecoveryState").GetString());
        Assert.False(response.Headers.Contains("X-AG2-Switch-Token"));
        Assert.DoesNotContain("switchToken", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, JournalRecoveryStates.ActionRequired)]
    [InlineData(true, JournalRecoveryStates.RestartRequired)]
    [InlineData(true, JournalRecoveryStates.NotResolvable)]
    [InlineData(true, JournalRecoveryStates.None)]
    [InlineData(true, JournalRecoveryStates.Unknown)]
    [InlineData(false, JournalRecoveryStates.None)]
    public async Task SwitchingStatusSerializesQuarantineActiveAndJournalRecoveryState(
        bool quarantineActive, string journalRecoveryState)
    {
        await StartAsync();
        _coordinator.StatusToReturn = new NativeSwitchStatus(
            ActiveTransactionId: "tx_test",
            CurrentState: quarantineActive ? NativeSwitchStates.Failed : NativeSwitchStates.Idle,
            LastResult: null,
            QuarantineActive: quarantineActive,
            JournalRecoveryState: journalRecoveryState);

        using var response = await _client.GetAsync("/api/switching/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var status = doc.RootElement.GetProperty("status");
        Assert.Equal(quarantineActive, status.GetProperty("quarantineActive").GetBoolean());
        Assert.Equal(journalRecoveryState, status.GetProperty("journalRecoveryState").GetString());
    }


    [Theory]
    [InlineData("Origin", "http://attacker.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task ForeignBrowserCannotObtainSwitchIntent(string header, string value)
    {
        await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/switching/intent");
        request.Headers.Add("X-AG2-Intent-Request", "1");
        request.Headers.Add(header, value);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("X-AG2-Switch-Token"));
    }

    [Theory]
    [InlineData(JournalResolutionStatus.NoJournal, null, HttpStatusCode.OK, false)]
    [InlineData(JournalResolutionStatus.CleanCleanupCompleted, "CLEAN_RECORDED_REMOVED", HttpStatusCode.OK, false)]
    [InlineData(JournalResolutionStatus.ResolvedRestartRequired, null, HttpStatusCode.OK, true)]
    [InlineData(JournalResolutionStatus.NotResolvable, "CORRUPT_JOURNAL", HttpStatusCode.Conflict, false)]
    [InlineData(JournalResolutionStatus.ProofFailed, "LIVE_IDENTITY_UNAVAILABLE", HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(JournalResolutionStatus.ProofFailed, "CREDENTIAL_MISMATCH", HttpStatusCode.Conflict, false)]
    [InlineData(JournalResolutionStatus.PersistenceFailure, "IO_ERROR", HttpStatusCode.InternalServerError, false)]
    public async Task ResolveQuarantineEndpoint_MapsStatusAndRestartsCorrectly(
        JournalResolutionStatus status,
        string? reasonCode,
        HttpStatusCode expectedStatusCode,
        bool expectedRestartRequired)
    {
        await StartAsync();
        _coordinator.NextResolutionResult = new JournalResolutionResult(
            Status: status,
            Message: "Resolution result message --csrf_token synthetic-secret",
            CoherentAccountId: status == JournalResolutionStatus.ResolvedRestartRequired ? "acc_target" : null,
            RestartRequired: expectedRestartRequired,
            ReasonCode: reasonCode
        );

        string token = await GetIntentTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/switching/resolve-quarantine")
        {
            Content = JsonContent.Create(new ResolveQuarantineRequest(true))
        };
        request.Headers.Add("X-AG2-Switch-Token", token);
        using var response = await _client.SendAsync(request);
        Assert.Equal(expectedStatusCode, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(status.ToString(), doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(expectedRestartRequired, doc.RootElement.GetProperty("restartRequired").GetBoolean());
        Assert.DoesNotContain("synthetic-secret", json);
    }

    [Fact]
    public async Task ResolveQuarantineEndpoint_RequiresTokenAndExplicitConfirmation()
    {
        await StartAsync();

        // 1. Missing token -> 403 Forbidden
        using var missingToken = await _client.PostAsJsonAsync(
            "/api/switching/resolve-quarantine", new ResolveQuarantineRequest(true));
        Assert.Equal(HttpStatusCode.Forbidden, missingToken.StatusCode);
        Assert.Equal(0, _coordinator.ResolveCallCount);

        // 2. Invalid token -> 403 Forbidden
        using var invalidToken = new HttpRequestMessage(HttpMethod.Post, "/api/switching/resolve-quarantine")
        {
            Content = JsonContent.Create(new ResolveQuarantineRequest(true))
        };
        invalidToken.Headers.Add("X-AG2-Switch-Token", "invalid-token");
        using var invalidResponse = await _client.SendAsync(invalidToken);
        Assert.Equal(HttpStatusCode.Forbidden, invalidResponse.StatusCode);
        Assert.Equal(0, _coordinator.ResolveCallCount);

        // 3. Confirm == false -> 400 BadRequest
        string token = await GetIntentTokenAsync();
        using var noConfirmation = new HttpRequestMessage(HttpMethod.Post, "/api/switching/resolve-quarantine")
        {
            Content = JsonContent.Create(new ResolveQuarantineRequest(false))
        };
        noConfirmation.Headers.Add("X-AG2-Switch-Token", token);
        using var noConfirmResponse = await _client.SendAsync(noConfirmation);
        Assert.Equal(HttpStatusCode.BadRequest, noConfirmResponse.StatusCode);
        Assert.Equal(0, _coordinator.ResolveCallCount);
    }

    [Fact]
    public async Task ResolveQuarantineEndpoint_NeverLeaksArbitraryExceptionTextOrFilePaths()
    {
        await StartAsync();
        _coordinator.ThrowOnResolve = new InvalidOperationException("Failed to open file: C:\\Users\\SecretUser\\AppData\\Local\\AG2-Router\\secret-token-xyz123.txt with error 0x80070005");

        string token = await GetIntentTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/switching/resolve-quarantine")
        {
            Content = JsonContent.Create(new ResolveQuarantineRequest(true))
        };
        request.Headers.Add("X-AG2-Switch-Token", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("An unexpected error occurred during quarantine resolution.", responseBody);
        Assert.DoesNotContain("SecretUser", responseBody);
        Assert.DoesNotContain("secret-token-xyz123", responseBody);
        Assert.DoesNotContain("AppData", responseBody);
        Assert.DoesNotContain("0x80070005", responseBody);
        Assert.DoesNotContain("InvalidOperationException", responseBody);
    }

    [Fact]
    public async Task ResolveQuarantineEndpoint_MalformedJsonPayload_ReturnsStaticErrorMessageWithoutParserDetails()
    {
        await StartAsync();
        string token = await GetIntentTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/switching/resolve-quarantine")
        {
            Content = new StringContent("{ \"confirm\": true, MALFORMED_SYNTAX", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-AG2-Switch-Token", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("A valid resolution confirmation request is required.", responseBody);
        Assert.DoesNotContain("JsonException", responseBody);
        Assert.DoesNotContain("MALFORMED_SYNTAX", responseBody);
        Assert.DoesNotContain("line", responseBody);
    }

    [Theory]
    [InlineData("Origin", "http://attacker.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task ResolveQuarantineEndpoint_RejectsCrossSiteAndForeignOrigin(string header, string value)
    {
        await StartAsync();
        string token = await GetIntentTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/switching/resolve-quarantine")
        {
            Content = JsonContent.Create(new ResolveQuarantineRequest(true))
        };
        request.Headers.Add("X-AG2-Switch-Token", token);
        request.Headers.Add(header, value);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _coordinator.ResolveCallCount);
    }

    [Fact]
    public async Task ResolveQuarantineEndpoint_NeverSerializesArbitraryCoordinatorProse()
    {
        await StartAsync();
        _coordinator.NextResolutionResult = new JournalResolutionResult(
            Status: JournalResolutionStatus.ProofFailed,
            Message: "ARBITRARY_COORDINATOR_DIAGNOSTIC_PROSE: user=SecretAdmin path=C:\\internal\\journal.json token=raw-secret-12345",
            CoherentAccountId: null,
            RestartRequired: false,
            ReasonCode: "CREDENTIAL_MISMATCH"
        );

        string token = await GetIntentTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/switching/resolve-quarantine")
        {
            Content = JsonContent.Create(new ResolveQuarantineRequest(true))
        };
        request.Headers.Add("X-AG2-Switch-Token", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        string responseBody = await response.Content.ReadAsStringAsync();

        // Must contain the static endpoint-owned allowlisted message
        Assert.Contains("Windows Credential Manager payload does not match the vaulted session.", responseBody);

        // Must NOT contain any part of the coordinator's arbitrary prose
        Assert.DoesNotContain("ARBITRARY_COORDINATOR_DIAGNOSTIC_PROSE", responseBody);
        Assert.DoesNotContain("SecretAdmin", responseBody);
        Assert.DoesNotContain("journal.json", responseBody);
        Assert.DoesNotContain("raw-secret-12345", responseBody);
    }

    [Fact]
    public async Task ResolveQuarantineEndpoint_WhenUnknownReasonCodeProvided_MapsToUnknownAndNeverSerializesRawReason()
    {
        await StartAsync();
        _coordinator.NextResolutionResult = new JournalResolutionResult(
            Status: JournalResolutionStatus.ProofFailed,
            Message: "Arbitrary coordinator message with user Alice and internal note",
            CoherentAccountId: null,
            RestartRequired: false,
            ReasonCode: @"private-alpha Alice C:\Users\Alice\journal-note token-xyz"
        );

        string token = await GetIntentTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/switching/resolve-quarantine")
        {
            Content = JsonContent.Create(new ResolveQuarantineRequest(true))
        };
        request.Headers.Add("X-AG2-Switch-Token", token);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        // Assert fixed generic public reason code
        Assert.Equal("UNKNOWN", doc.RootElement.GetProperty("reasonCode").GetString());

        // Assert endpoint-owned fixed message
        Assert.Equal("Account state coherence could not be verified.", doc.RootElement.GetProperty("message").GetString());

        // Assert no raw reason substrings cross the boundary
        Assert.DoesNotContain("private-alpha", json);
        Assert.DoesNotContain("Alice", json);
        Assert.DoesNotContain("journal-note", json);
        Assert.DoesNotContain("token-xyz", json);

        // Assert no coordinator Message prose crosses the boundary
        Assert.DoesNotContain("Arbitrary coordinator message", json);
        Assert.DoesNotContain("internal note", json);
    }

    private sealed class FakeSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public string NextCode { get; set; } = SwitchResultCodes.Success;
        public int CallCount { get; private set; }
        public JournalResolutionResult NextResolutionResult { get; set; } = new(JournalResolutionStatus.NoJournal, "No switch journal present.");
        public Exception? ThrowOnResolve { get; set; }
        public int ResolveCallCount { get; private set; }

        public NativeSwitchStatus StatusToReturn { get; set; } = new(null, NativeSwitchStates.Idle, null);
        public NativeSwitchStatus GetStatus() => StatusToReturn;

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

        public Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            if (ThrowOnResolve != null)
            {
                throw ThrowOnResolve;
            }
            return Task.FromResult(NextResolutionResult);
        }

        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
