using AG2Router.AG2.Adapter;
using Xunit;

namespace AG2Router.Tests;

public sealed class LiveIntegrationFactAttribute : FactAttribute
{
    public LiveIntegrationFactAttribute()
    {
        var optIn = Environment.GetEnvironmentVariable("AG2_LIVE_TEST");
        if (!string.Equals(optIn, "1", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Live integration test skipped by default. Opt-in with AG2_LIVE_TEST=1.";
        }
    }
}

public class AG2LiveParityTests
{
    [LiveIntegrationFact]
    [Trait("Category", "LiveIntegration")]
    public async Task LiveTelemetry_WhenAntigravityRunning_MatchesExpectedContracts()
    {
        var adapter = new AG2LiveAdapter();
        var status = await adapter.GetStatusAsync();

        Assert.True(status.Connected, $"Expected connected status when AG2_LIVE_TEST=1, got: {status.Status} - {status.Message}");

        var account = await adapter.GetCurrentAccountAsync();
        Assert.NotNull(account);
        Assert.False(string.IsNullOrWhiteSpace(account.Email));
        Assert.False(string.IsNullOrWhiteSpace(account.TierId));

        var quota = await adapter.GetQuotaAsync();
        Assert.NotNull(quota);
        Assert.NotEmpty(quota.Models);
        Assert.NotNull(quota.PromptCredits);
        Assert.NotNull(quota.FlowCredits);

        // Strict segregation invariant verified on live data
        Assert.True(quota.PromptCredits.MonthlyCredits >= 0);
        Assert.True(quota.FlowCredits.MonthlyCredits >= 0);

        var activity = await adapter.GetActivityStateAsync();
        Assert.NotNull(activity);
        Assert.True(activity.TotalTrajectories >= 0);
        Assert.Contains(activity.State, new[] { "IDLE", "BUSY" });
    }
}
