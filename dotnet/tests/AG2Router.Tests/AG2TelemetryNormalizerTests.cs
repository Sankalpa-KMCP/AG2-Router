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

    [Fact]
    public void Canonicalize_ReasoningVariants_SharingModelOrTier_AreDeduplicatedWithModes()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                CascadeModelConfigData = new RawCascadeModelConfigData
                {
                    ClientModelConfigs = new List<RawClientModelConfig>
                    {
                        new()
                        {
                            Label = "Gemini 2.5 Pro",
                            ModelOrTier = "gemini-2.5-pro",
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.85,
                                ResetTime = "2026-09-21T21:00:00Z",
                                IsExhausted = false
                            }
                        },
                        new()
                        {
                            Label = "Gemini 2.5 Pro (Thinking)",
                            ModelOrTier = "gemini-2.5-pro",
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.85,
                                ResetTime = "2026-09-21T21:00:00Z",
                                IsExhausted = false
                            }
                        }
                    }
                }
            }
        };

        var snapshot = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(snapshot);
        // Raw backward compatibility: 2 models preserved
        Assert.Equal(2, snapshot.Models.Count);

        // Canonical deduplication: 1 canonical model
        Assert.NotNull(snapshot.CanonicalModels);
        var canonical = Assert.Single(snapshot.CanonicalModels);
        Assert.Equal("tier:gemini-2.5-pro", canonical.Key);
        Assert.Equal("Gemini 2.5 Pro", canonical.Label);
        Assert.Equal("gemini-2.5-pro", canonical.ModelOrTier);
        Assert.Equal(0.85, canonical.RemainingFraction);
        Assert.Equal("2026-09-21T21:00:00Z", canonical.ResetTime);
        Assert.False(canonical.IsExhausted);
        Assert.Equal(new[] { "Standard", "Thinking" }, canonical.Modes);
    }

    [Fact]
    public void Canonicalize_DistinctModelOrTier_NeverMergedEvenWithSimilarLabels()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                CascadeModelConfigData = new RawCascadeModelConfigData
                {
                    ClientModelConfigs = new List<RawClientModelConfig>
                    {
                        new()
                        {
                            Label = "Gemini 3.8 Flash (High)",
                            ModelOrTier = "gemini-3.8-flash-high",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.70 }
                        },
                        new()
                        {
                            Label = "Gemini 3.1 Pro (High)",
                            ModelOrTier = "gemini-3.1-pro-high",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.40 }
                        },
                        new()
                        {
                            Label = "Identical Label Model",
                            ModelOrTier = "tier-alpha",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.90 }
                        },
                        new()
                        {
                            Label = "Identical Label Model",
                            ModelOrTier = "tier-beta",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.90 }
                        }
                    }
                }
            }
        };

        var snapshot = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.CanonicalModels);
        Assert.Equal(4, snapshot.CanonicalModels.Count);

        // Suffix (High) is not stripped because it is not a thinking/reasoning execution mode
        Assert.Equal("Gemini 3.8 Flash (High)", snapshot.CanonicalModels[0].Label);
        Assert.Equal("Gemini 3.1 Pro (High)", snapshot.CanonicalModels[1].Label);

        // Distinct ModelOrTier keeps identical labels separated
        Assert.Equal("tier:tier-alpha", snapshot.CanonicalModels[2].Key);
        Assert.Equal("tier:tier-beta", snapshot.CanonicalModels[3].Key);
    }

    [Fact]
    public void Canonicalize_SafeLabelFallback_MergesWhenModelOrTierMissingAndResetMatches()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                CascadeModelConfigData = new RawCascadeModelConfigData
                {
                    ClientModelConfigs = new List<RawClientModelConfig>
                    {
                        new()
                        {
                            Label = "Claude 3.7 Sonnet",
                            ModelOrTier = null,
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.60,
                                ResetTime = "2026-09-21T21:00:00Z"
                            }
                        },
                        new()
                        {
                            Label = "Claude 3.7 Sonnet (Thinking)",
                            ModelOrTier = null,
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.60,
                                ResetTime = "2026-09-21T21:00:00Z"
                            }
                        }
                    }
                }
            }
        };

        var snapshot = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.CanonicalModels);
        var canonical = Assert.Single(snapshot.CanonicalModels);
        Assert.Equal("Claude 3.7 Sonnet", canonical.Label);
        Assert.Equal(new[] { "Standard", "Thinking" }, canonical.Modes);
    }

    [Fact]
    public void Canonicalize_SafeLabelFallback_DifferingResetTimes_DoNotMerge()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                CascadeModelConfigData = new RawCascadeModelConfigData
                {
                    ClientModelConfigs = new List<RawClientModelConfig>
                    {
                        new()
                        {
                            Label = "Custom Pool",
                            ModelOrTier = null,
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.50,
                                ResetTime = "2026-09-21T18:00:00Z"
                            }
                        },
                        new()
                        {
                            Label = "Custom Pool (Thinking)",
                            ModelOrTier = null,
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.50,
                                ResetTime = "2026-09-28T00:00:00Z"
                            }
                        }
                    }
                }
            }
        };

        var snapshot = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.CanonicalModels);
        // Different reset times prevent merging when relying on label fallback
        Assert.Equal(2, snapshot.CanonicalModels.Count);
    }

    [Fact]
    public void Canonicalize_ConservativeConflict_TakesMinFraction_OrExhaustion_AndMaxResetTime()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                CascadeModelConfigData = new RawCascadeModelConfigData
                {
                    ClientModelConfigs = new List<RawClientModelConfig>
                    {
                        new()
                        {
                            Label = "Conflicted Model",
                            ModelOrTier = "conflicted-model",
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.80,
                                IsExhausted = false,
                                ResetTime = "2026-09-21T18:00:00Z"
                            }
                        },
                        new()
                        {
                            Label = "Conflicted Model (Reasoning)",
                            ModelOrTier = "conflicted-model",
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.30,
                                IsExhausted = true,
                                ResetTime = "2026-09-21T20:30:00Z"
                            }
                        }
                    }
                }
            }
        };

        var snapshot = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.CanonicalModels);
        var canonical = Assert.Single(snapshot.CanonicalModels);

        // Min remaining fraction
        Assert.Equal(0.30, canonical.RemainingFraction);
        // Logical OR: if any variant is exhausted, canonical is exhausted
        Assert.True(canonical.IsExhausted);
        // Latest reset time
        Assert.Equal("2026-09-21T20:30:00Z", canonical.ResetTime);
        Assert.Equal(new[] { "Standard", "Reasoning" }, canonical.Modes);
    }

    [Fact]
    public void Canonicalize_ZeroFraction_ForcesIsExhaustedTrue()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                CascadeModelConfigData = new RawCascadeModelConfigData
                {
                    ClientModelConfigs = new List<RawClientModelConfig>
                    {
                        new()
                        {
                            Label = "Zero Model",
                            ModelOrTier = "zero-model",
                            QuotaInfo = new RawQuotaInfo
                            {
                                RemainingFraction = 0.0,
                                IsExhausted = false
                            }
                        }
                    }
                }
            }
        };

        var snapshot = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.CanonicalModels);
        var canonical = Assert.Single(snapshot.CanonicalModels);
        Assert.Equal(0.0, canonical.RemainingFraction);
        Assert.True(canonical.IsExhausted);
    }

    [Fact]
    public void NormalizeQuotaSnapshot_FullSyntheticConnectRpcPayload_MatchesAllInvariants()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                Email = "dev@example.com",
                Name = "Developer Jane",
                CascadeModelConfigData = new RawCascadeModelConfigData
                {
                    ClientModelConfigs = new List<RawClientModelConfig>
                    {
                        new()
                        {
                            Label = "Gemini 2.5 Pro",
                            ModelOrTier = "gemini-2.5-pro",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.90, ResetTime = "2026-09-21T22:00:00Z" }
                        },
                        new()
                        {
                            Label = "Gemini 2.5 Pro (Thinking)",
                            ModelOrTier = "gemini-2.5-pro",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.90, ResetTime = "2026-09-21T22:00:00Z" }
                        },
                        new()
                        {
                            Label = "Claude 3.7 Sonnet",
                            ModelOrTier = "claude-3-7-sonnet",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.45, ResetTime = "2026-09-21T20:00:00Z" }
                        },
                        new()
                        {
                            Label = "Claude 3.7 Sonnet (Thinking)",
                            ModelOrTier = "claude-3-7-sonnet",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 0.45, ResetTime = "2026-09-21T20:00:00Z" }
                        },
                        new()
                        {
                            Label = "Gemini 2.5 Flash",
                            ModelOrTier = "gemini-2.5-flash",
                            QuotaInfo = new RawQuotaInfo { RemainingFraction = 1.0, ResetTime = "2026-09-21T23:00:00Z" }
                        }
                    }
                },
                PlanStatus = new RawPlanStatus
                {
                    AvailablePromptCredits = 1500,
                    AvailableFlowCredits = 300,
                    PlanInfo = new RawPlanInfo
                    {
                        MonthlyPromptCredits = 2000,
                        MonthlyFlowCredits = 500,
                        PlanName = "Google AI Pro"
                    }
                }
            }
        };

        var snapshot = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(snapshot);
        // Raw models preserved
        Assert.Equal(5, snapshot.Models.Count);

        // Deduplicated canonical models: exactly 3
        Assert.NotNull(snapshot.CanonicalModels);
        Assert.Equal(3, snapshot.CanonicalModels.Count);

        Assert.Equal("Gemini 2.5 Pro", snapshot.CanonicalModels[0].Label);
        Assert.Equal(new[] { "Standard", "Thinking" }, snapshot.CanonicalModels[0].Modes);

        Assert.Equal("Claude 3.7 Sonnet", snapshot.CanonicalModels[1].Label);
        Assert.Equal(new[] { "Standard", "Thinking" }, snapshot.CanonicalModels[1].Modes);

        Assert.Equal("Gemini 2.5 Flash", snapshot.CanonicalModels[2].Label);
        Assert.Equal(new[] { "Standard" }, snapshot.CanonicalModels[2].Modes);

        // Credit segregation preserved
        Assert.NotNull(snapshot.PromptCredits);
        Assert.Equal(1500, snapshot.PromptCredits.AvailableCredits);
        Assert.Equal(500, snapshot.PromptCredits.UsedCredits);

        Assert.NotNull(snapshot.FlowCredits);
        Assert.Equal(300, snapshot.FlowCredits.AvailableCredits);
        Assert.Equal(200, snapshot.FlowCredits.UsedCredits);
    }

    [Fact]
    public void NormalizeQuotaSnapshot_PartialCreditPool_PreservesNullValuesWithoutCoalescingZero()
    {
        var raw = new RawUserStatusResponse
        {
            UserStatus = new RawUserStatus
            {
                PlanStatus = new RawPlanStatus
                {
                    PlanInfo = new RawPlanInfo
                    {
                        MonthlyPromptCredits = null,
                        MonthlyFlowCredits = 500
                    },
                    AvailablePromptCredits = 100,
                    AvailableFlowCredits = null
                }
            }
        };

        var snapshot = AG2TelemetryNormalizer.NormalizeQuotaSnapshot(raw);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.PromptCredits);
        Assert.Equal(100, snapshot.PromptCredits.AvailableCredits);
        Assert.Null(snapshot.PromptCredits.MonthlyCredits);
        Assert.Null(snapshot.PromptCredits.UsedCredits);

        Assert.NotNull(snapshot.FlowCredits);
        Assert.Null(snapshot.FlowCredits.AvailableCredits);
        Assert.Equal(500, snapshot.FlowCredits.MonthlyCredits);
        Assert.Null(snapshot.FlowCredits.UsedCredits);
    }
}
