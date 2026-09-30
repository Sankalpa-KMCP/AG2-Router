using AG2Router.Core.Models;

namespace AG2Router.AG2.Routing;

/// <summary>Weakest-link evidence, not an aggregate/shared quota pool.</summary>
internal static class QuotaObservationEvidence
{
    public static IReadOnlyList<AccountModelQuotaObservation> Consolidate(IEnumerable<AccountModelQuotaObservation> rows) =>
        rows.GroupBy(o => (o.AccountId, o.ModelKey))
            .Select(group =>
            {
                var items = group.ToArray();
                bool coherentTime = items.Select(o => o.ObservedAtUtc).Distinct().Count() == 1;
                // Differently timed duplicate pools are not one coherent snapshot.
                // Supersede at the newest time, but retain unknown eligibility.
                double? fraction = !coherentTime || items.Any(o => o.RemainingFraction == null)
                    ? null : items.Min(o => o.RemainingFraction);
                var resets = items.Select(o => o.ResetTime).Distinct(StringComparer.Ordinal).ToArray();
                return new AccountModelQuotaObservation(group.Key.AccountId, group.Key.ModelKey,
                    fraction, resets.Length == 1 ? resets[0] : null,
                    items.Max(o => o.ObservedAtUtc),
                    string.Join("+", items.Select(o => o.Source).Distinct().Order(StringComparer.Ordinal)));
            }).ToArray();

    public static bool IsUsable(AccountModelQuotaObservation evidence, DateTimeOffset now, double minimumPercent) =>
        evidence.ObservedAtUtc <= now && now - evidence.ObservedAtUtc < CandidateSelector.InactiveCandidateEvidenceLifetime &&
        evidence.RemainingFraction is double fraction && double.IsFinite(fraction) &&
        fraction > 0 && fraction <= 1 && fraction >= minimumPercent / 100.0;

    public static IReadOnlyList<AccountModelQuotaObservation> Capture(string accountId,
        IEnumerable<ModelQuotaDto> models, DateTimeOffset observedAt, string source) =>
        Consolidate(models.Where(m => m != null && CandidateSelector.GetModelKey(m) != null)
            .Select(m => new AccountModelQuotaObservation(accountId, CandidateSelector.GetModelKey(m)!,
                m.IsExhausted ? 0 : m.RemainingFraction is double fraction && double.IsFinite(fraction) && fraction >= 0 && fraction <= 1
                    ? fraction : null, m.ResetTime, observedAt, source)));
}
