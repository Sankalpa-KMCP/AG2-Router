using AG2Router.App.Services;
using AG2Router.App.Views;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class MockAG2Adapter : IAG2Adapter
{
    public Func<CancellationToken, Task<Ag2StatusDto>>? GetStatusFunc { get; set; }
    public Func<CancellationToken, Task<AccountIdentityDto?>>? GetCurrentAccountFunc { get; set; }
    public Func<CancellationToken, Task<QuotaSnapshotDto?>>? GetQuotaFunc { get; set; }
    public Func<CancellationToken, Task<AccountQuotaObservation>>? GetAccountQuotaObservationFunc { get; set; }
    public Func<CancellationToken, Task<ActivityStatusDto>>? GetActivityStateFunc { get; set; }
    public int GetCurrentAccountCallCount { get; private set; }
    public int GetQuotaCallCount { get; private set; }
    public int GetAccountQuotaObservationCallCount { get; private set; }
    public AccountIdentityDto? CurrentAccount
    {
        get => GetCurrentAccountFunc != null ? GetCurrentAccountFunc(default).GetAwaiter().GetResult() : null;
        set => GetCurrentAccountFunc = _ => Task.FromResult(value);
    }

    public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return GetStatusFunc != null
            ? GetStatusFunc(cancellationToken)
            : Task.FromResult(new Ag2StatusDto(false, "OFFLINE", null, "Offline"));
    }

    public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        GetCurrentAccountCallCount++;
        return GetCurrentAccountFunc != null
            ? GetCurrentAccountFunc(cancellationToken)
            : Task.FromResult<AccountIdentityDto?>(null);
    }

    public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        GetQuotaCallCount++;
        return GetQuotaFunc != null
            ? GetQuotaFunc(cancellationToken)
            : Task.FromResult<QuotaSnapshotDto?>(null);
    }

    public async Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(
        CancellationToken cancellationToken = default)
    {
        GetAccountQuotaObservationCallCount++;
        if (GetAccountQuotaObservationFunc != null)
            return await GetAccountQuotaObservationFunc(cancellationToken);
        return new AccountQuotaObservation(
            await GetCurrentAccountAsync(cancellationToken),
            await GetQuotaAsync(cancellationToken));
    }

    public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default)
    {
        return GetActivityStateFunc != null
            ? GetActivityStateFunc(cancellationToken)
            : Task.FromResult(new ActivityStatusDto("OFFLINE", 0, 0, DateTime.UtcNow.ToString("o")));
    }
}

