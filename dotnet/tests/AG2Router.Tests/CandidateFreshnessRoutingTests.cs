using System.IO;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class CandidateFreshnessRoutingTests
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

    private static AccountModelQuotaObservation CreateObservation(
        string accountId,
        string modelKey = "gemini-pro",
        double fraction = 0.80,
        string? resetTime = null,
        DateTimeOffset? observedAt = null)
    {
        return new AccountModelQuotaObservation(
            accountId: accountId,
            modelKey: modelKey,
            remainingFraction: fraction,
            resetTime: resetTime,
            observedAtUtc: observedAt ?? DateTimeOffset.UtcNow,
            source: "ActiveGetUserStatus"
        );
    }

    // 1. no durable observation -> candidate rejected
    [Fact]
    public void Scenario01_NoDurableObservation_CandidateRejected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal);

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("Relevant model 'gemini-pro' quota is unknown (no observed telemetry for model)", eval.IneligibilityReason);
    }

    // 2. fresh matching observation above threshold -> candidate eligible
    [Fact]
    public void Scenario02_FreshMatchingObservationAboveThreshold_CandidateEligible()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.80, observedAt: now.AddMinutes(-5));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
        Assert.Equal(0.80, result.BestCandidate.RemainingFraction);
        Assert.Equal(80, result.BestCandidate.QuotaPercent);
    }

    // 3. fresh matching observation exactly at threshold -> eligible
    [Fact]
    public void Scenario03_FreshMatchingObservationExactlyAtThreshold_Eligible()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.30, observedAt: now.AddMinutes(-10));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
        Assert.Equal(0.30, result.BestCandidate.RemainingFraction);
        Assert.Equal(30, result.BestCandidate.QuotaPercent);
    }

    // 4. fresh matching observation below threshold -> rejected
    [Fact]
    public void Scenario04_FreshMatchingObservationBelowThreshold_Rejected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.29, observedAt: now.AddMinutes(-5));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Equal(0.29, eval.RemainingFraction);
        Assert.Contains("below minimum candidate threshold (30%)", eval.IneligibilityReason);
    }

    // 5. wrong model only -> rejected
    [Fact]
    public void Scenario05_WrongModelOnly_Rejected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var obs = CreateObservation("acc_cand", "claude-3-7-sonnet", 0.90, observedAt: now.AddMinutes(-5));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("Relevant model 'gemini-pro' quota is unknown (no observed telemetry for model)", eval.IneligibilityReason);
    }

    // 6. wrong account observation cannot qualify candidate
    [Fact]
    public void Scenario06_WrongAccountObservationCannotQualifyCandidate()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var obs = CreateObservation("acc_other", "gemini-pro", 0.90, observedAt: now.AddMinutes(-5));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs // Observation accountId doesn't match dictionary key/account.Id
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("Relevant model 'gemini-pro' quota is unknown", eval.IneligibilityReason);
    }

    // 7. current account excluded
    [Fact]
    public void Scenario07_CurrentAccountExcluded()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var obsCurr = CreateObservation("acc_curr", "gemini-pro", 0.99, observedAt: now.AddMinutes(-5));
        var obsCand = CreateObservation("acc_cand", "gemini-pro", 0.80, observedAt: now.AddMinutes(-5));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_curr"] = obsCurr,
            ["acc_cand"] = obsCand
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.True(result.ShouldSwitch);
        Assert.Single(result.Candidates);
        Assert.DoesNotContain(result.Candidates, c => c.Account.Id == "acc_curr");
        Assert.Equal("acc_cand", result.BestCandidate?.Account.Id);
    }

    // 8. observation just under 2h -> eligible if otherwise healthy
    [Fact]
    public void Scenario08_ObservationJustUnder2Hours_EligibleIfOtherwiseHealthy()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var observedAt = now - CandidateSelector.InactiveCandidateEvidenceLifetime + TimeSpan.FromSeconds(10);
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.75, observedAt: observedAt);
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
        Assert.True(Assert.Single(result.Candidates).IsEligible);
    }

    // 9. observation exactly 2h -> stale/rejected
    [Fact]
    public void Scenario09_ObservationExactly2Hours_StaleRejected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var observedAt = now - CandidateSelector.InactiveCandidateEvidenceLifetime;
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.75, observedAt: observedAt);
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("Observation for model 'gemini-pro' is stale (age exceeds 2 hour lifetime)", eval.IneligibilityReason);
    }

    // 10. observation over 2h -> stale/rejected
    [Fact]
    public void Scenario10_ObservationOver2Hours_StaleRejected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var observedAt = now - CandidateSelector.InactiveCandidateEvidenceLifetime - TimeSpan.FromMinutes(1);
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.75, observedAt: observedAt);
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("Observation for model 'gemini-pro' is stale (age exceeds 2 hour lifetime)", eval.IneligibilityReason);
    }

    // 11. future-dated observation -> rejected
    [Fact]
    public void Scenario11_FutureDatedObservation_Rejected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        var observedAt = now.AddMinutes(5);
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.85, observedAt: observedAt);
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("Observation for model 'gemini-pro' is future-dated and cannot be used", eval.IneligibilityReason);
    }

    // 12. exhausted before resetTime -> rejected
    [Fact]
    public void Scenario12_ExhaustedBeforeResetTime_Rejected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        string resetIso = now.AddMinutes(30).ToString("o");
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.0, resetTime: resetIso, observedAt: now.AddMinutes(-5));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("Relevant model 'gemini-pro' is exhausted on candidate (reset expected at", eval.IneligibilityReason);
    }

    // 13. exhausted after resetTime -> RESET_PROVISIONAL/rejected
    [Fact]
    public void Scenario13_ExhaustedAfterResetTime_ResetProvisionalRejected()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        string resetIso = now.AddMinutes(-10).ToString("o");
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.0, resetTime: resetIso, observedAt: now.AddMinutes(-20));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("Relevant model 'gemini-pro' quota is reset-provisional (prior exhaustion expired at", eval.IneligibilityReason);
    }

    // 14. resetTime passing does not fabricate RemainingFraction
    [Fact]
    public void Scenario14_ResetTimePassingDoesNotFabricateRemainingFraction()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;
        string resetIso = now.AddMinutes(-30).ToString("o");
        var obs = CreateObservation("acc_cand", "gemini-pro", 0.0, resetTime: resetIso, observedAt: now.AddMinutes(-40));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = obs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        var eval = Assert.Single(result.Candidates);
        Assert.Equal(0.0, eval.RemainingFraction);
        Assert.Equal(0, eval.QuotaPercent);
        Assert.False(eval.IsEligible);
    }

    // 15. fresh observation after prior reset provisional -> eligibility recalculated from new real fraction
    [Fact]
    public void Scenario15_FreshObservationAfterPriorResetProvisional_EligibilityRecalculatedFromNewRealFraction()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candidate = CreateAccount("acc_cand");
        var accounts = new List<AccountMetadata> { current, candidate };
        var now = DateTimeOffset.UtcNow;

        var freshObs = CreateObservation("acc_cand", "gemini-pro", 0.85, resetTime: null, observedAt: now.AddMinutes(-1));
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = freshObs
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
        Assert.Equal(0.85, result.BestCandidate.RemainingFraction);
        Assert.Equal(85, result.BestCandidate.QuotaPercent);
    }

    // 16. persisted observation survives store reload/restart and can qualify candidate
    [Fact]
    public async Task Scenario16_PersistedObservationSurvivesStoreReload_CanQualifyCandidate()
    {
        using var context = new TestContext();
        string storePath = Path.Combine(context.TempDir, "quota-observations.json");
        var store1 = new DurableQuotaObservationStore(storePath);

        var obs = CreateObservation("acc_cand", "gemini-pro", 0.75, observedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await store1.RecordObservationsAsync("acc_cand", new[] { obs });

        // Reload fresh store instance from same persisted file
        var store2 = new DurableQuotaObservationStore(storePath);

        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        context.AccountStore.Accounts[current.Id] = current;
        context.AccountStore.Accounts[candidate.Id] = candidate;
        context.AccountStore.ActiveAccountId = "acc_curr";
        context.SessionVault.StoredIds.Add("acc_curr");
        context.SessionVault.StoredIds.Add("acc_cand");

        context.Adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));

        await using var router = context.CreateRouter(quotaStore: store2);
        var result = await router.EvaluateCycleAsync();

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
        Assert.Equal(0.75, result.BestCandidate.RemainingFraction);
    }

    // 17. missing observation store file -> no candidate (fails closed / no switch)
    [Fact]
    public async Task Scenario17_MissingObservationStoreFile_NoCandidate_FailsClosedNoSwitch()
    {
        using var context = new TestContext();
        string nonExistentPath = Path.Combine(context.TempDir, "non-existent-store.json");
        var store = new DurableQuotaObservationStore(nonExistentPath);

        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        context.AccountStore.Accounts[current.Id] = current;
        context.AccountStore.Accounts[candidate.Id] = candidate;
        context.AccountStore.ActiveAccountId = "acc_curr";
        context.SessionVault.StoredIds.Add("acc_curr");
        context.SessionVault.StoredIds.Add("acc_cand");

        context.Adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));

        await using var router = context.CreateRouter(quotaStore: store);
        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("unknown", eval.IneligibilityReason);
    }

    // 18. corrupt store -> routing fails closed / no switch
    [Fact]
    public async Task Scenario18_CorruptStore_RoutingFailsClosedNoSwitch()
    {
        using var context = new TestContext();
        string storePath = Path.Combine(context.TempDir, "quota-observations.json");
        Directory.CreateDirectory(context.TempDir);
        await File.WriteAllTextAsync(storePath, "{ corrupt json not valid at all");

        var store = new DurableQuotaObservationStore(storePath);

        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        context.AccountStore.Accounts[current.Id] = current;
        context.AccountStore.Accounts[candidate.Id] = candidate;
        context.AccountStore.ActiveAccountId = "acc_curr";
        context.SessionVault.StoredIds.Add("acc_curr");
        context.SessionVault.StoredIds.Add("acc_cand");

        context.Adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));

        await using var router = context.CreateRouter(quotaStore: store);
        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        Assert.Contains("Quota observation store failed closed", result.Reason);
    }

    // 19. unsupported schema -> routing fails closed / no switch
    [Fact]
    public async Task Scenario19_UnsupportedSchema_RoutingFailsClosedNoSwitch()
    {
        using var context = new TestContext();
        string storePath = Path.Combine(context.TempDir, "quota-observations.json");
        Directory.CreateDirectory(context.TempDir);
        await File.WriteAllTextAsync(storePath, "{\"schemaVersion\": 999, \"updatedAt\": \"2026-09-29T00:00:00Z\", \"observations\": []}");

        var store = new DurableQuotaObservationStore(storePath);

        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        context.AccountStore.Accounts[current.Id] = current;
        context.AccountStore.Accounts[candidate.Id] = candidate;
        context.AccountStore.ActiveAccountId = "acc_curr";
        context.SessionVault.StoredIds.Add("acc_curr");
        context.SessionVault.StoredIds.Add("acc_cand");

        context.Adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));

        await using var router = context.CreateRouter(quotaStore: store);
        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        Assert.Contains("Quota observation store failed closed", result.Reason);
    }

    // 20. multiple candidates preserve current ranking
    [Fact]
    public void Scenario20_MultipleCandidatesPreserveCurrentRanking()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var cand1 = CreateAccount("acc_cand1", priority: 2, isReserve: false);
        var cand2 = CreateAccount("acc_cand2", priority: 1, isReserve: false);
        var cand3 = CreateAccount("acc_cand3", priority: 1, isReserve: false);
        var cand4 = CreateAccount("acc_cand4", priority: 1, isReserve: true);

        var accounts = new List<AccountMetadata> { current, cand1, cand2, cand3, cand4 };
        var now = DateTimeOffset.UtcNow;
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand1"] = CreateObservation("acc_cand1", "gemini-pro", 0.80, observedAt: now.AddMinutes(-5)),
            ["acc_cand2"] = CreateObservation("acc_cand2", "gemini-pro", 0.80, observedAt: now.AddMinutes(-5)),
            ["acc_cand3"] = CreateObservation("acc_cand3", "gemini-pro", 0.90, observedAt: now.AddMinutes(-5)),
            ["acc_cand4"] = CreateObservation("acc_cand4", "gemini-pro", 0.95, observedAt: now.AddMinutes(-5))
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1", "acc_cand2", "acc_cand3", "acc_cand4" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.True(result.ShouldSwitch);
        // cand3 has highest quota (0.90) among non-reserve
        Assert.Equal("acc_cand3", result.BestCandidate?.Account.Id);
    }

    // 21. reserve semantics unchanged
    [Fact]
    public void Scenario21_ReserveSemanticsUnchanged()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candStd = CreateAccount("acc_std", priority: 5, isReserve: false);
        var candRes = CreateAccount("acc_res", priority: 1, isReserve: true);

        var accounts = new List<AccountMetadata> { current, candStd, candRes };
        var now = DateTimeOffset.UtcNow;
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_std"] = CreateObservation("acc_std", "gemini-pro", 0.40, observedAt: now.AddMinutes(-5)),
            ["acc_res"] = CreateObservation("acc_res", "gemini-pro", 0.99, observedAt: now.AddMinutes(-5))
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_std", "acc_res" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_std", result.BestCandidate.Account.Id);
    }

    // 22. no eligible candidate -> no switch
    [Fact]
    public void Scenario22_NoEligibleCandidate_NoSwitch()
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr");
        var candStale = CreateAccount("acc_stale");
        var candExhausted = CreateAccount("acc_exhausted");
        var candLow = CreateAccount("acc_low");

        var accounts = new List<AccountMetadata> { current, candStale, candExhausted, candLow };
        var now = DateTimeOffset.UtcNow;
        var candidateObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_stale"] = CreateObservation("acc_stale", "gemini-pro", 0.80, observedAt: now.AddHours(-3)),
            ["acc_exhausted"] = CreateObservation("acc_exhausted", "gemini-pro", 0.0, resetTime: now.AddMinutes(30).ToString("o"), observedAt: now.AddMinutes(-10)),
            ["acc_low"] = CreateObservation("acc_low", "gemini-pro", 0.20, observedAt: now.AddMinutes(-10))
        };

        var result = CandidateSelector.SelectBestCandidate(
            currentAccountId: "acc_curr",
            currentQuotaFraction: 0.05,
            accounts: accounts,
            accountQuotas: new Dictionary<string, double> { ["acc_curr"] = 0.05 },
            config: config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_stale", "acc_exhausted", "acc_low" },
            relevantModelKeys: new[] { "gemini-pro" },
            candidateModelObservations: candidateObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        Assert.All(result.Candidates, c => Assert.False(c.IsEligible));
    }

    // 23. active account routing pressure still uses live current telemetry, not cached candidate data
    [Fact]
    public async Task Scenario23_ActiveAccountRoutingPressureStillUsesLiveCurrentTelemetry_NotCachedCandidateData()
    {
        using var context = new TestContext();
        string storePath = Path.Combine(context.TempDir, "quota-observations.json");
        var store = new DurableQuotaObservationStore(storePath);

        // Store says active account has 0.95 and candidate has 0.80
        var obsCurr = CreateObservation("acc_curr", "gemini-pro", 0.95, observedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var obsCand = CreateObservation("acc_cand", "gemini-pro", 0.80, observedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await store.RecordObservationsAsync("acc_curr", new[] { obsCurr });
        await store.RecordObservationsAsync("acc_cand", new[] { obsCand });

        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        context.AccountStore.Accounts[current.Id] = current;
        context.AccountStore.Accounts[candidate.Id] = candidate;
        context.AccountStore.ActiveAccountId = "acc_curr";
        context.SessionVault.StoredIds.Add("acc_curr");
        context.SessionVault.StoredIds.Add("acc_cand");

        // Live telemetry reports current account is exhausted (0.00 <= threshold 15%)
        context.Adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.0, null, true) },
            null, null
        ));

        await using var router = context.CreateRouter(quotaStore: store);
        var result = await router.EvaluateCycleAsync();

        // Pressure generated from LIVE 0.0, not store's 0.95!
        Assert.True(result.ShouldSwitch);
        Assert.Equal(0.0, result.CurrentQuotaFraction);
        Assert.NotNull(result.BestCandidate);
        Assert.Equal("acc_cand", result.BestCandidate.Account.Id);
    }

    // 24. candidate observation for another workload model cannot qualify
    [Fact]
    public async Task Scenario24_CandidateObservationForAnotherWorkloadModelCannotQualify()
    {
        using var context = new TestContext();
        string storePath = Path.Combine(context.TempDir, "quota-observations.json");
        var store = new DurableQuotaObservationStore(storePath);

        // Candidate observation is for claude-3-7-sonnet only
        var obsCand = CreateObservation("acc_cand", "claude-3-7-sonnet", 0.90, observedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        await store.RecordObservationsAsync("acc_cand", new[] { obsCand });

        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        context.AccountStore.Accounts[current.Id] = current;
        context.AccountStore.Accounts[candidate.Id] = candidate;
        context.AccountStore.ActiveAccountId = "acc_curr";
        context.SessionVault.StoredIds.Add("acc_curr");
        context.SessionVault.StoredIds.Add("acc_cand");

        // Workload requested model is gemini-pro
        context.Adapter.RequestedModelOrTier = "gemini-pro";
        context.Adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));

        await using var router = context.CreateRouter(quotaStore: store);
        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        var eval = Assert.Single(result.Candidates);
        Assert.False(eval.IsEligible);
        Assert.Contains("unknown", eval.IneligibilityReason);
    }

    // 25. unknown requested model still fails closed
    [Fact]
    public async Task Scenario25_UnknownRequestedModelStillFailsClosed()
    {
        using var context = new TestContext();
        string storePath = Path.Combine(context.TempDir, "quota-observations.json");
        var store = new DurableQuotaObservationStore(storePath);

        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");
        context.AccountStore.Accounts[current.Id] = current;
        context.AccountStore.Accounts[candidate.Id] = candidate;
        context.AccountStore.ActiveAccountId = "acc_curr";
        context.SessionVault.StoredIds.Add("acc_curr");
        context.SessionVault.StoredIds.Add("acc_cand");

        await using var router = context.CreateRouter(quotaStore: store);
        context.Adapter.RequestedModelOrTier = null;
        context.Adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            new List<ModelQuotaDto> { new("Gemini Pro", "gemini-pro", 0.05, null, false) },
            null, null
        ));

        var result = await router.EvaluateCycleAsync();

        Assert.False(result.ShouldSwitch);
        Assert.Null(result.BestCandidate);
        Assert.Contains("Requested workload model is unknown; automatic switching requires live model evidence.", result.Reason);
    }

    private sealed class TestContext : IDisposable
    {
        public string TempDir { get; } = Path.Combine(Path.GetTempPath(), $"ag2_test_cf_{Guid.NewGuid():N}");
        public FakeAccountStore AccountStore { get; } = new();
        public FakeSessionVault SessionVault { get; } = new();
        public MockAG2Adapter Adapter { get; } = new();
        public FakeSwitchCoordinator SwitchCoordinator { get; } = new();
        public RoutingSafetyGate SafetyGate { get; } = new();

        public NativeAutoRouter CreateRouter(
            IQuotaObservationStore? quotaStore = null,
            RouterConfigDto? config = null)
        {
            SwitchCoordinator.CommitSuccess = id => AccountStore.ActiveAccountId = id;
            Adapter.RequestedModelOrTier = "gemini-pro";
            Adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
            Adapter.GetCurrentAccountFunc = _ => Task.FromResult<AccountIdentityDto?>(
                AccountStore.ActiveAccountId is { } id && AccountStore.Accounts.TryGetValue(id, out var account)
                    ? new AccountIdentityDto(account.Email)
                    : null);

            return new NativeAutoRouter(
                AccountStore,
                SessionVault,
                Adapter,
                SwitchCoordinator,
                initialConfig: config ?? new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30),
                safetyGate: SafetyGate,
                quotaObservationStore: quotaStore
            );
        }

        public void Dispose()
        {
            SessionVault.Dispose();
            if (Directory.Exists(TempDir))
            {
                try { Directory.Delete(TempDir, recursive: true); } catch { }
            }
        }
    }

    private sealed class FakeAccountStore : IAccountStore
    {
        public Dictionary<string, AccountMetadata> Accounts { get; } = new(StringComparer.Ordinal);
        public string? ActiveAccountId { get; set; }

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AccountMetadata>>(Accounts.Values.ToList());

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.TryGetValue(id, out var a) ? a : null);

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.Values.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActiveAccountId);

        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default)
        {
            ActiveAccountId = id;
            return Task.CompletedTask;
        }

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default)
        {
            if (ActiveAccountId == expectedId)
            {
                ActiveAccountId = newId;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeSessionVault : ISessionVault, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ag2_vault_cf_{Guid.NewGuid():N}");
        public HashSet<string> StoredIds { get; } = new(StringComparer.Ordinal);

        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Contains(accountId));

        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(StoredIds.Contains(accountId) ? new byte[] { 1, 2, 3 } : null);

        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Remove(accountId));

        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(StoredIds.ToList());

        public string GetVaultPath() => Path.Combine(_directory, "vault");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                try { Directory.Delete(_directory, recursive: true); } catch { }
            }
        }
    }

    private sealed class FakeSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public Action<string>? CommitSuccess { get; set; }
        public string NextCode { get; set; } = SwitchResultCodes.Success;
        public string JournalRecoveryState { get; set; } = JournalRecoveryStates.None;
        public bool QuarantineActive { get; set; }
        public int CallCount { get; private set; }

        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null,
            QuarantineActive, JournalRecoveryState);

        public Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present."));

        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var result = MakeResult(targetAccountId);
            if (result.Success) CommitSuccess?.Invoke(targetAccountId);
            return Task.FromResult(result);
        }

        public NativeSwitchResult MakeResult(string targetAccountId)
        {
            bool success = NextCode == SwitchResultCodes.Success;
            string state = success ? NativeSwitchStates.Complete :
                NextCode == SwitchResultCodes.SwitchFailedRolledBack ? NativeSwitchStates.RolledBack :
                NativeSwitchStates.Failed;

            return new NativeSwitchResult(
                TransactionId: "tx_123",
                Success: success,
                Code: NextCode,
                State: state,
                TargetAccountId: targetAccountId,
                TargetEmail: "target@example.com",
                PreviousAccountId: "acc_current",
                PreviousEmail: "curr@example.com",
                Message: NextCode,
                StagesCompleted: Array.Empty<string>(),
                StartedAt: DateTimeOffset.UtcNow.ToString("O"),
                FinishedAt: DateTimeOffset.UtcNow.ToString("O"),
                ManualRecoveryRequired: NextCode == SwitchResultCodes.SwitchFailedRollbackFailed
            );
        }

        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
