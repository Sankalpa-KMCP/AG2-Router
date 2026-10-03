using System.IO;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.App.Services;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public sealed class TelemetryPollingCoordinatorGenerationTests : IDisposable
{
    private readonly string _tempDir;

    public TelemetryPollingCoordinatorGenerationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AG2_GenTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private NativeAutoRouter CreateTestRouter(
        string? configPath = null,
        IDurableFileWriter? fileWriter = null,
        RouterConfigDto? initialConfig = null)
    {
        return new NativeAutoRouter(
            new SimpleAccountStore(),
            new SimpleSessionVault(),
            new MockAG2Adapter(),
            new SimpleSwitchCoordinator(),
            initialConfig: initialConfig ?? new RouterConfigDto(AutoSwitchEnabled: true, PollingIntervalMs: 10000),
            safetyGate: new RoutingSafetyGate(),
            configFilePath: configPath,
            fileWriter: fileWriter
        );
    }

    // 1. Barrier test reproducing the exact defect:
    // Request A commits 5s (gen 2), paused before UpdateInterval.
    // Request B commits 30s (gen 3), calls UpdateInterval(30s, 3).
    // Resume Request A, calls UpdateInterval(5s, 2).
    // Assert: UpdateInterval for A returns false, coordinator Interval remains 30s (gen 3), router config is 30s.
    [Fact]
    public async Task BarrierTest_ConcurrentConfigSaves_PreventsStaleTimerOverwrite()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        Assert.Equal(TimeSpan.FromSeconds(10), coordinator.Interval);
        Assert.Equal(1, coordinator.LastAppliedConfigGeneration);

        var aCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? aResult = null;

        var taskA = Task.Run(async () =>
        {
            var (cfgA, genA) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 5000 });
            aCommitted.TrySetResult();
            await resumeA.Task;
            aResult = coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgA.PollingIntervalMs), genA);
        });

        await aCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Request B runs and finishes while A is paused
        var (cfgB, genB) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 30000 });
        Assert.Equal(3, genB);
        bool bResult = coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgB.PollingIntervalMs), genB);
        Assert.True(bResult);
        Assert.Equal(TimeSpan.FromSeconds(30), coordinator.Interval);
        Assert.Equal(3, coordinator.LastAppliedConfigGeneration);

        // Resume Request A
        resumeA.TrySetResult();
        await taskA.WaitAsync(TimeSpan.FromSeconds(5));

        // Stale A update was rejected
        Assert.False(aResult);
        Assert.Equal(TimeSpan.FromSeconds(30), coordinator.Interval);
        Assert.Equal(3, coordinator.LastAppliedConfigGeneration);
        Assert.Equal(30000, router.GetConfig().PollingIntervalMs);
    }

    // 2. Normal order (A then B):
    // A (gen 2) applied, B (gen 3) applied -> final timer = B.
    [Fact]
    public async Task NormalOrder_AppliesInSequence_FinalTimerIsB()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        var (cfgA, genA) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 5000 });
        Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgA.PollingIntervalMs), genA));
        Assert.Equal(TimeSpan.FromSeconds(5), coordinator.Interval);
        Assert.Equal(genA, coordinator.LastAppliedConfigGeneration);

        var (cfgB, genB) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 30000 });
        Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgB.PollingIntervalMs), genB));
        Assert.Equal(TimeSpan.FromSeconds(30), coordinator.Interval);
        Assert.Equal(genB, coordinator.LastAppliedConfigGeneration);
    }

    // 3. Delayed A publication:
    // A (gen 2) applied after B (gen 3) -> rejected, final timer = B.
    [Fact]
    public async Task DelayedPublication_AppliesOlderGenerationAfterNewer_RejectsAndRetainsB()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        var (cfgA, genA) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 5000 });
        var (cfgB, genB) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 30000 });

        // B publishes first
        Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgB.PollingIntervalMs), genB));
        // Delayed A publishes later
        Assert.False(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgA.PollingIntervalMs), genA));

        Assert.Equal(TimeSpan.FromSeconds(30), coordinator.Interval);
        Assert.Equal(genB, coordinator.LastAppliedConfigGeneration);
    }

    // 4. Reverse completion attempt:
    // Attempt to apply older generation -> rejected.
    [Fact]
    public async Task ReverseCompletionAttempt_RejectsLowerGeneration()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        // Current router generation is 1
        var (cfg2, gen2) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 8000 });
        var (cfg3, gen3) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 12000 });

        Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfg3.PollingIntervalMs), gen3));
        Assert.False(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfg2.PollingIntervalMs), gen2));
        Assert.False(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(10000), 1));

        Assert.Equal(TimeSpan.FromMilliseconds(12000), coordinator.Interval);
        Assert.Equal(gen3, coordinator.LastAppliedConfigGeneration);
    }

    // 5. Same interval:
    // Same interval saved in A and B -> timer correct, generation updated.
    [Fact]
    public async Task SameInterval_SavedInMultipleUpdates_UpdatesGenerationCorrectly()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        var (cfgA, genA) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 15000 });
        Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgA.PollingIntervalMs), genA));
        Assert.Equal(TimeSpan.FromSeconds(15), coordinator.Interval);
        Assert.Equal(genA, coordinator.LastAppliedConfigGeneration);

        var (cfgB, genB) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 15000 });
        Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgB.PollingIntervalMs), genB));
        Assert.Equal(TimeSpan.FromSeconds(15), coordinator.Interval);
        Assert.Equal(genB, coordinator.LastAppliedConfigGeneration);
        Assert.True(genB > genA);
    }

    // 6. Fast -> slow (1 ms then 30 s):
    // If A (1 ms) is delayed after B (30 s), timer stays 30 s.
    [Fact]
    public async Task FastToSlow_WhenFastIsDelayed_TimerStaysSlow()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        var (cfgFast, genFast) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 1 });
        var (cfgSlow, genSlow) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 30000 });

        // Slow applies first
        Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgSlow.PollingIntervalMs), genSlow));
        // Delayed Fast applies second
        Assert.False(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgFast.PollingIntervalMs), genFast));

        Assert.Equal(TimeSpan.FromSeconds(30), coordinator.Interval);
        Assert.Equal(genSlow, coordinator.LastAppliedConfigGeneration);
    }

    // 7. Slow -> fast (30 s then 1 ms):
    // If A (30 s) is delayed after B (1 ms), timer stays 1 ms.
    [Fact]
    public async Task SlowToFast_WhenSlowIsDelayed_TimerStaysFast()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        var (cfgSlow, genSlow) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 30000 });
        var (cfgFast, genFast) = router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 1 });

        // Fast applies first
        Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgFast.PollingIntervalMs), genFast));
        // Delayed Slow applies second
        Assert.False(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfgSlow.PollingIntervalMs), genSlow));

        Assert.Equal(TimeSpan.FromMilliseconds(1), coordinator.Interval);
        Assert.Equal(genFast, coordinator.LastAppliedConfigGeneration);
    }

    // 8. Persistence failure:
    // If WriteAtomicAsync throws, config is not committed, generation not incremented, callback not invoked, timer unchanged.
    [Fact]
    public async Task PersistenceFailure_Throws_ConfigAndGenerationNotCommitted_TimerUnchanged()
    {
        string configPath = Path.Combine(_tempDir, "failing-config.json");
        var failingWriter = new FailingFileWriter();
        await using var router = CreateTestRouter(configPath, failingWriter);
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        Assert.Equal(TimeSpan.FromSeconds(10), coordinator.Interval);
        Assert.Equal(1, coordinator.LastAppliedConfigGeneration);
        Assert.Equal(1, router.ConfigGeneration);

        bool callbackInvoked = false;
        var ex = Assert.Throws<IOException>(() =>
        {
            var update = router.GetConfig() with { PollingIntervalMs = 5000 };
            var (updated, generation) = router.UpdateConfigWithGeneration(update);
            callbackInvoked = true;
            coordinator.UpdateInterval(TimeSpan.FromMilliseconds(updated.PollingIntervalMs), generation);
        });

        Assert.Contains("Simulated disk write failure", ex.Message);
        Assert.False(callbackInvoked);
        Assert.Equal(10000, router.GetConfig().PollingIntervalMs);
        Assert.Equal(1, router.ConfigGeneration);
        Assert.Equal(TimeSpan.FromSeconds(10), coordinator.Interval);
        Assert.Equal(1, coordinator.LastAppliedConfigGeneration);
    }

    // 9. R02 interaction:
    // Automatic admission lease held -> config save waits -> once released, config commits and publishes correct interval.
    [Fact]
    public async Task R02Interaction_AutomaticAdmissionLeaseHeld_SaveWaitsThenCommitsAndPublishes()
    {
        await using var router = CreateTestRouter();
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, autoRouter: router);

        var admissionLease = await router.AcquireInterruptionAdmissionAsync(default);
        var contendedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.ConfigAdmissionContended = () => contendedTcs.TrySetResult();

        Task<(RouterConfigDto Config, long Generation)>? saveTask = null;
        try
        {
            saveTask = Task.Run(() => router.UpdateConfigWithGeneration(router.GetConfig() with { PollingIntervalMs = 4000 }));

            // Wait for save task to detect contention and wait on interruption admission
            await contendedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(saveTask.IsCompleted);
            Assert.Equal(10000, router.GetConfig().PollingIntervalMs);
            Assert.Equal(1, router.ConfigGeneration);
            Assert.Equal(TimeSpan.FromSeconds(10), coordinator.Interval);

            // Release admission lease
            admissionLease.Dispose();

            // Save completes now
            var (cfg, gen) = await saveTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4000, cfg.PollingIntervalMs);
            Assert.Equal(2, gen);
            Assert.Equal(2, router.ConfigGeneration);

            // Publish update to coordinator
            Assert.True(coordinator.UpdateInterval(TimeSpan.FromMilliseconds(cfg.PollingIntervalMs), gen));
            Assert.Equal(TimeSpan.FromMilliseconds(4000), coordinator.Interval);
            Assert.Equal(2, coordinator.LastAppliedConfigGeneration);
        }
        finally
        {
            admissionLease.Dispose();
            if (saveTask != null) await saveTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
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

    private sealed class FailingFileWriter : IDurableFileWriter
    {
        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Simulated disk write failure"));
    }
}
