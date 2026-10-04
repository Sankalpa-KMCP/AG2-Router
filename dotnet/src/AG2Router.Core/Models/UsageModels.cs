using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AG2Router.Core.Models;

/// <summary>
/// How a durable usage call record is attributed to a managed account. Attribution is never
/// inferred: it is either backed by explicit AG2 Router evidence or the record stays unattributed.
/// </summary>
public enum UsageAccountAttributionBasis
{
    /// <summary>
    /// The call was attributed to the referenced managed account through explicit evidence
    /// captured at observation time. Requires a non-empty <see cref="UsageCallRecord.AccountId"/>.
    /// </summary>
    VerifiedObservation,

    /// <summary>
    /// No defensible account attribution exists for the call. The account id must be absent.
    /// Unattributed calls still count toward All Accounts totals and remain visible as an
    /// explicit remainder; per-account totals may therefore sum to less than the global total.
    /// </summary>
    Unattributed
}

/// <summary>
/// How a usage call record's timestamp relates to the actual model call. The usage source
/// provides no per-call provider event timestamp, so execution time is never claimed.
/// </summary>
public enum UsageTimeAttribution
{
    /// <summary>
    /// The call was first observed while continuous forward collection was running, so
    /// <see cref="UsageCallRecord.FirstObservedAtUtc"/> may be bucketed into trends later,
    /// with poll-resolution semantics acknowledged by the consumer.
    /// </summary>
    ObservationTime,

    /// <summary>
    /// The call was discovered during historical/backfill/recovery enumeration. It contributes
    /// to totals only; <see cref="UsageCallRecord.FirstObservedAtUtc"/> is when the ledger saw
    /// the call, not when it executed, and must never be presented as an event timestamp.
    /// </summary>
    HistoricalUnknown
}

/// <summary>
/// One durably observed conversation-model call. Represents exactly one validated
/// conversation-call accounting unit; it does not imply coverage of all Antigravity usage.
/// Persists only numeric accounting metadata and minimum opaque identifiers: no prompt,
/// response, or other content, and no raw conversation/call identifiers.
/// </summary>
public record UsageCallRecord
{
    [JsonPropertyName("callKey")]
    public string CallKey { get; init; }

    [JsonPropertyName("responseModelKey")]
    public string? ResponseModelKey { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("responseOutputTokens")]
    public long? ResponseOutputTokens { get; init; }

    [JsonPropertyName("thinkingOutputTokens")]
    public long? ThinkingOutputTokens { get; init; }

    [JsonPropertyName("cacheReadTokens")]
    public long? CacheReadTokens { get; init; }

    [JsonPropertyName("firstObservedAtUtc")]
    public DateTimeOffset FirstObservedAtUtc { get; init; }

    [JsonPropertyName("timeAttribution")]
    public UsageTimeAttribution TimeAttribution { get; init; }

