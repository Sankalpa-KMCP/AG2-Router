using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Vault;
using AG2Router.App.Server;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class LoopbackServerAccountApiTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryWinCredStore _winCredStore;
    private readonly FakeDpapiProvider _fakeDpapi;
    private readonly SessionVault _sessionVault;
    private readonly InMemoryAccountStore _accountStore;
    private readonly MockAG2Adapter _mockAdapter;
    private readonly AccountEnrollmentService _enrollmentService;
    private readonly LoopbackServer _server;
    private readonly HttpClient _client;

    public LoopbackServerAccountApiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_server_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _winCredStore = new InMemoryWinCredStore();
        _fakeDpapi = new FakeDpapiProvider();
        _sessionVault = new SessionVault(_tempDir, _fakeDpapi);
        _accountStore = new InMemoryAccountStore();
        _mockAdapter = new MockAG2Adapter();
        _enrollmentService = new AccountEnrollmentService(_mockAdapter, _winCredStore, _sessionVault, _accountStore);

        _server = new LoopbackServer();
        _client = new HttpClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();

        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private async Task EnsureServerStartedAsync()
    {
        if (_server.BoundPort == 0)
        {
            await _server.StartAsync(
                requestedPort: 0,
                statusProvider: null,
                accountStore: _accountStore,
                sessionVault: _sessionVault,
                enrollmentService: _enrollmentService
            );
            _client.BaseAddress = new Uri(_server.BoundUrl);
        }
    }

    [Fact]
    public async Task GetAccounts_ReturnsEnrichedList_WithVaultAndActiveStatus()
    {
        await EnsureServerStartedAsync();

        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "user1@example.com",
            Name: "User One",
            Priority: 1
        ));
        await _sessionVault.SaveSessionAsync(acc.Id, "session-blob"u8.ToArray());
        await _accountStore.SetActiveAccountIdAsync(acc.Id);

        var response = await _client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var accountsArr = doc.RootElement.GetProperty("accounts");
        Assert.Equal(1, accountsArr.GetArrayLength());

        var first = accountsArr[0];
        Assert.Equal(acc.Id, first.GetProperty("id").GetString());
        Assert.Equal("user1@example.com", first.GetProperty("email").GetString());
        Assert.True(first.GetProperty("hasVaultedSession").GetBoolean());
        Assert.True(first.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task GetAccounts_ReportsVaultAvailabilityOnlyAfterSuccessfulDecryption()
    {
        await EnsureServerStartedAsync();
        var account = await _accountStore.AddAccountAsync(new CreateAccountInput("vaulted@example.com"));
        await _sessionVault.SaveSessionAsync(account.Id, "synthetic-session"u8.ToArray());
        Assert.True(await ReadAvailabilityAsync(account.Id));

        var path = _sessionVault.GetVaultPath();
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["records"]![account.Id]!["encryptedPayloadBase64"] = "AAAA";
        await File.WriteAllTextAsync(path, document.ToJsonString());
        Assert.True(await _sessionVault.HasSessionAsync(account.Id));
        Assert.False(await ReadAvailabilityAsync(account.Id));
    }

    private async Task<bool> ReadAvailabilityAsync(string accountId)
    {
        using var response = await _client.GetAsync("/api/accounts");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accounts").EnumerateArray()
            .Single(account => account.GetProperty("id").GetString() == accountId)
            .GetProperty("hasVaultedSession").GetBoolean();
    }

    [Fact]
    public async Task PostAccount_WithValidInput_Returns201_AndCreatesAccount()
    {
        await EnsureServerStartedAsync();

        var payload = new
        {
            email = "new_dev@example.com",
            name = "New Dev",
            priority = 2,
            isReserve = false,
            notes = "Test creation"
        };

        var response = await _client.PostAsJsonAsync("/api/accounts", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("account", out var accountElem));
        Assert.Equal("new_dev@example.com", accountElem.GetProperty("email").GetString());

        // Duplicate returns 400
        var dupResponse = await _client.PostAsJsonAsync("/api/accounts", payload);
        Assert.Equal(HttpStatusCode.BadRequest, dupResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteAccount_RemovesAccountAndVaultSession_Returns200()
    {
        await EnsureServerStartedAsync();

        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "to_delete@example.com",
            Priority: 1
        ));
        await _sessionVault.SaveSessionAsync(acc.Id, "session-blob"u8.ToArray());

        var response = await _client.DeleteAsync($"/api/accounts/{acc.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(acc.Id, doc.RootElement.GetProperty("removedId").GetString());
        Assert.True(doc.RootElement.GetProperty("vaultRecordDeleted").GetBoolean());

        // Confirm removed from store and vault
        Assert.Null(await _accountStore.GetAccountAsync(acc.Id));
        Assert.False(await _sessionVault.HasSessionAsync(acc.Id));

        // Deleting non-existent returns 404
        var notFoundResponse = await _client.DeleteAsync("/api/accounts/non_existent_id");
        Assert.Equal(HttpStatusCode.NotFound, notFoundResponse.StatusCode);
    }

    [Fact]
    public async Task PostEnrollCurrent_TriggersEnrollment_Returns200()
    {
        await EnsureServerStartedAsync();

        _mockAdapter.CurrentAccount = new AccountIdentityDto("enrolled@example.com", "Enrolled User");
        byte[] validBlob = Encoding.UTF8.GetBytes("{\"token\":\"fake-token-12345\",\"auth_method\":\"oauth\"}");
        _winCredStore.Seed("gemini:antigravity", "enrolled@example.com", validBlob);

        var payload = new
        {
            name = "Enrolled Via API",
            priority = 1,
            notes = "API enrollment test"
        };

        var response = await _client.PostAsJsonAsync("/api/accounts/enroll-current", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("isNew").GetBoolean());
        Assert.Equal("enrolled@example.com", doc.RootElement.GetProperty("account").GetProperty("email").GetString());
    }

    [Fact]
    public async Task PostSwitch_WithoutCoordinator_Returns501()
    {
        await EnsureServerStartedAsync();

        var response = await _client.PostAsync("/api/accounts/acc_test/switch", null);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("Native account switching is not configured", json);
    }

    [Fact]
    public async Task GetAccounts_IncludesAliasTotalCountAndActiveAccountId_WithoutExposingSecrets()
    {
        await EnsureServerStartedAsync();

        var acc1 = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "legacy@example.com",
            Name: "Legacy Account",
            Priority: 1
        ));

        var acc2 = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "aliased@example.com",
            Name: "Aliased Account",
            Priority: 2,
            Alias: "Work"
        ));

        await _accountStore.SetActiveAccountIdAsync(acc2.Id);

        var response = await _client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(acc2.Id, root.GetProperty("activeAccountId").GetString());

        var accounts = root.GetProperty("accounts");
        Assert.Equal(2, accounts.GetArrayLength());

        var first = accounts[0];
        Assert.Equal(acc1.Id, first.GetProperty("id").GetString());
        Assert.False(first.GetProperty("isActive").GetBoolean());
        Assert.True(first.TryGetProperty("alias", out var a1) ? a1.ValueKind == JsonValueKind.Null : true);

        var second = accounts[1];
        Assert.Equal(acc2.Id, second.GetProperty("id").GetString());
        Assert.True(second.GetProperty("isActive").GetBoolean());
        Assert.Equal("Work", second.GetProperty("alias").GetString());

        // Prove no session blobs, tokens, or DPAPI payloads are exposed
        Assert.DoesNotContain("session-blob", json);
        Assert.DoesNotContain("token", json);
        Assert.DoesNotContain("dpapi", json);
    }

    [Fact]
    public async Task PatchAccountAlias_WithValidTrimmedString_UpdatesAliasAndPersists()
    {
        await EnsureServerStartedAsync();

        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "to_alias@example.com",
            Name: "Target Name",
            Priority: 1
        ));
        await _sessionVault.SaveSessionAsync(acc.Id, "session-blob-secret"u8.ToArray());

        var payload = new { alias = "  Production Primary  " };
        var response = await _client.PatchAsJsonAsync($"/api/accounts/{acc.Id}", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        var account = doc.RootElement.GetProperty("account");
        Assert.Equal("Production Primary", account.GetProperty("alias").GetString());
        Assert.Equal("to_alias@example.com", account.GetProperty("email").GetString());
        Assert.Equal("Target Name", account.GetProperty("name").GetString());
        Assert.True(account.GetProperty("hasVaultedSession").GetBoolean());

        // Verify persisted state in store
        var loaded = await _accountStore.GetAccountAsync(acc.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Production Primary", loaded.Alias);
        Assert.Equal("Target Name", loaded.Name);

        // Verify session vault was not touched or corrupted
        var session = await _sessionVault.GetSessionAsync(acc.Id);
        Assert.NotNull(session);
        Assert.Equal("session-blob-secret", Encoding.UTF8.GetString(session));
    }

    [Fact]
    public async Task PatchAccountAlias_WithEmptyOrWhitespace_ClearsAlias()
    {
        await EnsureServerStartedAsync();

        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "clear_me@example.com",
            Alias: "Existing Alias"
        ));

        // 1. Whitespace clears alias
        var wsResponse = await _client.PatchAsJsonAsync($"/api/accounts/{acc.Id}", new { alias = "   " });
        Assert.Equal(HttpStatusCode.OK, wsResponse.StatusCode);

        var wsJson = await wsResponse.Content.ReadAsStringAsync();
        using var wsDoc = JsonDocument.Parse(wsJson);
        Assert.True(wsDoc.RootElement.GetProperty("account").TryGetProperty("alias", out var a1) ? a1.ValueKind == JsonValueKind.Null : true);

        var loaded1 = await _accountStore.GetAccountAsync(acc.Id);
        Assert.Null(loaded1?.Alias);

        // Re-set alias
        await _accountStore.UpdateAccountAsync(acc.Id, new UpdateAccountInput(Alias: "Temp"));

        // 2. Empty string clears alias
        var emptyResponse = await _client.PatchAsJsonAsync($"/api/accounts/{acc.Id}", new { alias = "" });
        Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);

        var loaded2 = await _accountStore.GetAccountAsync(acc.Id);
        Assert.Null(loaded2?.Alias);
    }

    [Fact]
    public async Task PatchAccountAlias_WithNull_ClearsAlias()
    {
        await EnsureServerStartedAsync();

        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "null_clear@example.com",
            Alias: "Initial Alias"
        ));

        var response = await _client.PatchAsJsonAsync($"/api/accounts/{acc.Id}", new { alias = (string?)null });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var loaded = await _accountStore.GetAccountAsync(acc.Id);
        Assert.Null(loaded?.Alias);
    }

    [Fact]
    public async Task PatchAccountAlias_WithUnknownId_Returns404()
    {
        await EnsureServerStartedAsync();

        var response = await _client.PatchAsJsonAsync("/api/accounts/non_existent_id", new { alias = "New" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PatchAccountAlias_WithInvalidJsonOrPayload_Returns400()
    {
        await EnsureServerStartedAsync();

        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(Email: "malformed@example.com"));

        // 1. Invalid JSON
        using var badContent = new StringContent("{ not-json }", Encoding.UTF8, "application/json");
        var badRes = await _client.PatchAsync($"/api/accounts/{acc.Id}", badContent);
        Assert.Equal(HttpStatusCode.BadRequest, badRes.StatusCode);

        // 2. Invalid ID traversal
        var travRes = await _client.PatchAsJsonAsync("/api/accounts/bad%5Cid", new { alias = "Test" });
        Assert.Equal(HttpStatusCode.BadRequest, travRes.StatusCode);
    }

    [Fact]
    public async Task PatchAccountAlias_CrossSiteOrForeignOrigin_Returns403()
    {
        await EnsureServerStartedAsync();

        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(Email: "sec_test@example.com"));

        // 1. Foreign origin
        using var foreignReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/accounts/{acc.Id}")
        {
            Content = JsonContent.Create(new { alias = "Hacked" })
        };
        foreignReq.Headers.Add("Origin", "http://evil.com");
        var foreignRes = await _client.SendAsync(foreignReq);
        Assert.Equal(HttpStatusCode.Forbidden, foreignRes.StatusCode);

        // 2. Sec-Fetch-Site cross-site
        using var crossSiteReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/accounts/{acc.Id}")
        {
            Content = JsonContent.Create(new { alias = "CrossSite" })
        };
        crossSiteReq.Headers.Add("Sec-Fetch-Site", "cross-site");
        var crossSiteRes = await _client.SendAsync(crossSiteReq);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteRes.StatusCode);
    }

    [Fact]
    public async Task GetStatus_ExposesCanonicalQuotaModels_WhilePreservingRawModelsAndCredits()
    {
        var server = new LoopbackServer();
        try
        {
            var rawModels = new List<ModelQuotaDto>
            {
                new("Gemini 2.5 Pro", "gemini-2.5-pro", 0.90, "2026-09-21T22:00:00Z", false),
                new("Gemini 2.5 Pro (Thinking)", "gemini-2.5-pro", 0.90, "2026-09-21T22:00:00Z", false)
            };
            var canonicalModels = new List<CanonicalModelQuotaDto>
            {
                new("tier:gemini-2.5-pro", "Gemini 2.5 Pro", "gemini-2.5-pro", 0.90, "2026-09-21T22:00:00Z", false, ["Standard", "Thinking"])
            };
            var quotaSnapshot = new QuotaSnapshotDto(
                Timestamp: "2026-09-21T18:00:00Z",
                Models: rawModels,
                PromptCredits: new CreditPoolDto(1500, 2000, 500),
                FlowCredits: new CreditPoolDto(300, 500, 200),
                CanonicalModels: canonicalModels
            );

            var systemStatus = new SystemStatusDto(
                Status: "ok",
                Ag2: new Ag2StatusDto(true, "CONNECTED", null, "Antigravity 2 Connected"),
                Router: new RouterStatusDto("IDLE", false, null, null, null, null, "Idle", new RouterConfigDto()),
                Telemetry: new TelemetryDto(
                    CurrentAccount: new AccountIdentityDto("dev@example.com", "Dev"),
                    Quota: quotaSnapshot,
                    Activity: null,
                    TotalAvailableQuotaPercent: null,
                    LastSuccessfulTelemetry: "2026-09-21T18:00:00Z"
                )
            );

            await server.StartAsync(0, statusProvider: () => systemStatus);
            using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };

            var response = await client.GetAsync("/api/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var quotaElem = doc.RootElement.GetProperty("telemetry").GetProperty("quota");

            // 1. Raw models preserved
            var rawElem = quotaElem.GetProperty("models");
            Assert.Equal(2, rawElem.GetArrayLength());

            // 2. Canonical models exposed
            var canonicalElem = quotaElem.GetProperty("canonicalModels");
            Assert.Equal(1, canonicalElem.GetArrayLength());
            var c0 = canonicalElem[0];
            Assert.Equal("tier:gemini-2.5-pro", c0.GetProperty("key").GetString());
            Assert.Equal("Gemini 2.5 Pro", c0.GetProperty("label").GetString());
            Assert.Equal(0.90, c0.GetProperty("remainingFraction").GetDouble(), 2);
            Assert.False(c0.GetProperty("isExhausted").GetBoolean());
            Assert.Equal(2, c0.GetProperty("modes").GetArrayLength());

            // 3. Credit pools segregated
            var promptElem = quotaElem.GetProperty("promptCredits");
            Assert.Equal(1500, promptElem.GetProperty("availableCredits").GetInt32());
            Assert.Equal(2000, promptElem.GetProperty("monthlyCredits").GetInt32());

            var flowElem = quotaElem.GetProperty("flowCredits");
            Assert.Equal(300, flowElem.GetProperty("availableCredits").GetInt32());
            Assert.Equal(500, flowElem.GetProperty("monthlyCredits").GetInt32());
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task DeleteAccount_WhenAccountIsActive_Returns409Conflict()
    {
        await EnsureServerStartedAsync();

        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "active_delete@example.com",
            Priority: 1
        ));
        await _accountStore.SetActiveAccountIdAsync(acc.Id);

        var response = await _client.DeleteAsync($"/api/accounts/{acc.Id}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("Cannot delete currently active account", json);

        // Verify account was NOT deleted
        Assert.NotNull(await _accountStore.GetAccountAsync(acc.Id));
    }

    [Fact]
    public async Task GetAccounts_DerivesHasVaultedSessionFromSessionVaultAuthoritatively()
    {
        await EnsureServerStartedAsync();

        // Add account claiming HasVaultedSession = true in metadata, but nothing in vault
        var acc = await _accountStore.AddAccountAsync(new CreateAccountInput(
            Email: "unvaulted@example.com",
            HasVaultedSession: true
        ));

        var response = await _client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var accountsArr = doc.RootElement.GetProperty("accounts");

        JsonElement? found = null;
        foreach (var item in accountsArr.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == acc.Id)
            {
                found = item;
                break;
            }
        }

        Assert.NotNull(found);
        // Authoritative vault check overrides unverified metadata
        Assert.False(found.Value.GetProperty("hasVaultedSession").GetBoolean());
    }

    [Fact]
    public async Task LoopbackServer_RejectsCrossSiteFetchSite_AcrossApi()
    {
        await EnsureServerStartedAsync();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        req.Headers.Add("Sec-Fetch-Site", "cross-site");

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task LoopbackServer_RejectsForeignOrigin_AcrossApi()
    {
        await EnsureServerStartedAsync();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/accounts");
        req.Headers.Add("Origin", "https://malicious.evil.com");

        using var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
