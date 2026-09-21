using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
        _winCredStore.Seed("gemini:antigravity", "antigravity", validBlob);

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
}