    [JsonPropertyName("accountAttributionBasis")]
    public UsageAccountAttributionBasis AccountAttributionBasis { get; init; }

    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; }

    [JsonConstructor]
    public UsageCallRecord(
        string callKey,
        string? responseModelKey,
        string? provider,
        long inputTokens,
        long outputTokens,
        long? responseOutputTokens,
        long? thinkingOutputTokens,
        long? cacheReadTokens,
        DateTimeOffset firstObservedAtUtc,
        UsageTimeAttribution timeAttribution,
        UsageAccountAttributionBasis accountAttributionBasis,
        string? accountId,
        string source)
    {
        ValidateCallKey(callKey);

        if (inputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(inputTokens), inputTokens, "InputTokens cannot be negative.");
        if (outputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(outputTokens), outputTokens, "OutputTokens cannot be negative.");
        ValidateOptionalTokens(responseOutputTokens, nameof(responseOutputTokens));
        ValidateOptionalTokens(thinkingOutputTokens, nameof(thinkingOutputTokens));
        ValidateOptionalTokens(cacheReadTokens, nameof(cacheReadTokens));

        if (firstObservedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException(
                "FirstObservedAtUtc must be expressed in UTC (zero offset); observation times are always persisted in UTC.",
                nameof(firstObservedAtUtc));
        if (!Enum.IsDefined(timeAttribution))
            throw new ArgumentException($"TimeAttribution '{timeAttribution}' is not a defined value.", nameof(timeAttribution));
        if (!Enum.IsDefined(accountAttributionBasis))
            throw new ArgumentException(
                $"AccountAttributionBasis '{accountAttributionBasis}' is not a defined value.",
                nameof(accountAttributionBasis));

        string? normalizedAccountId;
        if (accountAttributionBasis == UsageAccountAttributionBasis.VerifiedObservation)
        {
            if (string.IsNullOrWhiteSpace(accountId))
                throw new ArgumentException(
                    "VerifiedObservation attribution requires a managed account id.", nameof(accountId));
            normalizedAccountId = accountId.Trim();
        }
        else
        {
            if (accountId is not null)
                throw new ArgumentException(
                    "Unattributed calls must not carry an account id; attribution is never inferred.",
                    nameof(accountId));
            normalizedAccountId = null;
        }

        CallKey = callKey;
        ResponseModelKey = NormalizeOptionalText(responseModelKey, maxLength: 128, paramName: nameof(responseModelKey));
        Provider = NormalizeOptionalText(provider, maxLength: 64, paramName: nameof(provider));
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        ResponseOutputTokens = responseOutputTokens;
        ThinkingOutputTokens = thinkingOutputTokens;
        CacheReadTokens = cacheReadTokens;
        FirstObservedAtUtc = firstObservedAtUtc;
        TimeAttribution = timeAttribution;
        AccountAttributionBasis = accountAttributionBasis;
        AccountId = normalizedAccountId;
        Source = NormalizeRequiredText(source, maxLength: 128, paramName: nameof(source));
    }

    /// <summary>
    /// The headline conversation-token counter for this call: input + output. Cache reads are
    /// additive to uncached input in the provider's own accounting and are deliberately NOT
    /// included here; they are a separate counter (<see cref="CacheReadTokens"/>) so the
    /// headline metric never silently absorbs cached traffic.
    /// </summary>
    public long ConversationTokens => InputTokens + OutputTokens;

    /// <summary>
    /// Uncached input plus cache-read input, when cache reads were reported. Null when the
    /// provider did not report cache reads, because absence is not proven zero.
    /// </summary>
    public long? ProcessedInputTokens =>
        CacheReadTokens.HasValue ? InputTokens + CacheReadTokens.Value : null;

    /// <summary>
    /// Sum of the reported output components. Meaningful only when both components are present;
    /// a single present component is not comparable against <see cref="OutputTokens"/>.
    /// </summary>
    public long? OutputComponentSum =>
        ResponseOutputTokens.HasValue && ThinkingOutputTokens.HasValue
            ? ResponseOutputTokens.Value + ThinkingOutputTokens.Value
            : null;

    /// <summary>
    /// True when both output components were reported and their sum differs from the provider's
    /// authoritative <see cref="OutputTokens"/>. The mismatch is preserved and diagnosable, never
    /// silently repaired: aggregation always uses <see cref="OutputTokens"/> as the headline.
    /// </summary>
    public bool HasOutputComponentMismatch =>
        ResponseOutputTokens.HasValue && ThinkingOutputTokens.HasValue &&
        ResponseOutputTokens.Value + ThinkingOutputTokens.Value != OutputTokens;

    /// <summary>
    /// Compares the immutable accounting payload of two records sharing one call key. Provider
    /// counters, model, provider label, source, and both attribution decisions are immutable;
    /// <see cref="FirstObservedAtUtc"/> is observation metadata and is deliberately excluded so
    /// re-observation stays idempotent instead of manufacturing conflicts.
    /// </summary>
    public bool AccountingEquals(UsageCallRecord other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ResponseModelKey == other.ResponseModelKey &&
            Provider == other.Provider &&
            InputTokens == other.InputTokens &&
            OutputTokens == other.OutputTokens &&
            ResponseOutputTokens == other.ResponseOutputTokens &&
            ThinkingOutputTokens == other.ThinkingOutputTokens &&
            CacheReadTokens == other.CacheReadTokens &&
            TimeAttribution == other.TimeAttribution &&
            AccountAttributionBasis == other.AccountAttributionBasis &&
            AccountId == other.AccountId &&
            Source == other.Source;
    }

    private static void ValidateCallKey(string value)
    {
        if (value.Length != 64)
            throw new ArgumentException("CallKey must be exactly 64 lowercase hexadecimal characters.", "callKey");
        foreach (char c in value)
        {
            bool isLowercaseHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isLowercaseHex)
                throw new ArgumentException("CallKey must be exactly 64 lowercase hexadecimal characters.", "callKey");
        }
    }

    private static void ValidateOptionalTokens(long? value, string paramName)
    {
        if (value.HasValue && value.Value < 0)
            throw new ArgumentOutOfRangeException(paramName, value.Value, "Token counters cannot be negative.");
    }

    private static string? NormalizeOptionalText(string? value, int maxLength, string paramName)
    {
        return value is null ? null : NormalizeRequiredText(value, maxLength, paramName);
    }

    private static string NormalizeRequiredText(string value, int maxLength, string paramName)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException($"'{paramName}' cannot be empty or whitespace.", paramName);
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"'{paramName}' cannot exceed {maxLength} characters.", paramName);
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException($"'{paramName}' must not contain control characters.", paramName);
        return trimmed;
    }
}

