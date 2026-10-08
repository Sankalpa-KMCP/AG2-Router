using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AG2Router.App.Server;
using AG2Router.App.Views;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class PoolExhaustionAndFailureClassificationTests
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
    public void CandidateSelector_WhenAllCandidatesZeroQuota_ReportsAllExhausted_WithEarliestResetTime()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand1 = CreateAccount("acc_cand1", "cand1@example.com");
        var cand2 = CreateAccount("acc_cand2", "cand2@example.com");

        var reset1 = now.AddHours(2).ToString("O");
        var reset2 = now.AddHours(4).ToString("O");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand1"] = new AccountModelQuotaObservation("acc_cand1", "gemini-pro", 0.0, reset1, now.AddMinutes(-5), "test"),
            ["acc_cand2"] = new AccountModelQuotaObservation("acc_cand2", "gemini-pro", 0.0, reset2, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1, cand2],
            new Dictionary<string, double> { ["acc_cand1"] = 0.0, ["acc_cand2"] = 0.0 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1", "acc_cand2" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.False(result.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.AllExhausted, result.PoolStatus.ReasonCode);
        Assert.Equal(reset1, result.PoolStatus.EarliestResetTime);
        Assert.Equal(2, result.PoolStatus.EnrolledCandidatesCount);
        Assert.Equal(2, result.PoolStatus.EligibleCandidatesCount);
        Assert.Equal(0, result.PoolStatus.UsableCandidatesCount);
    }

    [Fact]
    public void CandidateSelector_WhenSomeExhaustedAndSomeBelowMinimum_ReportsQuotaDepleted_WithEarliestResetTime()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand1 = CreateAccount("acc_cand1", "cand1@example.com");
        var cand2 = CreateAccount("acc_cand2", "cand2@example.com");

        var reset1 = now.AddHours(3).ToString("O");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand1"] = new AccountModelQuotaObservation("acc_cand1", "gemini-pro", 0.0, reset1, now.AddMinutes(-5), "test"),
            ["acc_cand2"] = new AccountModelQuotaObservation("acc_cand2", "gemini-pro", 0.20, null, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1, cand2],
            new Dictionary<string, double> { ["acc_cand1"] = 0.0, ["acc_cand2"] = 0.20 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1", "acc_cand2" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.False(result.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.QuotaDepleted, result.PoolStatus.ReasonCode);
        Assert.Equal(reset1, result.PoolStatus.EarliestResetTime);
    }

    [Fact]
    public void CandidateSelector_WhenAllBelowMinimum_ReportsAllBelowMinimum()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand1 = CreateAccount("acc_cand1", "cand1@example.com");
        var cand2 = CreateAccount("acc_cand2", "cand2@example.com");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand1"] = new AccountModelQuotaObservation("acc_cand1", "gemini-pro", 0.25, null, now.AddMinutes(-5), "test"),
            ["acc_cand2"] = new AccountModelQuotaObservation("acc_cand2", "gemini-pro", 0.20, null, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1, cand2],
            new Dictionary<string, double> { ["acc_cand1"] = 0.25, ["acc_cand2"] = 0.20 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1", "acc_cand2" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.False(result.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.AllBelowMinimum, result.PoolStatus.ReasonCode);
    }

    [Fact]
    public void CandidateSelector_WhenAllAccountsLackObservationsOrExpired_ReportsEvidenceStaleOrUnknown()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand1 = CreateAccount("acc_cand1", "cand1@example.com");

        // Stale observation older than 2 hours
        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand1"] = new AccountModelQuotaObservation("acc_cand1", "gemini-pro", 0.80, null, now.AddHours(-3), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1],
            new Dictionary<string, double>(),
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.False(result.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.EvidenceStaleOrUnknown, result.PoolStatus.ReasonCode);
        Assert.Null(result.PoolStatus.EarliestResetTime);
    }

    [Fact]
    public void CandidateSelector_WhenAllEligibleCandidatesInCooldown_ReportsAllInCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand1 = CreateAccount("acc_cand1", "cand1@example.com");

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1],
            new Dictionary<string, double> { ["acc_cand1"] = 0.80 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1" },
            cooldownAccountIds: new HashSet<string> { "acc_cand1" },
            relevantModelKeys: ["gemini-pro"],
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.False(result.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.AllInCooldown, result.PoolStatus.ReasonCode);
    }

    [Fact]
    public void CandidateSelector_WhenAllCandidatesReserveOnly_ReportsReserveOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand1 = CreateAccount("acc_cand1", "cand1@example.com", isReserve: true);

        // Case 1: Reserve account has usable quota -> usable, but flagged ReserveOnly
        var resultUsable = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1],
            new Dictionary<string, double> { ["acc_cand1"] = 0.80 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1" },
            evaluationTimeUtc: now
        );

        Assert.True(resultUsable.ShouldSwitch);
        Assert.NotNull(resultUsable.BestCandidate);
        Assert.NotNull(resultUsable.PoolStatus);
        Assert.True(resultUsable.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.ReserveOnly, resultUsable.PoolStatus.ReasonCode);
        Assert.Equal(1, resultUsable.PoolStatus.UsableCandidatesCount);

        // Case 2: Reserve account has no quota observations (not usable) -> no usable candidate, ReserveOnly
        var resultNoQuota = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1],
            new Dictionary<string, double>(),
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1" },
            evaluationTimeUtc: now
        );

        Assert.False(resultNoQuota.ShouldSwitch);
        Assert.NotNull(resultNoQuota.PoolStatus);
        Assert.False(resultNoQuota.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.ReserveOnly, resultNoQuota.PoolStatus.ReasonCode);
        Assert.Equal(0, resultNoQuota.PoolStatus.UsableCandidatesCount);
    }

    [Fact]
    public void CandidateSelector_WhenCandidatesFailValidationOrLackSession_ReportsValidationOrSessionFailed()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand1 = CreateAccount("acc_cand1", "cand1@example.com", validationStatus: "FAILED");
        var cand2 = CreateAccount("acc_cand2", "cand2@example.com", hasVaultedSession: false);

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1, cand2],
            new Dictionary<string, double> { ["acc_cand1"] = 0.80, ["acc_cand2"] = 0.80 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr" }, // cand1 and cand2 not vaulted
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.False(result.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.ValidationOrSessionFailed, result.PoolStatus.ReasonCode);
    }

    [Fact]
    public void CandidateSelector_WhenNoSecondaryAccountsExist_ReportsNoEnrolledAlternatives()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current],
            new Dictionary<string, double>(),
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr" },
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.False(result.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.NoEnrolledAlternatives, result.PoolStatus.ReasonCode);
        Assert.Equal(0, result.PoolStatus.EnrolledCandidatesCount);
    }

    [Fact]
    public void CandidateSelector_WhenUsableCandidateExists_ReportsReady()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var candidate = CreateAccount("acc_cand", "cand@example.com");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand"] = new AccountModelQuotaObservation("acc_cand", "gemini-pro", 0.85, null, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, candidate],
            new Dictionary<string, double> { ["acc_cand"] = 0.85 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.True(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.True(result.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.Ready, result.PoolStatus.ReasonCode);
        Assert.Equal(1, result.PoolStatus.UsableCandidatesCount);
    }

    [Fact]
    public void FindEarliestResetTime_ValidFutureDates_ReturnsMinimumFutureResetTime()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        var obs = new Dictionary<string, AccountModelQuotaObservation>
        {
            ["a"] = new("a", "m", 0, "2026-10-06T14:30:00Z", now, "src"),
            ["b"] = new("b", "m", 0, "2026-10-06T13:15:00Z", now, "src"),
            ["c"] = new("c", "m", 0, "2026-10-06T11:00:00Z", now, "src"), // in the past
            ["d"] = new("d", "m", 0, "invalid-date", now, "src")
        };

        var earliest = CandidateSelector.FindEarliestResetTime(obs, null, now);
        Assert.Equal("2026-10-06T13:15:00Z", earliest);
    }

    [Fact]
    public void FindEarliestResetTime_NoFutureOrInvalid_ReturnsNull()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        var obs = new Dictionary<string, AccountModelQuotaObservation>
        {
            ["c"] = new("c", "m", 0, "2026-10-06T11:00:00Z", now, "src"), // in the past
            ["d"] = new("d", "m", 0, "not-a-date", now, "src"),
            ["e"] = new("e", "m", 0, null, now, "src")
        };

        var earliest = CandidateSelector.FindEarliestResetTime(obs, null, now);
        Assert.Null(earliest);
    }

    [Fact]
    public void EarliestResetTime_IgnoresCurrentAccount()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand = CreateAccount("acc_cand", "cand@example.com");

        var activeReset = now.AddHours(1).ToString("O");
        var candidateReset = now.AddHours(3).ToString("O");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_curr"] = new("acc_curr", "gemini-pro", 0.0, activeReset, now.AddMinutes(-5), "test"),
            ["acc_cand"] = new("acc_cand", "gemini-pro", 0.0, candidateReset, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand],
            new Dictionary<string, double> { ["acc_cand"] = 0.0 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.Equal(candidateReset, result.PoolStatus.EarliestResetTime);
    }

    [Fact]
    public void EarliestResetTime_IgnoresValidationFailedAccount()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var candFailed = CreateAccount("acc_failed", "failed@example.com", validationStatus: "FAILED");
        var candValid = CreateAccount("acc_valid", "valid@example.com");

        var failedReset = now.AddHours(1).ToString("O");
        var validReset = now.AddHours(3).ToString("O");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_failed"] = new("acc_failed", "gemini-pro", 0.0, failedReset, now.AddMinutes(-5), "test"),
            ["acc_valid"] = new("acc_valid", "gemini-pro", 0.0, validReset, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, candFailed, candValid],
            new Dictionary<string, double> { ["acc_failed"] = 0.0, ["acc_valid"] = 0.0 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_failed", "acc_valid" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.Equal(validReset, result.PoolStatus.EarliestResetTime);
    }

    [Fact]
    public void EarliestResetTime_IgnoresUnvaultedAccount()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var candUnvaulted = CreateAccount("acc_unvaulted", "unvaulted@example.com", hasVaultedSession: false);
        var candValid = CreateAccount("acc_valid", "valid@example.com");

        var unvaultedReset = now.AddHours(1).ToString("O");
        var validReset = now.AddHours(3).ToString("O");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_unvaulted"] = new("acc_unvaulted", "gemini-pro", 0.0, unvaultedReset, now.AddMinutes(-5), "test"),
            ["acc_valid"] = new("acc_valid", "gemini-pro", 0.0, validReset, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, candUnvaulted, candValid],
            new Dictionary<string, double> { ["acc_unvaulted"] = 0.0, ["acc_valid"] = 0.0 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_valid" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.Equal(validReset, result.PoolStatus.EarliestResetTime);
    }

    [Fact]
    public void EarliestResetTime_IncludesValidReserveCandidate()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var candReserve = CreateAccount("acc_reserve", "reserve@example.com", isReserve: true);
        var candStandard = CreateAccount("acc_standard", "standard@example.com");

        var reserveReset = now.AddHours(2).ToString("O");
        var standardReset = now.AddHours(4).ToString("O");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_reserve"] = new("acc_reserve", "gemini-pro", 0.0, reserveReset, now.AddMinutes(-5), "test"),
            ["acc_standard"] = new("acc_standard", "gemini-pro", 0.0, standardReset, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, candReserve, candStandard],
            new Dictionary<string, double> { ["acc_reserve"] = 0.0, ["acc_standard"] = 0.0 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_reserve", "acc_standard" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.Equal(reserveReset, result.PoolStatus.EarliestResetTime);
    }

    [Fact]
    public void EarliestResetTime_SelectsEarliestValidAlternative()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var cand1 = CreateAccount("acc_cand1", "cand1@example.com");
        var cand2 = CreateAccount("acc_cand2", "cand2@example.com");
        var cand3 = CreateAccount("acc_cand3", "cand3@example.com");

        var reset1 = now.AddHours(5).ToString("O");
        var reset2 = now.AddHours(2).ToString("O");
        var reset3 = now.AddHours(4).ToString("O");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_cand1"] = new("acc_cand1", "gemini-pro", 0.0, reset1, now.AddMinutes(-5), "test"),
            ["acc_cand2"] = new("acc_cand2", "gemini-pro", 0.0, reset2, now.AddMinutes(-5), "test"),
            ["acc_cand3"] = new("acc_cand3", "gemini-pro", 0.0, reset3, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, cand1, cand2, cand3],
            new Dictionary<string, double> { ["acc_cand1"] = 0.0, ["acc_cand2"] = 0.0, ["acc_cand3"] = 0.0 },
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_cand1", "acc_cand2", "acc_cand3" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.Equal(reset2, result.PoolStatus.EarliestResetTime);
    }

    [Fact]
    public void EarliestResetTime_ReturnsNull_WhenOnlyActiveOrIneligibleEvidenceExists()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new RouterConfigDto(LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30);
        var current = CreateAccount("acc_curr", "curr@example.com");
        var candFailed = CreateAccount("acc_failed", "failed@example.com", validationStatus: "FAILED");
        var candUnvaulted = CreateAccount("acc_unvaulted", "unvaulted@example.com", hasVaultedSession: false);

        var activeReset = now.AddHours(1).ToString("O");
        var failedReset = now.AddHours(2).ToString("O");
        var unvaultedReset = now.AddHours(3).ToString("O");

        var modelObservations = new Dictionary<string, AccountModelQuotaObservation>(StringComparer.Ordinal)
        {
            ["acc_curr"] = new("acc_curr", "gemini-pro", 0.0, activeReset, now.AddMinutes(-5), "test"),
            ["acc_failed"] = new("acc_failed", "gemini-pro", 0.0, failedReset, now.AddMinutes(-5), "test"),
            ["acc_unvaulted"] = new("acc_unvaulted", "gemini-pro", 0.0, unvaultedReset, now.AddMinutes(-5), "test")
        };

        var result = CandidateSelector.SelectBestCandidate(
            "acc_curr",
            0.05,
            [current, candFailed, candUnvaulted],
            new Dictionary<string, double>(),
            config,
            vaultedAccountIds: new HashSet<string> { "acc_curr", "acc_failed" },
            relevantModelKeys: ["gemini-pro"],
            candidateModelObservations: modelObservations,
            evaluationTimeUtc: now
        );

        Assert.False(result.ShouldSwitch);
        Assert.NotNull(result.PoolStatus);
        Assert.Null(result.PoolStatus.EarliestResetTime);
    }

    [Fact]
    public void TrayAndFlyoutFormatting_AccuratelyFormatsStates()
    {
        // 1. Disabled
        var tipDisabled = AG2Router.App.App.FormatTrayTooltip("HEALTHY", false, "IDLE", null);
        Assert.Contains("Auto: Off", tipDisabled);
        var (flyoutDisabledText, flyoutDisabledTip) = QuickStatusWindow.FormatAutoSwitchStatus(false, null);
        Assert.Equal("Disabled", flyoutDisabledText);
        Assert.Null(flyoutDisabledTip);

        // 2. Ready
        var readyPool = new CandidatePoolStatusDto(true, CandidatePoolReasonCodes.Ready, "Ready", 2, 2, 1);
        var tipReady = AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", readyPool);
        Assert.Contains("Auto: IDLE", tipReady);
        var (flyoutReadyText, _) = QuickStatusWindow.FormatAutoSwitchStatus(true, readyPool);
        Assert.Equal("Active", flyoutReadyText);

        // 3. Pool Exhausted
        var exhaustedPool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.AllExhausted, "All accounts exhausted", 2, 2, 0, "2026-10-07T00:00:00Z");
        var tipExhausted = AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", exhaustedPool);
        Assert.Contains("Auto: Pool exhausted", tipExhausted);
        var (flyoutExText, flyoutExTip) = QuickStatusWindow.FormatAutoSwitchStatus(true, exhaustedPool);
        Assert.Equal("Pool Exhausted", flyoutExText);
        Assert.Equal("All accounts exhausted", flyoutExTip);

        // 4. Cooldown
        var cdPool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.AllInCooldown, "In cooldown", 2, 0, 0);
        var tipCd = AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", cdPool);
        Assert.Contains("Auto: Cooling down", tipCd);
        var (flyoutCdText, _) = QuickStatusWindow.FormatAutoSwitchStatus(true, cdPool);
        Assert.Equal("In Cooldown", flyoutCdText);

        // 5. Stale Evidence
        var stalePool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.EvidenceStaleOrUnknown, "Observations stale", 2, 2, 0);
        var tipStale = AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", stalePool);
        Assert.Contains("Auto: No fresh evidence", tipStale);
        var (flyoutStaleText, _) = QuickStatusWindow.FormatAutoSwitchStatus(true, stalePool);
        Assert.Equal("No Fresh Evidence", flyoutStaleText);

        // 6. Reserve Only
        var reservePool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.ReserveOnly, "All reserve", 2, 2, 0);
        var tipReserve = AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", reservePool);
        Assert.Contains("Auto: Reserve only", tipReserve);
        var (flyoutReserveText, _) = QuickStatusWindow.FormatAutoSwitchStatus(true, reservePool);
        Assert.Equal("Reserve Only", flyoutReserveText);

        // 7. Validation / Session failed
        var invalidPool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.ValidationOrSessionFailed, "Invalid sessions", 2, 0, 0);
        var tipInvalid = AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", invalidPool);
        Assert.Contains("Auto: Session invalid", tipInvalid);
        var (flyoutInvalidText, _) = QuickStatusWindow.FormatAutoSwitchStatus(true, invalidPool);
        Assert.Equal("Invalid Sessions", flyoutInvalidText);

        // 8. No Alternatives
        var noAltPool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.NoEnrolledAlternatives, "No alternatives", 0, 0, 0);
        var tipNoAlt = AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", noAltPool);
        Assert.Contains("Auto: No alternatives", tipNoAlt);
        var (flyoutNoAltText, _) = QuickStatusWindow.FormatAutoSwitchStatus(true, noAltPool);
        Assert.Equal("No Alternatives", flyoutNoAltText);

        // 9. Below Minimum
        var belowMinPool = new CandidatePoolStatusDto(false, CandidatePoolReasonCodes.AllBelowMinimum, "Below minimum", 2, 2, 0);
        var tipBelowMin = AG2Router.App.App.FormatTrayTooltip("HEALTHY", true, "IDLE", belowMinPool);
        Assert.Contains("Auto: Below minimum", tipBelowMin);
        var (flyoutBelowMinText, _) = QuickStatusWindow.FormatAutoSwitchStatus(true, belowMinPool);
        Assert.Equal("Below Minimum", flyoutBelowMinText);
    }

    [Fact]
    public async Task LoopbackServer_StatusEndpoints_SerializePoolStatusMatchingContract()
    {
        var server = new LoopbackServer();
        try
        {
            var pool = new CandidatePoolStatusDto(
                HasUsableCandidate: false,
                ReasonCode: CandidatePoolReasonCodes.AllExhausted,
                Message: "All candidate accounts are quota-exhausted.",
                EnrolledCandidatesCount: 2,
                EligibleCandidatesCount: 2,
                UsableCandidatesCount: 0,
                EarliestResetTime: "2026-10-07T12:00:00Z"
            );

            var routerStatus = new RouterStatusDto(
                State: RoutingSafetyGateState.Idle,
                AutoSwitchEnabled: true,
                ActiveAccountId: "acc1",
                ActiveAccountEmail: "acc1@example.com",
                PendingTargetAccountId: null,
                LastEvaluatedAt: "2026-10-06T12:00:00Z",
                LastDecisionReason: "All accounts exhausted",
                Config: new RouterConfigDto(AutoSwitchEnabled: true),
                PoolStatus: pool
            );

            var systemStatus = new SystemStatusDto(
                Status: "ok",
                Ag2: new Ag2StatusDto(true, "HEALTHY", null, "Connected"),
                Router: routerStatus,
                Telemetry: null
            );

            await server.StartAsync(0, statusProvider: () => systemStatus);

            using var client = new HttpClient();

            // 1. Test /api/status
            using var responseStatus = await client.GetAsync($"{server.BoundUrl}/api/status");
            Assert.Equal(HttpStatusCode.OK, responseStatus.StatusCode);
            var statusJson = JsonDocument.Parse(await responseStatus.Content.ReadAsStringAsync());
            var routerElem = statusJson.RootElement.GetProperty("router");
            Assert.True(routerElem.TryGetProperty("poolStatus", out var poolElem));
            Assert.False(poolElem.GetProperty("hasUsableCandidate").GetBoolean());
            Assert.Equal(CandidatePoolReasonCodes.AllExhausted, poolElem.GetProperty("reasonCode").GetString());
            Assert.Equal("2026-10-07T12:00:00Z", poolElem.GetProperty("earliestResetTime").GetString());
            Assert.Equal(2, poolElem.GetProperty("enrolledCandidatesCount").GetInt32());

            // 2. Test /api/router/status (when autoRouter null, provides default)
            using var responseRouter = await client.GetAsync($"{server.BoundUrl}/api/router/status");
            Assert.Equal(HttpStatusCode.OK, responseRouter.StatusCode);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativeAutoRouter_ExposesPoolStatus_InRouterStatusDto_AndCandidateEvidenceStatusDto()
    {
        var accountStore = new TestAccountStore();
        var sessionVault = new TestSessionVault();
        var adapter = new TestAdapter();
        var coordinator = new TestSwitchCoordinator();
        var quotaStore = new TestQuotaObservationStore();

        var curr = CreateAccount("acc_curr", "curr@example.com");
        var cand = CreateAccount("acc_cand", "cand@example.com");
        accountStore.Accounts[curr.Id] = curr;
        accountStore.Accounts[cand.Id] = cand;
        accountStore.ActiveAccountId = curr.Id;
        sessionVault.StoredIds.Add(curr.Id);
        sessionVault.StoredIds.Add(cand.Id);

        var config = new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: "gemini-pro");
        var router = new NativeAutoRouter(accountStore, sessionVault, adapter, coordinator, initialConfig: config, quotaObservationStore: quotaStore);

        // Before any authoritative evaluation, the dashboard serves no fabricated pool
        // status: PoolStatus is unavailable until a real routing evaluation publishes one.
        var evidenceStatus = await router.GetCandidateEvidenceStatusAsync();
        Assert.Null(evidenceStatus.PoolStatus);

        // Record exhausted quota observation for candidate
        quotaStore.Observations.Add(new AccountModelQuotaObservation("acc_cand", "gemini-pro", 0.0, null, DateTimeOffset.UtcNow, "test"));
        router.SetObservedQuota("acc_cand", 0.0, [new("Gemini Pro", "gemini-pro", 0.0, null, true)]);

        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(new QuotaSnapshotDto(
            DateTime.UtcNow.ToString("O"),
            [new ModelQuotaDto("Gemini Pro", "gemini-pro", 0.05, null, false)],
            null, null
        ));
        adapter.GetActivityStateFunc = _ => Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTime.UtcNow.ToString("O")));

        var evalResult = await router.EvaluateCycleAsync();
        Assert.False(evalResult.ShouldSwitch);
        Assert.NotNull(evalResult.PoolStatus);
        Assert.Equal(CandidatePoolReasonCodes.AllExhausted, evalResult.PoolStatus.ReasonCode);

        // GetStatus now returns this evaluated pool status
        var status = router.GetStatus();
        Assert.NotNull(status.PoolStatus);
        Assert.False(status.PoolStatus.HasUsableCandidate);
        Assert.Equal(CandidatePoolReasonCodes.AllExhausted, status.PoolStatus.ReasonCode);
    }

    [Fact]
    public async Task TransportFailure_DoesNotMarkQuotaAsExhausted_AndSetsTelemetryUnavailable()
    {
        var accountStore = new TestAccountStore();
        var sessionVault = new TestSessionVault();
        var adapter = new TestAdapter();
        var coordinator = new TestSwitchCoordinator();
        var quotaStore = new TestQuotaObservationStore();

        var curr = CreateAccount("acc_curr", "curr@example.com");
        var cand = CreateAccount("acc_cand", "cand@example.com");
        accountStore.Accounts[curr.Id] = curr;
        accountStore.Accounts[cand.Id] = cand;
        accountStore.ActiveAccountId = curr.Id;
        sessionVault.StoredIds.Add(curr.Id);
        sessionVault.StoredIds.Add(cand.Id);

        // Candidate has healthy durable observed quota
        quotaStore.Observations.Add(new AccountModelQuotaObservation("acc_cand", "gemini-pro", 0.80, null, DateTimeOffset.UtcNow, "test"));

        var router = new NativeAutoRouter(accountStore, sessionVault, adapter, coordinator,
            initialConfig: new RouterConfigDto(AutoSwitchEnabled: true, WorkloadModelKey: "gemini-pro"),
            quotaObservationStore: quotaStore);
        router.SetObservedQuota("acc_cand", 0.80, [new("Gemini Pro", "gemini-pro", 0.80, null, false)]);

        // Simulate loopback transport failure: Language server unreachable (adapter returns OFFLINE)
        adapter.GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(false, "OFFLINE", null, "Connect-RPC loopback daemon unavailable: 503"));
        adapter.GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(null);

        var result = await router.EvaluateCycleAsync();

        // Must NOT trigger switch, and must NOT mark quota as 0% / exhausted
        Assert.False(result.ShouldSwitch);
        Assert.Contains("unavailable", result.Reason, StringComparison.OrdinalIgnoreCase);

        // Candidate quota must remain 80% (not cleared or zeroed by upstream transport error)
        var evidence = await router.GetCandidateEvidenceStatusAsync();
        var candStatus = Assert.Single(evidence.Candidates);
        Assert.Equal(0.80, candStatus.RemainingFraction);
        Assert.Equal("USABLE", candStatus.State);
    }

    private sealed class TestQuotaObservationStore : IQuotaObservationStore
    {
        public List<AccountModelQuotaObservation> Observations { get; } = [];

        public Task RecordCompleteSnapshotAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations,
            DateTimeOffset observedAtUtc, string source, CancellationToken cancellationToken = default)
        {
            Observations.RemoveAll(o => o.AccountId == accountId);
            Observations.AddRange(observations);
            return Task.CompletedTask;
        }

        public Task RecordObservationsAsync(string accountId, IEnumerable<AccountModelQuotaObservation> observations, CancellationToken cancellationToken = default)
        {
            Observations.AddRange(observations);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetAllObservationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AccountModelQuotaObservation>>(Observations.ToList());

        public Task<IReadOnlyList<AccountModelQuotaObservation>> GetObservationsForAccountAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AccountModelQuotaObservation>>(Observations.Where(o => o.AccountId == accountId).ToList());

        public Task<AccountModelQuotaObservation?> GetObservationAsync(string accountId, string modelKey, CancellationToken cancellationToken = default)
            => Task.FromResult(Observations.FirstOrDefault(o => o.AccountId == accountId && string.Equals(o.ModelKey, modelKey, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class TestAccountStore : IAccountStore
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

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default)
        {
            if (ActiveAccountId == expectedId)
            {
                ActiveAccountId = newId;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class TestSessionVault : ISessionVault
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ag2_pool_test_{Guid.NewGuid():N}");
        public HashSet<string> StoredIds { get; } = new(StringComparer.Ordinal);

        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(StoredIds.Contains(accountId));
        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(StoredIds.Contains(accountId) ? new byte[] { 1 } : null);
        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default) => Task.FromResult(StoredIds.Remove(accountId));
        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(StoredIds.ToList());
        public string GetVaultPath() => _dir;
    }

    private sealed class TestAdapter : IAG2Adapter
    {
        public Func<CancellationToken, Task<Ag2StatusDto>>? GetStatusFunc { get; set; }
        public Func<CancellationToken, Task<QuotaSnapshotDto?>>? GetQuotaFunc { get; set; }
        public Func<CancellationToken, Task<ActivityStatusDto>>? GetActivityStateFunc { get; set; }
        public AccountIdentityDto? Identity { get; set; }

        public Task<Ag2StatusDto> GetStatusAsync(CancellationToken cancellationToken = default) =>
            GetStatusFunc != null ? GetStatusFunc(cancellationToken) : Task.FromResult(new Ag2StatusDto(true, "HEALTHY", null, "OK"));

        public Task<AccountIdentityDto?> GetCurrentAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountIdentityDto?>(Identity ?? new AccountIdentityDto("curr@example.com", "Current User"));

        public Task<QuotaSnapshotDto?> GetQuotaAsync(CancellationToken cancellationToken = default) =>
            GetQuotaFunc != null ? GetQuotaFunc(cancellationToken) : Task.FromResult<QuotaSnapshotDto?>(null);

        public Task<AccountQuotaObservation> GetAccountQuotaObservationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountQuotaObservation(
                Identity ?? new AccountIdentityDto("curr@example.com", "Current User"),
                GetQuotaFunc != null ? GetQuotaFunc(cancellationToken).GetAwaiter().GetResult() : null));

        public Task<RequestedModelObservation> GetRequestedModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new RequestedModelObservation(
                Identity ?? new AccountIdentityDto("curr@example.com", "Current User"), "gemini-pro"));

        public Task<ActivityStatusDto> GetActivityStateAsync(CancellationToken cancellationToken = default) =>
            GetActivityStateFunc != null ? GetActivityStateFunc(cancellationToken) : Task.FromResult(new ActivityStatusDto("IDLE", 0, 0, DateTimeOffset.UtcNow.ToString("O")));
    }

    private sealed class TestSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public bool CanAdmitSwitch(out string? blockingReason)
        {
            blockingReason = null;
            return true;
        }
        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null, false, JournalRecoveryStates.None);
        public Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "None"));
        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<NativeSwitchResult> SwitchAutomaticallyAsync(string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent, string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<NativeSwitchResult> SwitchAutomaticallyAsync(string targetAccountId, string? expectedActiveAccountId, Func<bool> planIsCurrent, string? requiredWorkloadModelKey, double? minimumCandidateQuotaPercent, Func<CancellationToken, Task<IDisposable>> acquireInterruptionAdmissionAsync, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
