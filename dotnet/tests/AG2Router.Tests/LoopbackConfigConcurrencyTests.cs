using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.App.Server;
using AG2Router.App.Services;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed class LoopbackConfigConcurrencyTests : IAsyncDisposable
{
    private readonly LoopbackServer _server = new();
    private readonly HttpClient _client = new();
    private readonly string _tempDir;

    public LoopbackConfigConcurrencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AG2_LoopbackConcurrency_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private NativeAutoRouter CreateTestRouter(RouterConfigDto? initialConfig = null)
    {
        return new NativeAutoRouter(
            new SimpleAccountStore(),
            new SimpleSessionVault(),
            new MockAG2Adapter(),
            new SimpleSwitchCoordinator(),
            initialConfig: initialConfig ?? new RouterConfigDto(AutoSwitchEnabled: true, PollingIntervalMs: 10000),
            safetyGate: new RoutingSafetyGate()
        );
    }

    // 10. LoopbackServer end-to-end HTTP concurrency test:
    // Concurrent HTTP POST /api/config with barrier in onPollingIntervalChangedAsync,
    // proving final runtime timer matches final committed config.
    [Fact]
    public async Task PostConfig_ConcurrentHttpRequests_MaintainsLatestWinsPollingInterval()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        var aEnteredCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await _server.StartAsync(
            requestedPort: 0,
            statusProvider: () => coordinator.CurrentStatus,
            autoRouter: router,
            onPollingIntervalChangedAsync: async (interval, gen) =>
            {
                if (gen == 2)
                {
                    aEnteredCallback.TrySetResult();
                    await resumeA.Task;
                }
                coordinator.UpdateInterval(interval, gen);
            }
        );

        _client.BaseAddress = new Uri(_server.BoundUrl);

        // Request A: 5000ms
        var updateA = new RouterConfigDto(PollingIntervalMs: 5000);
        var requestATask = _client.PostAsJsonAsync("/api/config", updateA);

        // Wait until Request A has committed configuration and paused inside onPollingIntervalChangedAsync
        await aEnteredCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Request B: 30000ms runs while Request A is paused
        var updateB = new RouterConfigDto(PollingIntervalMs: 30000);
        using var responseB = await _client.PostAsJsonAsync("/api/config", updateB);
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);

        // Coordinator currently has Request B's interval (30s, gen 3)
        Assert.Equal(TimeSpan.FromSeconds(30), coordinator.Interval);
        Assert.Equal(3, coordinator.LastAppliedConfigGeneration);

        // Resume Request A's delayed publication
        resumeA.TrySetResult();
        using var responseA = await requestATask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, responseA.StatusCode);

        // Assert: coordinator Interval remains 30s, generation remains 3, and router config is 30s
        Assert.Equal(TimeSpan.FromSeconds(30), coordinator.Interval);
        Assert.Equal(3, coordinator.LastAppliedConfigGeneration);
        Assert.Equal(30000, router.GetConfig().PollingIntervalMs);
    }

    [Fact]
    public async Task PostConfig_SynchronousGenerationCallback_InvokedWithCorrectGeneration()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        long observedGeneration = 0;
        TimeSpan observedInterval = TimeSpan.Zero;

        await _server.StartAsync(
            requestedPort: 0,
            statusProvider: () => coordinator.CurrentStatus,
            autoRouter: router,
            onPollingIntervalChangedWithGeneration: (interval, gen) =>
            {
                observedInterval = interval;
                observedGeneration = gen;
                coordinator.UpdateInterval(interval, gen);
            }
        );

        _client.BaseAddress = new Uri(_server.BoundUrl);

        var update = new RouterConfigDto(PollingIntervalMs: 7500);
        using var response = await _client.PostAsJsonAsync("/api/config", update);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(TimeSpan.FromMilliseconds(7500), observedInterval);
        Assert.Equal(2, observedGeneration);
        Assert.Equal(TimeSpan.FromMilliseconds(7500), coordinator.Interval);
        Assert.Equal(2, coordinator.LastAppliedConfigGeneration);
    }

    private sealed class SimpleAccountStore : IAccountStore
    {
        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AccountMetadata>>([]);
        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<AccountMetadata?>(null);
        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult<AccountMetadata?>(null);
        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default) => Task.FromResult<AccountMetadata?>(null);
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default) => Task.FromResult<AccountMetadata?>(null);
    }

    private sealed class SimpleSessionVault : ISessionVault
    {
        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public string GetVaultPath() => "C:\\fake\\vault";
    }

    private sealed class SimpleSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null);
        public Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present."));
        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NativeSwitchResult(
                "tx_fake", true, SwitchResultCodes.Success, NativeSwitchStates.Complete,
                targetAccountId, "target@example.com", null, null, "SUCCESS", [],
                DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O")));
        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
