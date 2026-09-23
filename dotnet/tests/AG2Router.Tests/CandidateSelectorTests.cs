using AG2Router.AG2.Routing;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class CandidateSelectorTests
{
    private static AccountMetadata CreateAccount(
        string id,
        string email = "test@example.com",
        int priority = 1,
        bool isReserve = false,
        bool hasVaultedSession = true,
        string validationStatus = "VALID")
    {
        return new AccountMetadata(
            Id: id,
            Email: email,
            Name: "Test User",
            Priority: priority,
            IsReserve: isReserve,
            ValidationStatus: validationStatus,
            HasVaultedSession: hasVaultedSession,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O")
        );
    }

    [Fact]
    public void SelectBestCandidate_CurrentAccountQuotaHealthy_ReturnsNoSwitch()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        var accounts = new List<AccountMetadata> { current, candidate };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.50,
            ["acc_cand"] = 0.90
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.50,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" }
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        Assert.Contains("healthy", result.Reason);
    }

    [Theory]
    [InlineData(0.1501, false)]
    [InlineData(0.1500, true)]
    [InlineData(0.1499, true)]
    [InlineData(0.0000, true)]
    public void SelectBestCandidate_LowQuotaThresholdBoundaries(double currentQuota, bool expectedShouldSwitch)
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        var accounts = new List<AccountMetadata> { current, candidate };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = currentQuota,
            ["acc_cand"] = 0.80
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            currentQuota,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" }
        );

        Assert.Equal(expectedShouldSwitch, result.ShouldSwitch);
        if (expectedShouldSwitch)
        {
            Assert.NotNull(result.BestCandidate);
            Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
        }
    }

    [Theory]
    [InlineData(0.3000, true)]
    [InlineData(0.3001, true)]
    [InlineData(0.2990, false)]
    [InlineData(0.1000, false)]
    public void SelectBestCandidate_CandidateMinimumQuotaThresholdBoundaries(double candidateQuota, bool expectedEligible)
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        var accounts = new List<AccountMetadata> { current, candidate };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.10,
            ["acc_cand"] = candidateQuota
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.10,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" }
        );

        if (expectedEligible)
        {
            Assert.True(result.ShouldSwitch);
            Assert.NotNull(result.BestCandidate);
            Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
        }
        else
        {
            Assert.False(result.ShouldSwitch);
            Assert.Null(result.BestCandidate);
        }
    }

    [Fact]
    public void SelectBestCandidate_ExcludesNonVaultedAndExpiredAndCooldown()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var unvaulted = CreateAccount("acc_unvaulted", "unv@example.com", hasVaultedSession: false);
        var expired = CreateAccount("acc_expired", "exp@example.com", validationStatus: "EXPIRED");
        var cooldown = CreateAccount("acc_cooldown", "cd@example.com");
        var valid = CreateAccount("acc_valid", "valid@example.com");

        var accounts = new List<AccountMetadata> { current, unvaulted, expired, cooldown, valid };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_unvaulted"] = 0.90,
            ["acc_expired"] = 0.90,
            ["acc_cooldown"] = 0.90,
            ["acc_valid"] = 0.80
        };

        var vaulted = new HashSet<string> { "acc_curr", "acc_expired", "acc_cooldown", "acc_valid" };
        var activeCooldowns = new HashSet<string> { "acc_cooldown" };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: vaulted,
            cooldownAccountIds: activeCooldowns
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_valid", result.BestCandidate.Account.Id);

        var evaluations = result.Candidates.ToDictionary(c => c.Account.Id);
        Assert.False(evaluations["acc_unvaulted"].IsEligible);
        Assert.Contains("vaulted", evaluations["acc_unvaulted"].IneligibilityReason);

        Assert.False(evaluations["acc_expired"].IsEligible);
        Assert.Contains("EXPIRED", evaluations["acc_expired"].IneligibilityReason);

        Assert.False(evaluations["acc_cooldown"].IsEligible);
        Assert.Contains("cooldown", evaluations["acc_cooldown"].IneligibilityReason);

        Assert.True(evaluations["acc_valid"].IsEligible);
    }

    [Fact]
    public void SelectBestCandidate_NonReserveAlwaysPrecedesReserve()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var reserve = CreateAccount("acc_res", "res@example.com", priority: 1, isReserve: true);
        var standard = CreateAccount("acc_std", "std@example.com", priority: 5, isReserve: false);

        var accounts = new List<AccountMetadata> { current, reserve, standard };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_res"] = 0.99, // Reserve has higher quota
            ["acc_std"] = 0.40  // Standard has lower quota but is non-reserve
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_res", "acc_std" }
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_std", result.BestCandidate.Account.Id);
    }

    [Fact]
    public void SelectBestCandidate_HigherQuotaFractionTakesPrecedence()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candLow = CreateAccount("acc_low", priority: 1);
        var candHigh = CreateAccount("acc_high", priority: 2);

        var accounts = new List<AccountMetadata> { current, candLow, candHigh };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_low"] = 0.60,
            ["acc_high"] = 0.85
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_low", "acc_high" }
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_high", result.BestCandidate.Account.Id);
    }

    [Fact]
    public void SelectBestCandidate_EqualQuotaWithinTolerance_BreaksTieByPriority()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candP2 = CreateAccount("acc_p2", priority: 2);
        var candP1 = CreateAccount("acc_p1", priority: 1);

        var accounts = new List<AccountMetadata> { current, candP2, candP1 };
        // Quotas differ by less than 0.001
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_p2"] = 0.7005,
            ["acc_p1"] = 0.7000
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_p2", "acc_p1" }
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_p1", result.BestCandidate.Account.Id); // P1 wins tie
    }

    [Fact]
    public void SelectBestCandidate_EqualQuotaAndPriority_BreaksTieByAlphabeticalId()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candB = CreateAccount("acc_bravo", priority: 1);
        var candA = CreateAccount("acc_alpha", priority: 1);

        var accounts = new List<AccountMetadata> { current, candB, candA };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_bravo"] = 0.80,
            ["acc_alpha"] = 0.80
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_bravo", "acc_alpha" }
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_alpha", result.BestCandidate.Account.Id);
    }

    [Fact]
    public void SelectBestCandidate_NoEligibleCandidates_ReturnsNoSwitch()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var accounts = new List<AccountMetadata> { current };
        var quotas = new Dictionary<string, double> { ["acc_curr"] = 0.05 };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr" }
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        Assert.Contains("No secondary accounts", result.Reason);
    }

    [Fact]
    public void SelectBestCandidate_CandidateWithoutObservedQuota_IsMarkedIneligible()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var unobservedCandidate = CreateAccount("acc_unobserved");
        var accounts = new List<AccountMetadata> { current, unobservedCandidate };
        // quotas dictionary only contains current account; unobservedCandidate has no entry
        var quotas = new Dictionary<string, double> { ["acc_curr"] = 0.05 };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_unobserved" }
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var candidateEval = Assert.Single(result.Candidates);
        Assert.False(candidateEval.IsEligible);
        Assert.Contains("unknown", candidateEval.IneligibilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectBestCandidate_CandidateLacksModelTelemetryWhenRelevantModelSpecified_IsMarkedIneligible()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_cand"] = 0.80
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new List<string> { "gemini-pro" },
            accountModelQuotas: new Dictionary<string, IReadOnlyList<ModelQuotaDto>>() // empty: no per-model telemetry for candidate
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var candEval = Assert.Single(result.Candidates);
        Assert.False(candEval.IsEligible);
        Assert.Contains("unknown", candEval.IneligibilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectBestCandidate_CandidateMissingSpecificRelevantModel_IsMarkedIneligible()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_cand"] = 0.80
        };
        var modelQuotas = new Dictionary<string, IReadOnlyList<ModelQuotaDto>>
        {
            ["acc_cand"] = new List<ModelQuotaDto>
            {
                new("Claude 3.7 Sonnet", "claude-3-7-sonnet", 0.90, null, false)
            }
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new List<string> { "gemini-pro" },
            accountModelQuotas: modelQuotas
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var candEval = Assert.Single(result.Candidates);
        Assert.False(candEval.IsEligible);
        Assert.Contains("gemini-pro", candEval.IneligibilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectBestCandidate_CandidateHasExhaustedRelevantModel_IsMarkedIneligible()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_cand"] = 0.80
        };
        var modelQuotas = new Dictionary<string, IReadOnlyList<ModelQuotaDto>>
        {
            ["acc_cand"] = new List<ModelQuotaDto>
            {
                new("Gemini Pro", "gemini-pro", 0.0, null, true)
            }
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new List<string> { "gemini-pro" },
            accountModelQuotas: modelQuotas
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var candEval = Assert.Single(result.Candidates);
        Assert.False(candEval.IsEligible);
        Assert.Contains("exhausted", candEval.IneligibilityReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectBestCandidate_CandidateHasHealthyRelevantModel_IsSelected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var quotas = new Dictionary<string, double>
        {
            ["acc_curr"] = 0.05,
            ["acc_cand"] = 0.80
        };
        var modelQuotas = new Dictionary<string, IReadOnlyList<ModelQuotaDto>>
        {
            ["acc_cand"] = new List<ModelQuotaDto>
            {
                new("Gemini Pro", "gemini-pro", 0.75, null, false)
            }
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            accounts,
            quotas,
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new List<string> { "gemini-pro" },
            accountModelQuotas: modelQuotas
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
    }
}
