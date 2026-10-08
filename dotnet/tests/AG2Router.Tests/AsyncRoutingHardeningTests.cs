using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AG2Router.AG2.Accounts;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.AG2.Switching;
using AG2Router.AG2.Vault;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Verification tests for async routing and switch admission hardening (PROMPT #002).
/// Validates non-blocking configuration updates, contention notification, cancellation propagation,
/// and async switch plan evaluation.
/// </summary>
[Collection("SwitchCoordinator")]
public sealed class AsyncRoutingHardeningTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ag2_async_tests_{Guid.NewGuid():N}");
    private readonly SimpleAccountStore _accountStore = new();
    private readonly SimpleSessionVault _sessionVault = new();
    private readonly MockAG2Adapter _adapter = new();
    private readonly SimpleSwitchCoordinator _switchCoordinator = new();
    private readonly RoutingSafetyGate _safetyGate = new();

    public AsyncRoutingHardeningTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task UpdateConfigAsync_PersistsToConfigPathViaDurableFileWriter()
    {
        var fakeWriter = new FakeDurableFileWriter();
        string configPath = Path.Combine(_tempDir, "config.json");
        await using var router = new NativeAutoRouter(
            _accountStore,
            _sessionVault,
            _adapter,
            _switchCoordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true),
            safetyGate: _safetyGate,
            configFilePath: configPath,
            fileWriter: fakeWriter);

        var update = new RouterConfigDto(AutoSwitchEnabled: false, LowQuotaThresholdPercent: 25);
        var result = await router.UpdateConfigAsync(update);

        Assert.False(result.AutoSwitchEnabled);
        Assert.Equal(25, result.LowQuotaThresholdPercent);
        Assert.Equal(configPath, fakeWriter.LastWrittenPath);
        Assert.NotNull(fakeWriter.LastWrittenContent);
        Assert.Contains("\"AutoSwitchEnabled\": false", fakeWriter.LastWrittenContent);
        Assert.Contains("\"LowQuotaThresholdPercent\": 25", fakeWriter.LastWrittenContent);
    }

    [Fact]
    public async Task UpdateConfigWithGenerationAsync_MonotonicallyIncrementsGeneration()
    {
        var fakeWriter = new FakeDurableFileWriter();
        string configPath = Path.Combine(_tempDir, "config.json");
        await using var router = new NativeAutoRouter(
            _accountStore,
            _sessionVault,
            _adapter,
            _switchCoordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, PollingIntervalMs: 5000),
            safetyGate: _safetyGate,
            configFilePath: configPath,
            fileWriter: fakeWriter);

        long initialGen = router.ConfigGeneration;

        var (cfg1, gen1) = await router.UpdateConfigWithGenerationAsync(
            router.GetConfig() with { PollingIntervalMs = 6000 });
        Assert.Equal(6000, cfg1.PollingIntervalMs);
        Assert.True(gen1 > initialGen);

        var (cfg2, gen2) = await router.UpdateConfigWithGenerationAsync(
            router.GetConfig() with { PollingIntervalMs = 7000 });
        Assert.Equal(7000, cfg2.PollingIntervalMs);
        Assert.True(gen2 > gen1);
    }

    [Fact]
    public async Task UpdateConfigAsync_DurableFailureDoesNotReportOrApplySuccess()
    {
        var writer = new FakeDurableFileWriter { Failure = new IOException("synthetic async write failure") };
        string configPath = Path.Combine(_tempDir, "config.json");
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, PollingIntervalMs: 3000);
        await using var router = new NativeAutoRouter(
            _accountStore,
            _sessionVault,
            _adapter,
            _switchCoordinator,
            initialConfig: initial,
            safetyGate: _safetyGate,
            configFilePath: configPath,
            fileWriter: writer);

        await Assert.ThrowsAsync<IOException>(() => router.UpdateConfigAsync(
            initial with { AutoSwitchEnabled = false, PollingIntervalMs = 9000 }));

        Assert.Equal(initial, router.GetConfig());
    }

    [Fact]
    public async Task UpdateConfigWithGenerationAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var fakeWriter = new FakeDurableFileWriter();
        string configPath = Path.Combine(_tempDir, "config.json");
        var initial = new RouterConfigDto(AutoSwitchEnabled: true);
        await using var router = new NativeAutoRouter(
            _accountStore,
            _sessionVault,
            _adapter,
            _switchCoordinator,
            initialConfig: initial,
            safetyGate: _safetyGate,
            configFilePath: configPath,
            fileWriter: fakeWriter);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            router.UpdateConfigWithGenerationAsync(
                initial with { LowQuotaThresholdPercent = 20 },
                cts.Token));

        Assert.Equal(initial, router.GetConfig());
    }

    [Fact]
    public async Task UpdateConfigAsync_ConcurrentWithContentionBarrier_InvokesCallbackBeforeWaiting()
    {
        var fakeWriter = new FakeDurableFileWriter();
        string configPath = Path.Combine(_tempDir, "config.json");
        var initial = new RouterConfigDto(AutoSwitchEnabled: true);
        await using var router = new NativeAutoRouter(
            _accountStore,
            _sessionVault,
            _adapter,
            _switchCoordinator,
            initialConfig: initial,
            safetyGate: _safetyGate,
            configFilePath: configPath,
            fileWriter: fakeWriter);

        // Acquire interruption admission manually to simulate an active operation in flight
        using (await router.AcquireInterruptionAdmissionAsync(CancellationToken.None))
        {
            bool contentionReported = false;
            router.ConfigAdmissionContended = () => contentionReported = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

            // Should report contention immediately because admission is held
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await router.UpdateConfigAsync(
                    router.GetConfig() with { PollingIntervalMs = 12000 },
                    cts.Token);
            });

            Assert.True(contentionReported, "onContentionWaiting callback should have been invoked when blocked by admission");
        }

        // Releasing admission lease allows subsequent update to complete
        var (updated, generation) = await router.UpdateConfigWithGenerationAsync(
            router.GetConfig() with { LowQuotaThresholdPercent = 15 });
        Assert.Equal(15, updated.LowQuotaThresholdPercent);
        Assert.True(generation > 1);
    }

    [Fact]
    public async Task SwitchAutomaticallyAsync_WhenAsyncPredicateFalse_ReturnsCancelledAndZeroMutations()
    {
        var accounts = new SimpleAccountStore();
        var credentials = new FakeCredentialStore();
        var adapter = new FakeSwitchAdapter();
        var process = new FakeProcessLifecycle();
        var journal = new FakeJournalStore(Path.Combine(_tempDir, "journal.json"));
        var dpapi = new FakeDpapiProvider();
        var vault = new SessionVault(_tempDir, dpapi);

        await accounts.AddAccountAsync(new CreateAccountInput("source@example.com", HasVaultedSession: true));
        await accounts.AddAccountAsync(new CreateAccountInput("target@example.com", HasVaultedSession: true));
        var all = await accounts.ListAccountsAsync();
        var sourceAcc = all.First(a => a.Email == "source@example.com");
        var targetAcc = all.First(a => a.Email == "target@example.com");
        await accounts.SetActiveAccountIdAsync(sourceAcc.Id);
        await vault.SaveSessionAsync(sourceAcc.Id, [1, 2, 3]);
        await vault.SaveSessionAsync(targetAcc.Id, [4, 5, 6]);

        credentials.Set("source@example.com", [1, 2, 3]);
        adapter.Identity = new AccountIdentityDto("source@example.com");

        var coordinator = new NativeAccountSwitchCoordinator(
            accounts,
            vault,
            credentials,
            credentials,
            adapter,
            process,
            journal);

        var result = await coordinator.SwitchAutomaticallyAsync(
            targetAcc.Id,
            sourceAcc.Id,
            () => Task.FromResult(false),
            requiredWorkloadModelKey: null,
            minimumCandidateQuotaPercent: null,
            acquireInterruptionAdmissionAsync: _ => Task.FromResult<IDisposable>(new TestAdmissionLease()));

        Assert.False(result.Success);
        Assert.Equal(SwitchResultCodes.Cancelled, result.Code);
        Assert.Equal("Automatic switch plan became stale.", result.Message);
        Assert.Equal(0, credentials.WriteCount);
        Assert.Equal(0, process.StopCount);
    }

    private sealed class TestAdmissionLease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FakeDurableFileWriter : IDurableFileWriter
    {
        public Exception? Failure { get; set; }
        public string? LastWrittenPath { get; private set; }
        public string? LastWrittenContent { get; private set; }

        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Failure != null) return Task.FromException(Failure);
            LastWrittenPath = destinationPath;
            LastWrittenContent = content;
            return Task.CompletedTask;
        }
    }

    private sealed class SimpleAccountStore : InMemoryAccountStore
    {
    }

    private sealed class SimpleSessionVault : ISessionVault
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"simple_vault_{Guid.NewGuid():N}");
        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public string GetVaultPath() => _path;
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

    private sealed class FakeCredentialStore : IWinCredReader, IWinCredWriter
    {
        public int WriteCount { get; private set; }
        private WinCredEntry? _entry;

        public void Set(string userName, byte[] blob)
        {
            _entry = new WinCredEntry("gemini:antigravity", 1, userName, 2, (byte[])blob.Clone());
        }

        public Task<WinCredEntry?> ReadCredentialAsync(string target = "gemini:antigravity", CancellationToken cancellationToken = default)
            => Task.FromResult(_entry != null ? new WinCredEntry(_entry.Target, _entry.Type, _entry.UserName, _entry.Persistence, (byte[])_entry.Blob.Clone()) : null);

        public Task<bool> WriteCredentialAsync(WinCredEntry entry, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            _entry = new WinCredEntry(entry.Target, entry.Type, entry.UserName, entry.Persistence, (byte[])entry.Blob.Clone());
            return Task.FromResult(true);
        }
    }

    private sealed class FakeSwitchAdapter : IAG2Adapter
    {
        public AccountIdentityDto? Identity { get; set; }

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Ag2StatusDto(true, "HEALTHY", new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")), "synthetic"));

        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Identity);

        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<QuotaSnapshotDto?>(null);

        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountQuotaObservation(Identity, null));

        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")));
    }

    private sealed class FakeProcessLifecycle : IAG2ProcessLifecycle
    {
        public int StopCount { get; private set; }
        private readonly AG2ProcessSnapshot _snapshot = new(
            100, DateTime.UtcNow.AddMinutes(-1), @"C:\Synthetic\Antigravity\resources\bin\language_server.exe",
            ["--standalone", "--csrf_token", "synthetic"],
            ["--standalone", "--csrf_token", "[REDACTED]"], 10, DateTime.UtcNow.AddDays(-1),
            new string('A', 64), 1);
        private readonly AG2ProcessGeneration _replacement = new(
            101, DateTime.UtcNow, @"C:\Synthetic\Antigravity\resources\bin\language_server.exe", 2);

        public Task<AG2ProcessSnapshot> CaptureVerifiedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task RevalidateAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopVerifiedAsync(
            AG2ProcessSnapshot snapshot,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? verifyBeforeKillAsync = null,
            Action? onStopAttempted = null,
            Action? onStopIssued = null)
        {
            StopCount++;
            onStopIssued?.Invoke();
            return Task.CompletedTask;
        }

        public Task<AG2ProcessGeneration> LaunchAsync(AG2ProcessSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.FromResult(_replacement);

        public Task<AG2ProcessGeneration> WaitForHealthyReplacementAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration launched,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_replacement);

        public Task QuiesceForRollbackAsync(
            AG2ProcessSnapshot original,
            AG2ProcessGeneration? transactionOwnedReplacement,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AG2ProcessGeneration> RestoreAsync(
            AG2ProcessSnapshot original,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AG2ProcessGeneration(102, DateTime.UtcNow, original.ExecutablePath, 3));

        public Task<bool> IsGenerationCurrentAsync(AG2ProcessGeneration generation, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeJournalStore : ISwitchJournalStore
    {
        private readonly SwitchJournalStore _underlying;

        public string JournalFilePath => _underlying.JournalFilePath;

        public FakeJournalStore(string journalFilePath)
        {
            _underlying = new SwitchJournalStore(journalFilePath);
        }

        public Task<SwitchJournalReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            _underlying.ReadAsync(cancellationToken);

        public Task WriteEntryAsync(SwitchJournalEntry entry, CancellationToken cancellationToken = default) =>
            _underlying.WriteEntryAsync(entry, cancellationToken);

        public Task DeleteAsync(CancellationToken cancellationToken = default) =>
            _underlying.DeleteAsync(cancellationToken);

        public Task<SwitchJournalDeleteResult> DeleteIfUnchangedAsync(
            SwitchJournalEntry expectedEntry,
            CancellationToken cancellationToken = default) =>
            _underlying.DeleteIfUnchangedAsync(expectedEntry, cancellationToken);

        public Task<SwitchJournalWriteResult> CreateIfAbsentAsync(
            SwitchJournalEntry nextEntry,
            CancellationToken cancellationToken = default) =>
            _underlying.CreateIfAbsentAsync(nextEntry, cancellationToken);

        public Task<SwitchJournalWriteResult> ReplaceIfUnchangedAsync(
            SwitchJournalEntry expectedEntry,
            SwitchJournalEntry nextEntry,
            CancellationToken cancellationToken = default) =>
            _underlying.ReplaceIfUnchangedAsync(expectedEntry, nextEntry, cancellationToken);
    }
}
