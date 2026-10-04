using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

/// <summary>
/// Domain semantics for the usage-accounting model: deterministic privacy-minimizing call
/// keys, token validation and computed formulas, output-composition policy, and the account/
/// time attribution contracts that later usage stages depend on.
/// </summary>
public class UsageDomainTests
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private static UsageCallRecord Call(
        string? cascadeId = "cascade-1",
        string? responseId = "response-1",
        long inputTokens = 3372,
        long outputTokens = 500,
        long? responseOutputTokens = 420,
        long? thinkingOutputTokens = 80,
        long? cacheReadTokens = null,
        string? responseModel = "claude-sonnet-4-5",
        string? provider = "anthropic",
        DateTimeOffset? firstObservedAtUtc = null,
        UsageTimeAttribution timeAttribution = UsageTimeAttribution.ObservationTime,
        UsageAccountAttributionBasis accountAttributionBasis = UsageAccountAttributionBasis.VerifiedObservation,
        string? accountId = "acc_synthetic01",
        string source = "SyntheticTest") =>
        new(
            UsageCallKeys.Compute(cascadeId ?? "cascade-1", responseId ?? "response-1"),
            UsageCallKeys.CanonicalizeResponseModel(responseModel),
            provider,
            inputTokens,
            outputTokens,
            responseOutputTokens,
            thinkingOutputTokens,
            cacheReadTokens,
            firstObservedAtUtc ?? Observed,
            timeAttribution,
            accountAttributionBasis,
            accountId,
            source);

    #region A. Call key

    [Fact]
    public void Compute_IsDeterministic_AndLowercaseHex64()
    {
        string first = UsageCallKeys.Compute("cascade-7", "response-9");
        string second = UsageCallKeys.Compute("cascade-7", "response-9");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            $"Call key must be lowercase hexadecimal; found '{c}'."));
    }

    [Fact]
    public void Compute_DifferentiatesCascades_Responses_AndBoth()
    {
        string baseline = UsageCallKeys.Compute("cascade-a", "response-1");
        string otherCascade = UsageCallKeys.Compute("cascade-b", "response-1");
        string otherResponse = UsageCallKeys.Compute("cascade-a", "response-2");
        string both = UsageCallKeys.Compute("cascade-b", "response-2");

        Assert.NotEqual(baseline, otherCascade);
        Assert.NotEqual(baseline, otherResponse);
        Assert.NotEqual(baseline, both);
        Assert.NotEqual(otherCascade, otherResponse);
    }

    [Fact]
    public void Compute_LengthPrefixes_PreventConcatenationAmbiguity()
    {
        // ("ab","c") and ("a","bc") concatenate to the same raw string; the length-prefixed
        // encoding must keep them distinct call identities.
        Assert.NotEqual(UsageCallKeys.Compute("ab", "c"), UsageCallKeys.Compute("a", "bc"));
    }

    [Fact]
    public void Compute_RejectsNull_Empty_Whitespace_AndOversizedIdentifiers()
    {
        Assert.Throws<ArgumentNullException>(() => UsageCallKeys.Compute(null!, "r"));
        Assert.Throws<ArgumentNullException>(() => UsageCallKeys.Compute("c", null!));
        Assert.Throws<ArgumentException>(() => UsageCallKeys.Compute("", "r"));
        Assert.Throws<ArgumentException>(() => UsageCallKeys.Compute("c", "   "));
        Assert.Throws<ArgumentException>(() => UsageCallKeys.Compute(new string('c', 4097), "r"));
        Assert.Throws<ArgumentException>(() => UsageCallKeys.Compute("c", new string('r', 4097)));
    }

    #endregion

    #region Model canonicalization

    [Fact]
    public void CanonicalizeResponseModel_TrimsLowercases_AndMapsAbsenceToNull()
    {
        Assert.Equal("claude-sonnet-4-5", UsageCallKeys.CanonicalizeResponseModel("  Claude-Sonnet-4-5 "));
        Assert.Null(UsageCallKeys.CanonicalizeResponseModel(null));
        Assert.Null(UsageCallKeys.CanonicalizeResponseModel("   "));
    }

    [Fact]
    public void Record_NormalizesModel_AndPreservesUnknown()
    {
        var known = Call(responseModel: " Claude-Sonnet-4-5 ");
        Assert.Equal("claude-sonnet-4-5", known.ResponseModelKey);

        var unknown = Call(responseModel: null);
        Assert.Null(unknown.ResponseModelKey);
    }

    #endregion

    #region B. Token validation

    [Fact]
    public void Record_AcceptsZero_AndPositiveTokenCounters()
    {
        var call = Call(inputTokens: 0, outputTokens: 0, responseOutputTokens: 0, thinkingOutputTokens: 0, cacheReadTokens: 0);
        Assert.Equal(0, call.ConversationTokens);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Record_RejectsNegativeTokenCounters(int fieldIndex)
    {
        long? responseOutput = fieldIndex == 2 ? -1 : 420;
        long? thinkingOutput = fieldIndex == 3 ? -1 : 80;
        long? cacheRead = fieldIndex == 4 ? -1 : null;

        Assert.ThrowsAny<ArgumentException>(() => Call(
            inputTokens: fieldIndex == 0 ? -1 : 100,
            outputTokens: fieldIndex == 1 ? -1 : 500,
            responseOutputTokens: responseOutput,
            thinkingOutputTokens: thinkingOutput,
            cacheReadTokens: cacheRead));
    }

    #endregion

    #region C. Accounting formula

    [Fact]
    public void ConversationTokens_IsInputPlusOutput_AndNeverIncludesCache()
    {
        // Live-validated shape: cache reads are additive to uncached input, so including them
        // in the headline counter would overstate conversation tokens.
        var call = Call(inputTokens: 3372, outputTokens: 500, cacheReadTokens: 16307);

        Assert.Equal(3872, call.ConversationTokens);
        Assert.NotEqual(3372 + 500 + 16307, call.ConversationTokens);
        Assert.Equal(3372 + 16307, call.ProcessedInputTokens);
    }

    [Fact]
    public void ProcessedInputTokens_IsUnknown_WhenCacheReadsWereNotReported()
    {
        var call = Call(inputTokens: 3372, outputTokens: 500, cacheReadTokens: null);
        Assert.Null(call.ProcessedInputTokens);
    }

    #endregion

    #region D. Output composition

    [Fact]
    public void OutputComponentMismatch_IsDiagnosable_AndNeverRepaired()
    {
        // Live validation observed output = response + thinking. The domain keeps the
        // provider's authoritative counter even when future schema variation breaks the
        // decomposition, and exposes the mismatch instead of silently repairing it.
        var mismatched = Call(outputTokens: 500, responseOutputTokens: 420, thinkingOutputTokens: 80);
        Assert.False(mismatched.HasOutputComponentMismatch);
        Assert.Equal(500, mismatched.OutputComponentSum);

        var varied = Call(outputTokens: 500, responseOutputTokens: 400, thinkingOutputTokens: 80);
        Assert.True(varied.HasOutputComponentMismatch);
        Assert.Equal(500, varied.OutputTokens);
        Assert.Equal(480, varied.OutputComponentSum);
        Assert.Equal(500, varied.ConversationTokens - varied.InputTokens);
    }

    [Fact]
    public void OutputComponentSum_IsUnknown_WhenOnlyOneComponentIsReported()
    {
        var onlyResponse = Call(outputTokens: 500, responseOutputTokens: 500, thinkingOutputTokens: null);
        var onlyThinking = Call(outputTokens: 500, responseOutputTokens: null, thinkingOutputTokens: 500);
        var neither = Call(outputTokens: 500, responseOutputTokens: null, thinkingOutputTokens: null);

        Assert.Null(onlyResponse.OutputComponentSum);
        Assert.Null(onlyThinking.OutputComponentSum);
        Assert.Null(neither.OutputComponentSum);
        Assert.False(onlyResponse.HasOutputComponentMismatch);
        Assert.False(neither.HasOutputComponentMismatch);
    }

    #endregion

    #region E. Account attribution

    [Fact]
    public void VerifiedObservation_RequiresAccountId()
    {
        Assert.Throws<ArgumentException>(() => Call(
            accountAttributionBasis: UsageAccountAttributionBasis.VerifiedObservation, accountId: null));
        Assert.Throws<ArgumentException>(() => Call(
            accountAttributionBasis: UsageAccountAttributionBasis.VerifiedObservation, accountId: "   "));

        var verified = Call(accountAttributionBasis: UsageAccountAttributionBasis.VerifiedObservation, accountId: " acc_synthetic01 ");
        Assert.Equal("acc_synthetic01", verified.AccountId);
    }

    [Fact]
    public void Unattributed_ForbidsAccountId_AndNeverInfersOne()
    {
        Assert.Throws<ArgumentException>(() => Call(
            accountAttributionBasis: UsageAccountAttributionBasis.Unattributed, accountId: "acc_synthetic01"));

        var unattributed = Call(
            accountAttributionBasis: UsageAccountAttributionBasis.Unattributed,
            accountId: null);
        Assert.Null(unattributed.AccountId);
        Assert.Equal(UsageAccountAttributionBasis.Unattributed, unattributed.AccountAttributionBasis);
    }

    #endregion

    #region F. Time attribution

    [Fact]
    public void TimeAttribution_IsExplicit_AndDistinguishable()
    {
        var observationTime = Call(timeAttribution: UsageTimeAttribution.ObservationTime);
        var historical = Call(timeAttribution: UsageTimeAttribution.HistoricalUnknown);

        Assert.NotEqual(observationTime.TimeAttribution, historical.TimeAttribution);
        // Both records only carry first-observation metadata; neither claims an event time.
        Assert.Equal(Observed, observationTime.FirstObservedAtUtc);
        Assert.Equal(Observed, historical.FirstObservedAtUtc);
    }

    [Fact]
    public void FirstObservedAtUtc_MustBeUtc()
    {
        Assert.Throws<ArgumentException>(() => Call(
            firstObservedAtUtc: new DateTimeOffset(2026, 10, 4, 14, 0, 0, TimeSpan.FromHours(2))));
    }

    #endregion

    #region Conflict comparison + integrity

    [Fact]
    public void AccountingEquals_IgnoresObservationTime_ButNoAccountingField()
    {
        var original = Call();
        var reObservedLater = Call(firstObservedAtUtc: Observed.AddMinutes(5));
        var differentInput = Call(inputTokens: 999);
        var differentBasis = Call(accountAttributionBasis: UsageAccountAttributionBasis.Unattributed, accountId: null);
        var differentModel = Call(responseModel: "claude-opus-4-6");

        Assert.True(original.AccountingEquals(reObservedLater));
        Assert.False(original.AccountingEquals(differentInput));
        Assert.False(original.AccountingEquals(differentBasis));
        Assert.False(original.AccountingEquals(differentModel));
        Assert.Throws<ArgumentNullException>(() => original.AccountingEquals(null!));
    }

    [Theory]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("abc")]
    public void Record_RejectsInvalidCallKeys(string invalidKey)
    {
        Assert.Throws<ArgumentException>(() => new UsageCallRecord(
            invalidKey, null, null, 1, 1, null, null, null,
            Observed, UsageTimeAttribution.ObservationTime,
            UsageAccountAttributionBasis.Unattributed, null, "SyntheticTest"));
    }

    [Fact]
    public void Record_RejectsInvalidSource_AndOversizedText()
    {
        Assert.Throws<ArgumentException>(() => Call(source: "   "));
        Assert.Throws<ArgumentException>(() => Call(source: new string('s', 129)));
        Assert.Throws<ArgumentException>(() => Call(provider: new string('p', 65)));
        Assert.Throws<ArgumentException>(() => Call(responseModel: new string('m', 129)));
        Assert.Throws<ArgumentException>(() => Call(source: "bad\u0003control"));
    }

    #endregion
}
