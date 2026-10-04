using AG2Router.Core.Contracts;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Usage;

/// <summary>
/// Aggregation over the durable usage ledger. One scan per request produces lifetime totals
/// split by time-attribution basis, the observation-time trend, and the per-model breakdown.
/// "All Accounts" includes unattributed calls; per-account scopes include only calls whose
/// verified attribution matches the internal account id, so the unattributed remainder is
/// always explicit and never hidden. Sums use checked arithmetic: an aggregate that would
/// overflow Int64 fails loudly instead of silently wrapping.
/// </summary>
public sealed class UsageAggregationService
{
    public static readonly IReadOnlyList<string> SupportedRanges = ["24h", "7d", "30d", "all"];

    private readonly IUsageCallLedger _ledger;
    private readonly TimeProvider _timeProvider;

    public UsageAggregationService(IUsageCallLedger ledger, TimeProvider? timeProvider = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<UsageSummaryResult> GetSummaryAsync(UsageScope scope, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        var generatedAtUtc = _timeProvider.GetUtcNow();
        var all = await _ledger.GetAllCallsAsync(cancellationToken).ConfigureAwait(false);

        var totals = Aggregate(Filter(all, scope));
        var historicalUnknown = Aggregate(Filter(all, scope).Where(static call => call.TimeAttribution == UsageTimeAttribution.HistoricalUnknown));
        var unattributed = scope.Kind == UsageScopeKind.All
            ? Aggregate(all.Where(static call => call.AccountAttributionBasis == UsageAccountAttributionBasis.Unattributed))
            : null;

        var accounts = new List<UsageAccountBreakdownItem>();
        if (scope.Kind == UsageScopeKind.All)
        {
            var byAccount = new Dictionary<string, UsageAccountBreakdownItem>(StringComparer.Ordinal);
            foreach (var call in all)
            {
                bool isUnattributed = call.AccountAttributionBasis == UsageAccountAttributionBasis.Unattributed;
                string key = isUnattributed ? "__unattributed__" : call.AccountId ?? "__unattributed__";
                if (!byAccount.TryGetValue(key, out var row))
                {
                    row = new UsageAccountBreakdownItem
                    {
                        AccountId = isUnattributed ? null : call.AccountId,
                        IsUnattributed = isUnattributed,
                    };
                    byAccount[key] = row;
                }
                checked
                {
                    byAccount[key] = row with
                    {
                        Calls = row.Calls + 1,
                        ConversationTokens = row.ConversationTokens + call.ConversationTokens,
                    };
                }
            }
            accounts = byAccount.Values
                .OrderByDescending(static row => row.ConversationTokens)
                .ThenBy(static row => row.AccountId, StringComparer.Ordinal)
                .ToList();
        }

        return new UsageSummaryResult
        {
            Scope = scope,
            Totals = totals,
            HistoricalUnknown = historicalUnknown,
            Unattributed = unattributed,
            Accounts = accounts,
            GeneratedAtUtc = generatedAtUtc,
        };
    }

    public async Task<UsageTimeSeriesResult> GetTimeSeriesAsync(UsageScope scope, string range, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        var (window, bucketSize) = ParseRange(range);
        var generatedAtUtc = _timeProvider.GetUtcNow();
        var nowUtc = generatedAtUtc;

        var calls = Filter(await _ledger.GetAllCallsAsync(cancellationToken).ConfigureAwait(false), scope);
        var historicalUnknown = Aggregate(calls.Where(static call => call.TimeAttribution == UsageTimeAttribution.HistoricalUnknown));

        var observationTimeCalls = calls
            .Where(static call => call.TimeAttribution == UsageTimeAttribution.ObservationTime)
            .Where(call => window is null || call.FirstObservedAtUtc >= nowUtc - window.Value)
            .ToList();

        var buckets = new Dictionary<DateTimeOffset, UsageTimeBucket>();
        foreach (var call in observationTimeCalls)
        {
            var bucketStart = BucketStart(call.FirstObservedAtUtc, bucketSize);
            if (!buckets.TryGetValue(bucketStart, out var bucket))
            {
                bucket = new UsageTimeBucket { BucketStartUtc = bucketStart };
                buckets[bucketStart] = bucket;
            }
            checked
            {
                buckets[bucketStart] = bucket with
                {
                    Calls = bucket.Calls + 1,
                    ConversationTokens = bucket.ConversationTokens + call.ConversationTokens,
                    InputTokens = bucket.InputTokens + call.InputTokens,
                    OutputTokens = bucket.OutputTokens + call.OutputTokens,
                    CacheReadTokens = bucket.CacheReadTokens + (call.CacheReadTokens ?? 0),
                };
            }
        }

        return new UsageTimeSeriesResult
        {
            Range = range,
            Buckets = buckets.Values.OrderBy(static bucket => bucket.BucketStartUtc).ToList(),
            HistoricalUnknown = historicalUnknown,
            GeneratedAtUtc = generatedAtUtc,
        };
    }

    public async Task<UsageModelBreakdownResult> GetModelBreakdownAsync(UsageScope scope, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        var generatedAtUtc = _timeProvider.GetUtcNow();
        var calls = Filter(await _ledger.GetAllCallsAsync(cancellationToken).ConfigureAwait(false), scope);

        var byModel = new Dictionary<string, UsageModelBreakdownItem>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            string key = call.ResponseModelKey ?? "__unknown__";
            if (!byModel.TryGetValue(key, out var item))
            {
                item = new UsageModelBreakdownItem
                {
                    ModelKey = call.ResponseModelKey,
                    IsUnknownModel = call.ResponseModelKey is null,
                };
                byModel[key] = item;
            }
            checked
            {
                byModel[key] = item with
                {
                    Calls = item.Calls + 1,
                    ConversationTokens = item.ConversationTokens + call.ConversationTokens,
                    InputTokens = item.InputTokens + call.InputTokens,
                    OutputTokens = item.OutputTokens + call.OutputTokens,
                    ThinkingOutputTokens = item.ThinkingOutputTokens + (call.ThinkingOutputTokens ?? 0),
                    CacheReadTokens = item.CacheReadTokens + (call.CacheReadTokens ?? 0),
                };
            }
        }

        return new UsageModelBreakdownResult
        {
            Scope = scope,
            Models = byModel.Values
                .OrderByDescending(static item => item.ConversationTokens)
                .ThenBy(static item => item.ModelKey, StringComparer.Ordinal)
                .ToList(),
            GeneratedAtUtc = generatedAtUtc,
        };
    }