/// <summary>
/// Deterministic, privacy-minimizing identity for a conversation-model call. The durable
/// ledger keys calls by SHA-256 over a domain-separated, length-prefixed encoding of
/// (cascadeId, responseId), so repeated observation of one call yields one stable key,
/// different tuples can never collide through concatenation ambiguity, and the raw
/// conversation/call identifiers never need to be persisted.
/// </summary>
public static class UsageCallKeys
{
    private const int MaxIdentifierLength = 4096;
    private static readonly byte[] DomainTag = "AG2U1K1"u8.ToArray();

    /// <summary>
    /// Computes the durable call key for one logical model call. Inputs are the exact raw
    /// provider identifiers; they are not trimmed, so distinct raw tuples never alias.
    /// </summary>
    public static string Compute(string cascadeId, string responseId)
    {
        ArgumentNullException.ThrowIfNull(cascadeId);
        ArgumentNullException.ThrowIfNull(responseId);
        if (cascadeId.Length == 0 || string.IsNullOrWhiteSpace(cascadeId))
            throw new ArgumentException("cascadeId cannot be empty or whitespace.", nameof(cascadeId));
        if (responseId.Length == 0 || string.IsNullOrWhiteSpace(responseId))
            throw new ArgumentException("responseId cannot be empty or whitespace.", nameof(responseId));
        if (cascadeId.Length > MaxIdentifierLength)
            throw new ArgumentException($"cascadeId cannot exceed {MaxIdentifierLength} characters.", nameof(cascadeId));
        if (responseId.Length > MaxIdentifierLength)
            throw new ArgumentException($"responseId cannot exceed {MaxIdentifierLength} characters.", nameof(responseId));

        byte[] cascadeBytes = Encoding.UTF8.GetBytes(cascadeId);
        byte[] responseBytes = Encoding.UTF8.GetBytes(responseId);

        // 4-byte little-endian length prefixes make the concatenation unambiguous: the byte
        // lengths of both identifiers are recoverable from the hashed input, so ("ab","c") and
        // ("a","bc") hash differently by construction.
        byte[] buffer = new byte[DomainTag.Length + 4 + cascadeBytes.Length + 4 + responseBytes.Length];
        int offset = 0;
        DomainTag.CopyTo(buffer, offset);
        offset += DomainTag.Length;
        WriteLengthPrefix(buffer, ref offset, cascadeBytes.Length);
        cascadeBytes.CopyTo(buffer, offset);
        offset += cascadeBytes.Length;
        WriteLengthPrefix(buffer, ref offset, responseBytes.Length);
        responseBytes.CopyTo(buffer, offset);

        byte[] digest = SHA256.HashData(buffer);
        CryptographicOperations.ZeroMemory(buffer);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// Canonicalizes a provider-reported response model identifier for durable storage and
    /// later model-breakdown grouping: trimmed, invariant lowercase; null when absent, so
    /// "unknown model" is always representable and never conflated with a real key.
    /// </summary>
    public static string? CanonicalizeResponseModel(string? rawResponseModel) =>
        string.IsNullOrWhiteSpace(rawResponseModel) ? null : rawResponseModel.Trim().ToLowerInvariant();

    private static void WriteLengthPrefix(byte[] buffer, ref int offset, int length)
    {
        buffer[offset++] = (byte)(length & 0xFF);
        buffer[offset++] = (byte)((length >> 8) & 0xFF);
        buffer[offset++] = (byte)((length >> 16) & 0xFF);
        buffer[offset++] = (byte)((length >> 24) & 0xFF);
    }
}