public class TelemetryPollingCoordinatorTests
{
    [Fact]
    public async Task PollAsync_WhenConnected_UpdatesCurrentStatusAndTelemetry()
    {
        var adapter = new MockAG2Adapter
        {
            GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(
                Connected: true,
                Status: "HEALTHY",
                Activity: new ActivityStatusDto("IDLE", 5, 0, DateTime.UtcNow.ToString("o")),
                Message: "Connected"
            )),
            GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(
                Email: "test@example.com",
                Name: "Tester",
                TierId: "pro",
                TierName: "Pro Tier",
                RawStatusTimestamp: DateTime.UtcNow.ToString("o")
            )),
            GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
                Timestamp: DateTime.UtcNow.ToString("o"),
                Models: new List<ModelQuotaDto>
                {
                    new("Gemini Flash", "gemini-flash", 0.8, null, false)
                },
                PromptCredits: null,
                FlowCredits: null
            ))
        };

        var coordinator = new TelemetryPollingCoordinator(adapter);

        await coordinator.PollAsync();

        var status = coordinator.CurrentStatus;
        Assert.True(status.Ag2.Connected);
        Assert.NotNull(status.Telemetry);
        Assert.Equal("test@example.com", status.Telemetry.CurrentAccount?.Email);
        Assert.Single(status.Telemetry.Quota!.Models);
        Assert.Equal(0.8, status.Telemetry.Quota.Models[0].RemainingFraction);
    }

    [Fact]
    public async Task PollAsync_WhenOffline_SetsRouterOfflineAndNullTelemetry()
    {
        var adapter = new MockAG2Adapter
        {
            GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(
                Connected: false,
                Status: "OFFLINE",
                Activity: new ActivityStatusDto("OFFLINE", 0, 0, DateTime.UtcNow.ToString("o")),
                Message: "Antigravity is not running"
            ))
        };

        var coordinator = new TelemetryPollingCoordinator(adapter);

        await coordinator.PollAsync();

        var status = coordinator.CurrentStatus;
        Assert.False(status.Ag2.Connected);
        Assert.Null(status.Telemetry);
        Assert.Equal("DEGRADED", status.Router.State);
    }

    [Fact]
    public async Task PollAsync_PreventsOverlappingPolls()
    {
        var tcs = new TaskCompletionSource<bool>();
        int callCount = 0;

        var adapter = new MockAG2Adapter
        {
            GetStatusFunc = async _ =>
            {
                Interlocked.Increment(ref callCount);
                await tcs.Task; // Hold the poll open
                return new Ag2StatusDto(true, "HEALTHY", null, "OK");
            }
        };

        var coordinator = new TelemetryPollingCoordinator(adapter);

        // First poll starts and hangs awaiting tcs.Task
        var task1 = Task.Run(() => coordinator.PollAsync());
        await Task.Delay(50); // Let task1 acquire semaphore

        // Second poll should immediately skip because task1 holds the semaphore
        await coordinator.PollAsync();

        Assert.Equal(1, callCount); // Second call did NOT invoke adapter!

        // Release first poll
        tcs.SetResult(true);
        await task1;

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task StopAsync_CancelsPromptlyWithoutExceptions()
    {
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter, TimeSpan.FromMilliseconds(50));

        coordinator.Start();
        await Task.Delay(100);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await coordinator.StopAsync(cts.Token);
        await coordinator.DisposeAsync();

        // Passed if shutdown completes without hanging or throwing
    }

    [Fact]
    public void TryApplyStatus_OlderSequenceException_DoesNotOverwriteNewerStatus()
    {
        var adapter = new MockAG2Adapter();
        var coordinator = new TelemetryPollingCoordinator(adapter);

        var newerHealthy = new SystemStatusDto(
            Status: "ok",
            Ag2: new Ag2StatusDto(true, "HEALTHY", null, "All good"),
            Router: new RouterStatusDto("HEALTHY", false, "user@example.com", "user@example.com", null, DateTime.UtcNow.ToString("o"), "Active", new RouterConfigDto()),
            Telemetry: null
        );

        var olderError = new SystemStatusDto(
            Status: "error",
            Ag2: new Ag2StatusDto(false, "ERROR", null, "Failed"),
            Router: new RouterStatusDto("ERROR", false, null, null, null, DateTime.UtcNow.ToString("o"), "Error", new RouterConfigDto()),
            Telemetry: null
        );

        // Newer poll (sequence 2) succeeds and applies first
        Assert.True(coordinator.TryApplyStatus(2, newerHealthy));
        Assert.Equal("HEALTHY", coordinator.CurrentStatus.Ag2.Status);

        // Older poll (sequence 1) encounters delayed exception and tries to apply fallback error
        Assert.False(coordinator.TryApplyStatus(1, olderError));
        Assert.Equal("HEALTHY", coordinator.CurrentStatus.Ag2.Status); // Preserved without clobbering!
    }

    [Fact]
    public void ModelQuotaFormatter_PreservesPerModelTransparency()
    {
        var models = new List<ModelQuotaDto>
        {
            new("Claude 3.7 Sonnet", "claude-3.7-sonnet", 0.75, null, false),
            new("Gemini 2.5 Flash", "gemini-2.5-flash", 1.0, null, false)
        };

        var (label, val, tooltip) = ModelQuotaFormatter.FormatModelQuota(models);
        Assert.Equal("Claude 3.7 Sonnet", label);
        Assert.Equal("75%", val);
        Assert.NotNull(tooltip);
        Assert.Contains("Claude 3.7 Sonnet: 75%", tooltip);
        Assert.Contains("Gemini 2.5 Flash: 100%", tooltip);

        var (emptyLabel, emptyVal, emptyTooltip) = ModelQuotaFormatter.FormatModelQuota(null);
        Assert.Equal("Model Quota", emptyLabel);
        Assert.Equal("—%", emptyVal);
        Assert.Null(emptyTooltip);
    }

    [Fact]
    public async Task PollAsync_WhenConnected_DerivesIdentityAndQuotaFromSingleObservation()
    {
        var account = new AccountIdentityDto("user@example.com", "User", "tier", "Tier", DateTime.UtcNow.ToString("o"));
        var quota = new QuotaSnapshotDto(
            Timestamp: DateTime.UtcNow.ToString("o"),
            Models: new List<ModelQuotaDto> { new("Gemini Flash", "gemini-flash", 0.9, null, false) },
            PromptCredits: null,
            FlowCredits: null
        );

        var adapter = new MockAG2Adapter
        {
            GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "Connected")),
            GetAccountQuotaObservationFunc = _ => Task.FromResult(new AccountQuotaObservation(account, quota)),
            GetCurrentAccountFunc = _ => throw new InvalidOperationException("Independent GetCurrentAccountAsync must not be called"),
            GetQuotaFunc = _ => throw new InvalidOperationException("Independent GetQuotaAsync must not be called")
        };

        var coordinator = new TelemetryPollingCoordinator(adapter);

        await coordinator.PollAsync();

        var status = coordinator.CurrentStatus;
        Assert.True(status.Ag2.Connected);
        Assert.NotNull(status.Telemetry);
        Assert.Same(account, status.Telemetry.CurrentAccount);
        Assert.Same(quota, status.Telemetry.Quota);
        Assert.Equal(1, adapter.GetAccountQuotaObservationCallCount);
        Assert.Equal(0, adapter.GetCurrentAccountCallCount);
        Assert.Equal(0, adapter.GetQuotaCallCount);
    }

    [Fact]
    public async Task PollAsync_PreventsRace_WhenAccountSwitchesBetweenHypotheticalIndependentReads()
    {
        // Models the AUD-102 race scenario:
        // Suppose account switches from Account A to Account B during polling.
        // Under the old two-read approach:
        // 1. GetCurrentAccountAsync() returns Account A.
        // 2. Account switch occurs.
        // 3. GetQuotaAsync() returns Quota B.
        // Result was a corrupted snapshot combining Account A identity with Account B quota!
        // Under the coherent GetAccountQuotaObservationAsync approach:
        // Identity and quota are captured in a single upstream observation (Account B, Quota B).
        var accountA = new AccountIdentityDto("account-a@example.com", "Account A", "pro", "Pro", DateTime.UtcNow.ToString("o"));
        var quotaA = new QuotaSnapshotDto(
            Timestamp: DateTime.UtcNow.ToString("o"),
            Models: new List<ModelQuotaDto> { new("Gemini Flash", "gemini-flash", 0.95, null, false) },
            PromptCredits: null,
            FlowCredits: null
        );

        var accountB = new AccountIdentityDto("account-b@example.com", "Account B", "ultra", "Ultra", DateTime.UtcNow.ToString("o"));
        var quotaB = new QuotaSnapshotDto(
            Timestamp: DateTime.UtcNow.ToString("o"),
            Models: new List<ModelQuotaDto> { new("Claude Opus", "claude-opus", 0.20, null, false) },
            PromptCredits: null,
            FlowCredits: null
        );

        var adapter = new MockAG2Adapter
        {
            GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "Connected")),
            // If independent calls were made, they would simulate the race (Account A identity followed by Quota B)
            GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(accountA),
            GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(quotaB),
            // But the coherent observation boundary returns the true atomic post-switch state
            GetAccountQuotaObservationFunc = _ => Task.FromResult(new AccountQuotaObservation(accountB, quotaB))
        };

        var coordinator = new TelemetryPollingCoordinator(adapter);

        await coordinator.PollAsync();

        var status = coordinator.CurrentStatus;
        Assert.NotNull(status.Telemetry);
        // Must reflect coherent Account B with Quota B, never mixing Account A with Quota B
        Assert.Equal("account-b@example.com", status.Telemetry.CurrentAccount?.Email);
        Assert.Equal("account-b@example.com", status.Router.ActiveAccountId);
        Assert.Equal("account-b@example.com", status.Router.ActiveAccountEmail);
        Assert.Single(status.Telemetry.Quota!.Models);
        Assert.Equal("Claude Opus", status.Telemetry.Quota.Models[0].Label);
        Assert.Equal("claude-opus", status.Telemetry.Quota.Models[0].ModelOrTier);
        Assert.Equal(0.20, status.Telemetry.Quota.Models[0].RemainingFraction);

        // Explicitly assert that Account A was NEVER paired with Quota B
        Assert.NotEqual("account-a@example.com", status.Telemetry.CurrentAccount?.Email);
        Assert.NotEqual("account-a@example.com", status.Router.ActiveAccountId);
        Assert.Equal(0, adapter.GetCurrentAccountCallCount);
        Assert.Equal(0, adapter.GetQuotaCallCount);
    }

    [Fact]
    public async Task PollAsync_WhenObservationIdentityIsMissing_PreservesTruthfulUnknownIdentity()
    {
        var quota = new QuotaSnapshotDto(
            Timestamp: DateTime.UtcNow.ToString("o"),
            Models: new List<ModelQuotaDto> { new("Gemini Flash", "gemini-flash", 0.5, null, false) },
            PromptCredits: null,
            FlowCredits: null
        );

        var adapter = new MockAG2Adapter
        {
            GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "Connected")),
            GetAccountQuotaObservationFunc = _ => Task.FromResult(new AccountQuotaObservation(null, quota)),
            // A fallback identity must NOT be queried or used
            GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(new AccountIdentityDto(
                "stale-fallback@example.com", "Fallback", "free", "Free", DateTime.UtcNow.ToString("o")))
        };

        var coordinator = new TelemetryPollingCoordinator(adapter);

        await coordinator.PollAsync();

        var status = coordinator.CurrentStatus;
        Assert.NotNull(status.Telemetry);
        Assert.Null(status.Telemetry.CurrentAccount);
        Assert.Null(status.Router.ActiveAccountId);
        Assert.Null(status.Router.ActiveAccountEmail);
        Assert.Contains("Account none active with 1 models.", status.Router.LastDecisionReason);
        Assert.Same(quota, status.Telemetry.Quota);
        Assert.Equal(0, adapter.GetCurrentAccountCallCount);
    }

    [Fact]
    public async Task PollAsync_WhenObservationQuotaIsMissing_PreservesTruthfulUnknownQuota()
    {
        var account = new AccountIdentityDto("user@example.com", "User", "tier", "Tier", DateTime.UtcNow.ToString("o"));

        var adapter = new MockAG2Adapter
        {
            GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "Connected")),
            GetAccountQuotaObservationFunc = _ => Task.FromResult(new AccountQuotaObservation(account, null)),
            // A fallback quota must NOT be queried or fabricated
            GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
                DateTime.UtcNow.ToString("o"), new List<ModelQuotaDto>(), null, null))
        };

        var coordinator = new TelemetryPollingCoordinator(adapter);

        await coordinator.PollAsync();

        var status = coordinator.CurrentStatus;
        Assert.NotNull(status.Telemetry);
        Assert.Same(account, status.Telemetry.CurrentAccount);
        Assert.Null(status.Telemetry.Quota);
        Assert.Null(status.Telemetry.TotalAvailableQuotaPercent);
        Assert.Contains("Account user@example.com active with 0 models.", status.Router.LastDecisionReason);
        Assert.Equal(0, adapter.GetQuotaCallCount);
    }

    [Fact]
    public async Task PollAsync_WhenObservationIsNull_HandlesSafelyWithoutThrowing()
    {
        var adapter = new MockAG2Adapter
        {
            GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "Connected")),
            GetAccountQuotaObservationFunc = _ => Task.FromResult<AccountQuotaObservation>(null!)
        };

        var coordinator = new TelemetryPollingCoordinator(adapter);

        await coordinator.PollAsync();

        var status = coordinator.CurrentStatus;
        Assert.True(status.Ag2.Connected);
        Assert.NotNull(status.Telemetry);
        Assert.Null(status.Telemetry.CurrentAccount);
        Assert.Null(status.Telemetry.Quota);
        Assert.Null(status.Router.ActiveAccountId);
        Assert.Null(status.Router.ActiveAccountEmail);
        Assert.Contains("Account none active with 0 models.", status.Router.LastDecisionReason);
    }
}