    private static IEnumerable<UsageCallRecord> Filter(IEnumerable<UsageCallRecord> calls, UsageScope scope) =>
        scope.Kind switch
        {
            UsageScopeKind.All => calls,
            UsageScopeKind.Unattributed => calls.Where(static call =>
                call.AccountAttributionBasis == UsageAccountAttributionBasis.Unattributed),
            UsageScopeKind.Account => calls.Where(call =>
                call.AccountAttributionBasis == UsageAccountAttributionBasis.VerifiedObservation &&
                string.Equals(call.AccountId, scope.AccountId, StringComparison.Ordinal)),
            _ => throw new InvalidOperationException($"Unsupported usage scope '{scope.Kind}'."),
        };

    private static UsageTokenTotals Aggregate(IEnumerable<UsageCallRecord> calls)
    {
        long calls_ = 0, conversation = 0, input = 0, output = 0;
        long responseOutput = 0, responseOutputCalls = 0, thinkingOutput = 0, thinkingOutputCalls = 0;
        long cacheRead = 0, cacheReported = 0, cacheUnknown = 0, mismatch = 0;

        foreach (var call in calls)
        {
            checked
            {
                calls_++;
                conversation += call.ConversationTokens;
                input += call.InputTokens;
                output += call.OutputTokens;
                if (call.ResponseOutputTokens.HasValue)
                {
                    responseOutput += call.ResponseOutputTokens.Value;
                    responseOutputCalls++;
                }
                if (call.ThinkingOutputTokens.HasValue)
                {
                    thinkingOutput += call.ThinkingOutputTokens.Value;
                    thinkingOutputCalls++;
                }
                if (call.CacheReadTokens.HasValue)
                {
                    cacheRead += call.CacheReadTokens.Value;
                    cacheReported++;
                }
                else
                {
                    cacheUnknown++;
                }
                if (call.HasOutputComponentMismatch)
                    mismatch++;
            }
        }

        return new UsageTokenTotals
        {
            Calls = calls_,
            ConversationTokens = conversation,
            InputTokens = input,
            OutputTokens = output,
            ResponseOutputTokens = responseOutput,
            ResponseOutputCalls = responseOutputCalls,
            ThinkingOutputTokens = thinkingOutput,
            ThinkingOutputCalls = thinkingOutputCalls,
            CacheReadTokens = cacheRead,
            CacheReportedCalls = cacheReported,
            CacheUnknownCalls = cacheUnknown,
            OutputMismatchCalls = mismatch,
        };
    }

    private static void ValidateScope(UsageScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.IsValid)
            throw new ArgumentException("The usage scope is invalid.", nameof(scope));
    }

    private static (TimeSpan? Window, TimeSpan BucketSize) ParseRange(string range) => range switch
    {
        "24h" => (TimeSpan.FromHours(24), TimeSpan.FromHours(1)),
        "7d" => (TimeSpan.FromDays(7), TimeSpan.FromDays(1)),
        "30d" => (TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
        "all" => (null, TimeSpan.FromDays(30)),
        _ => throw new ArgumentException($"Unsupported usage time range '{range}'.", nameof(range)),
    };

    private static DateTimeOffset BucketStart(DateTimeOffset timestamp, TimeSpan bucketSize)
    {
        var utc = timestamp.UtcDateTime;
        if (bucketSize == TimeSpan.FromHours(1))
            return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
        if (bucketSize == TimeSpan.FromDays(1))
            return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        // Monthly buckets for the "all" range.
        return new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
