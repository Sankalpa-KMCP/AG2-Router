using AG2Router.Core.Models;

namespace AG2Router.AG2.Routing;

public static class CandidateSelector
{
    public static SelectionResult SelectBestCandidate(
        string? currentAccountId,
        double? currentQuotaFraction,
        IReadOnlyList<AccountMetadata> accounts,
        IReadOnlyDictionary<string, double> accountQuotas,
        RouterConfigDto config,
        ISet<string>? vaultedAccountIds = null,
        ISet<string>? cooldownAccountIds = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(accountQuotas);
        ArgumentNullException.ThrowIfNull(config);

        double lowThresholdFraction = config.LowQuotaThresholdPercent / 100.0;
        double minCandidateFraction = config.MinimumCandidateQuotaPercent / 100.0;

        // 1. Check if current account quota is healthy
        if (currentAccountId != null && currentQuotaFraction.HasValue)
        {
            if (currentQuotaFraction.Value > lowThresholdFraction)
            {
                int pct = (int)Math.Round(currentQuotaFraction.Value * 100.0);
                return new SelectionResult(
                    ShouldSwitch: false,
                    Reason: $"Current account quota is healthy ({pct}% > {config.LowQuotaThresholdPercent}%).",
                    CurrentAccountId: currentAccountId,
                    CurrentQuotaFraction: currentQuotaFraction,
                    BestCandidate: null,
                    Candidates: Array.Empty<CandidateEvaluation>()
                );
            }
        }

        // 2. Evaluate each registered account
        var evaluations = new List<CandidateEvaluation>();

        foreach (var account in accounts)
        {
            if (account.Id == currentAccountId)
            {
                continue;
            }

            double remainingFraction = accountQuotas.TryGetValue(account.Id, out double q) ? q : 0.0;
            int quotaPercent = (int)Math.Round(remainingFraction * 100.0);

            bool isEligible = true;
            string? ineligibilityReason = null;

            if (string.Equals(account.ValidationStatus, "EXPIRED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(account.ValidationStatus, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                isEligible = false;
                ineligibilityReason = $"Account validation status is {account.ValidationStatus}";
            }
            else if (remainingFraction < minCandidateFraction)
            {
                isEligible = false;
                ineligibilityReason = $"Quota ({quotaPercent}%) is below minimum candidate threshold ({config.MinimumCandidateQuotaPercent}%)";
            }
            else if ((vaultedAccountIds != null && !vaultedAccountIds.Contains(account.Id)) || !account.HasVaultedSession)
            {
                isEligible = false;
                ineligibilityReason = "Account does not have a vaulted session";
            }
            else if (cooldownAccountIds != null && cooldownAccountIds.Contains(account.Id))
            {
                isEligible = false;
                ineligibilityReason = "Account is in cooldown following a recent switch or failure";
            }

            // Score calculation:
            // Base: 0 to 100 based on remaining quota
            // Standard account bonus: +50
            // Priority bonus: up to 10 points based on priority (lower priority integer = higher preference)
            double score = remainingFraction * 100.0;
            if (!account.IsReserve)
            {
                score += 50.0;
            }
            score += Math.Max(0, 10 - account.Priority);
            score = Math.Round(score, 2);

            evaluations.Add(new CandidateEvaluation(
                Account: account,
                RemainingFraction: remainingFraction,
                QuotaPercent: quotaPercent,
                IsEligible: isEligible,
                IneligibilityReason: ineligibilityReason,
                Score: score
            ));
        }

        // 3. Filter eligible candidates
        var eligible = evaluations.Where(e => e.IsEligible).ToList();

        if (eligible.Count == 0)
        {
            string noCandidateReason = evaluations.Count == 0
                ? "No secondary accounts are registered in the store."
                : $"No candidate accounts meet the minimum quota threshold ({config.MinimumCandidateQuotaPercent}%).";

            return new SelectionResult(
                ShouldSwitch: false,
                Reason: noCandidateReason,
                CurrentAccountId: currentAccountId,
                CurrentQuotaFraction: currentQuotaFraction,
                BestCandidate: null,
                Candidates: evaluations
            );
        }

        // 4. Deterministic sorting:
        // Non-reserve first -> Higher usable quota -> Lower priority integer -> Alphabetical ID
        eligible.Sort((a, b) =>
        {
            // 1. Non-reserve vs Reserve
            if (a.Account.IsReserve != b.Account.IsReserve)
            {
                return a.Account.IsReserve ? 1 : -1;
            }

            // 2. Usable Quota (highest fraction first)
            double quotaDiff = b.RemainingFraction - a.RemainingFraction;
            if (Math.Abs(quotaDiff) > 0.001)
            {
                return quotaDiff > 0 ? 1 : -1;
            }

            // 3. Priority (lower integer is better: 1 before 2)
            int priorityDiff = a.Account.Priority.CompareTo(b.Account.Priority);
            if (priorityDiff != 0)
            {
                return priorityDiff;
            }

            // 4. Deterministic ID tie-breaker
            return string.Compare(a.Account.Id, b.Account.Id, StringComparison.Ordinal);
        });

        var best = eligible[0];
        string currentPctText = currentQuotaFraction.HasValue
            ? $"{Math.Round(currentQuotaFraction.Value * 100.0)}%"
            : "unknown";

        return new SelectionResult(
            ShouldSwitch: true,
            Reason: $"Low quota on current account ({currentPctText} <= {config.LowQuotaThresholdPercent}%). Optimal candidate selected: {best.Account.Email} ({best.QuotaPercent}%).",
            CurrentAccountId: currentAccountId,
            CurrentQuotaFraction: currentQuotaFraction,
            BestCandidate: best,
            Candidates: evaluations
        );
    }
}
