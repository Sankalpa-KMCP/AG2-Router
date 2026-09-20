using AG2Router.AG2.Normalization;
using AG2Router.AG2.Rpc;
using Xunit;

namespace AG2Router.Tests;

public class AG2TelemetryNormalizerTests
{
    [Fact]
    public void NormalizeAccountIdentity_ValidPayload_ExtractsIdentity()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                Email = "user@example.com",
                Name = "Jane Doe",
                UserTier = new RawUserTier { Id = "g1-pro-tier", Name = "Google AI Pro" }
            }
        };

        var identity = AG2TelemetryNormalizer.NormalizeAccountIdentity(raw);

        Assert.NotNull(identity);
        Assert.Equal("user@example.com", identity.Email);
        Assert.Equal("Jane Doe", identity.Name);
        Assert.Equal("g1-pro-tier", identity.TierId);
        Assert.Equal("Google AI Pro", identity.TierName);
    }

    [Fact]
    public void NormalizeAccountIdentity_EmptyOrNullEmail_ReturnsNull()
    {
        var raw1 = new RawUserStatusResponse { UserStatus = new RawUserStatus { Email = "" } };
        var raw2 = new RawUserStatusResponse { UserStatus = null };

        Assert.Null(AG2TelemetryNormalizer.NormalizeAccountIdentity(raw1));
        Assert.Null(AG2TelemetryNormalizer.NormalizeAccountIdentity(raw2));
        Assert.Null(AG2TelemetryNormalizer.NormalizeAccountIdentity(null));
    }

    [Fact]
    public void NormalizeQuotaSnapshot_PreservesIndividualModelsAndClampsFractions()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                CascadeModelConfigData = new RawCascadeModelConfigData
                {
                    ClientModelConfigs = new List<RawClientModelConfig>
                    {
                        new() { Label = "Gemini 3.8 Flash", QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.75, IsExhausted = false } },
                        new() { Label = "Claude Sonnet 4.6", QuotaInfo = new RawQuotaInfo { RemainingFraction = 1.5, IsExhausted = false } }, // Over 1.0 -> clamp
                        new() { Label = "Exhausted Model", QuotaInfo = new RawQuotaInfo { RemainingFraction = -0.1, IsExhausted = true } } // Under 0.0 -> clamp
                    }
                }
            }
        };

        var quota = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(quota);
        Assert.Equal(3, quota.Models.Count);

        Assert.Equal(0.75, quota.Models[0].RemainingFraction);
        Assert.False(quota.Models[0].IsExhausted);

        Assert.Equal(1.0, quota.Models[1].RemainingFraction); // Clamped to 1.0
        Assert.False(quota.Models[1].IsExhausted);

        Assert.Equal(0.0, quota.Models[2].RemainingFraction); // Clamped to 0.0
        Assert.True(quota.Models[2].IsExhausted);
    }

    [Fact]
    public void NormalizeQuotaSnapshot_StrictlySegregatesPromptAndFlowCredits()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                PlanStatus = new RawPlanStatus
                {
                    AvailablePromptCredits = 500,
                    AvailableFlowCredits = 100,
                    PlanInfo = new RawPlanInfo
                    {
                        MonthlyPromptCredits = 1000,
                        MonthlyFlowCredits = 200
                    }
                }
            }
        };

        var quota = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(quota);
        Assert.NotNull(quota.PromptCredits);
        Assert.NotNull(quota.FlowCredits);

        // Prompt pool
        Assert.Equal(500, quota.PromptCredits.AvailableCredits);
        Assert.Equal(1000, quota.PromptCredits.MonthlyCredits);
        Assert.Equal(500, quota.PromptCredits.UsedCredits);

        // Flow pool
        Assert.Equal(100, quota.FlowCredits.AvailableCredits);
        Assert.Equal(200, quota.FlowCredits.MonthlyCredits);
        Assert.Equal(100, quota.FlowCredits.UsedCredits);

        // Verify distinct values: never summed together
        Assert.NotEqual(quota.PromptCredits.AvailableCredits, quota.FlowCredits.AvailableCredits);
    }

    [Fact]
    public void NormalizeActivitySnapshot_ClassifiesBusyAndIdle()
    {
        var runningTrajectories = new RawTrajectoriesResponse
        {
            TrajectorySummaries = new Dictionary<string, RawTrajectorySummary>
            {
                ["t1"] = new() { Status = "CASCADE_RUN_STATUS_RUNNING" },
                ["t2"] = new() { Status = "CASCADE_RUN_STATUS_IDLE" }
            }
        };

        var idleTrajectories = new RawTrajectoriesResponse
        {
            TrajectorySummaries = new Dictionary<string, RawTrajectorySummary>
            {
                ["t1"] = new() { Status = "CASCADE_RUN_STATUS_IDLE" }
            }
        };

        var emptyTrajectories = new RawTrajectoriesResponse();

        var busySnap = AG2TelemetryNormalizer.NormalizeActivitySnapshot(runningTrajectories);
        Assert.Equal("BUSY", busySnap.State);
        Assert.Equal(2, busySnap.TotalTrajectories);
        Assert.Equal(1, busySnap.RunningTrajectories);

        var idleSnap = AG2TelemetryNormalizer.NormalizeActivitySnapshot(idleTrajectories);
        Assert.Equal("IDLE", idleSnap.State);
        Assert.Equal(1, idleSnap.TotalTrajectories);
        Assert.Equal(0, idleSnap.RunningTrajectories);

        var emptySnap = AG2TelemetryNormalizer.NormalizeActivitySnapshot(emptyTrajectories);
        Assert.Equal("IDLE", emptySnap.State);
        Assert.Equal(0, emptySnap.TotalTrajectories);
        Assert.Equal(0, emptySnap.RunningTrajectories);
    }
}
