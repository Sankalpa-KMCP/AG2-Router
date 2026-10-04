using System.Globalization;
using System.Linq;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Routing;

/// <summary>
/// Pure candidate-selection policy for automatic routing. Selection here is advisory:
/// the chosen plan is later revalidated against the same durable evidence, current
/// configuration, identity, activity, and process provenance by the switch coordinator
/// before any mutation. Unknown quota never fabricates eligibility — a candidate without
/// observed capacity for the requested model stays ineligible.
/// </summary>
public static class CandidateSelector
{
    // Application-side freshness bound for inactive candidates' durable observations.
    // This is an AG2 Router policy, not an upstream guarantee: no external contract
    // promises quota validity for any particular duration.
    public static readonly TimeSpan InactiveCandidateEvidenceLifetime = TimeSpan.FromHours(2);

    public static string? CanonicalizeModelKey(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim().ToLowerInvariant()
            : null;

    public static string? GetModelKey(ModelQuotaDto m) => CanonicalizeModelKey(m.ModelOrTier);

    public static SelectionResult SelectBestCandidate(
        string? currentAccountId,
        double? currentQuotaFraction,
        IReadOnlyList<AccountMetadata> accounts,
        IReadOnlyDictionary<string, double> accountQuotas,
        RouterConfigDto config,
        ISet<string>? vaultedAccountIds = null,
        ISet<string>? cooldownAccountIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<ModelQuotaDto>>? accountModelQuotas = null,
        IReadOnlyCollection<string>? relevantModelKeys = null,
        IReadOnlyDictionary<string, AccountModelQuotaObservation>? candidateModelObservations = null,
        DateTimeOffset? evaluationTimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(accountQuotas);
        ArgumentNullException.ThrowIfNull(config);

        double lowThresholdFraction = config.LowQuotaThresholdPercent / 100.0;
        double minCandidateFraction = config.MinimumCandidateQuotaPercent / 100.0;
        bool requiresModelEvidence = relevantModelKeys != null && relevantModelKeys.Count > 0;

        if (currentAccountId == null ||
            !currentQuotaFraction.HasValue || !double.IsFinite(currentQuotaFraction.Value))
        {
            return new SelectionResult(false, "Current account quota is unknown; automatic switching requires observed low quota.",
                currentAccountId, null, null, Array.Empty<CandidateEvaluation>());
        }

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

            double remainingFraction = 0.0;
            int quotaPercent = 0;
            bool isEligible = true;
            string? ineligibilityReason = null;

            if (candidateModelObservations != null && requiresModelEvidence)
            {
                if (string.Equals(account.ValidationStatus, "EXPIRED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(account.ValidationStatus, "FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    isEligible = false;
                    ineligibilityReason = $"Account validation status is {account.ValidationStatus}";
                }
                else if (vaultedAccountIds != null ? !vaultedAccountIds.Contains(account.Id) : !account.HasVaultedSession)
                {
                    isEligible = false;
                    ineligibilityReason = "Account does not have a vaulted session";
                }
                else if (cooldownAccountIds != null && cooldownAccountIds.Contains(account.Id))
                {
                    isEligible = false;
                    ineligibilityReason = "Account is in cooldown following a recent switch or failure";
                }
                else
                {
                    DateTimeOffset now = evaluationTimeUtc ?? DateTimeOffset.UtcNow;
                    string reqKey = relevantModelKeys!.First();
                    string? canonicalReqKey = CanonicalizeModelKey(reqKey);

                    if (!candidateModelObservations.TryGetValue(account.Id, out var obs) ||
                        obs == null ||
                        !string.Equals(obs.AccountId, account.Id, StringComparison.Ordinal) ||
                        !string.Equals(obs.ModelKey, canonicalReqKey, StringComparison.OrdinalIgnoreCase))
                    {
                        isEligible = false;
                        ineligibilityReason = $"Relevant model '{reqKey}' quota is unknown (no observed telemetry for model)";
                    }
                    else
                    {
                        // Display-only default: an unknown fraction is rejected below, so the
                        // zero here never reaches eligibility logic — it only shapes the
                        // diagnostics reported for ineligible candidates.
                        remainingFraction = obs.RemainingFraction ?? 0;
                        quotaPercent = (int)Math.Round(remainingFraction * 100.0);

                        if (!obs.RemainingFraction.HasValue)
                        {
                            isEligible = false;
                            ineligibilityReason = $"Relevant model '{reqKey}' quota is unknown";
                        }
                        else if (obs.ObservedAtUtc > now)
                        {
                            isEligible = false;
                            ineligibilityReason = $"Observation for model '{reqKey}' is future-dated and cannot be used";
                        }
                        else if (now - obs.ObservedAtUtc >= InactiveCandidateEvidenceLifetime)
                        {
                            isEligible = false;
                            ineligibilityReason = $"Observation for model '{reqKey}' is stale (age exceeds 2 hour lifetime)";
                        }
                        else if (obs.RemainingFraction <= 0.0)
                        {
                            // Exhaustion never becomes eligible via its reset time: the reset
                            // instant is descriptive evidence only. Before the instant, the
                            // candidate is still exhausted; after it, the candidate is
                            // "reset-provisional" — still ineligible until fresh live telemetry
                            // proves the pool actually replenished.
                            isEligible = false;
                            if (!string.IsNullOrWhiteSpace(obs.ResetTime) &&
                                DateTimeOffset.TryParse(obs.ResetTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var resetTimeUtc))
                            {
                                if (now < resetTimeUtc)
                                {
                                    ineligibilityReason = $"Relevant model '{reqKey}' is exhausted on candidate (reset expected at {obs.ResetTime})";
                                }
                                else
                                {
                                    ineligibilityReason = $"Relevant model '{reqKey}' quota is reset-provisional (prior exhaustion expired at {obs.ResetTime}; awaiting fresh live telemetry)";
                                }
                            }
                            else
                            {
                                ineligibilityReason = $"Relevant model '{reqKey}' is exhausted on candidate";
                            }
                        }
                        else if (obs.RemainingFraction < minCandidateFraction)
                        {
                            isEligible = false;
                            ineligibilityReason = $"Relevant model '{reqKey}' quota ({quotaPercent}%) is below minimum candidate threshold ({config.MinimumCandidateQuotaPercent}%)";
                        }
                        else
                        {
                            isEligible = true;
                        }
                    }
                }
            }
            else
            {
                bool hasObservedQuota = accountQuotas.TryGetValue(account.Id, out remainingFraction);
                quotaPercent = hasObservedQuota ? (int)Math.Round(remainingFraction * 100.0) : 0;

                if (!hasObservedQuota)
                {
                    isEligible = false;
                    ineligibilityReason = "Account quota is unknown (no observed telemetry)";
                }
                else if (string.Equals(account.ValidationStatus, "EXPIRED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(account.ValidationStatus, "FAILED", StringComparison.OrdinalIgnoreCase))
                {
                    isEligible = false;
                    ineligibilityReason = $"Account validation status is {account.ValidationStatus}";
                }
                else if (!requiresModelEvidence && remainingFraction < minCandidateFraction)
                {
                    isEligible = false;
                    ineligibilityReason = $"Quota ({quotaPercent}%) is below minimum candidate threshold ({config.MinimumCandidateQuotaPercent}%)";
                }
                else if (vaultedAccountIds != null ? !vaultedAccountIds.Contains(account.Id) : !account.HasVaultedSession)
                {
                    isEligible = false;
                    ineligibilityReason = "Account does not have a vaulted session";
                }
                else if (cooldownAccountIds != null && cooldownAccountIds.Contains(account.Id))
                {
                    isEligible = false;
                    ineligibilityReason = "Account is in cooldown following a recent switch or failure";
                }
                if (isEligible && requiresModelEvidence)
                {
                    if (accountModelQuotas != null && accountModelQuotas.TryGetValue(account.Id, out var candidateModels) && candidateModels != null && candidateModels.Count > 0)
                    {
                        double minRelevantFraction = 1.0;
                        bool hasAnyRelevantModel = false;

                        foreach (var reqKey in relevantModelKeys!)
                        {
                            var matchingModels = candidateModels
                                .Where(m => string.Equals(GetModelKey(m), reqKey, StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            if (matchingModels.Count == 0)
                            {
                                isEligible = false;
                                ineligibilityReason = $"Relevant model '{reqKey}' quota is unknown (no observed telemetry for model)";
                                break;
                            }

                            if (matchingModels.Any(m => m.IsExhausted || (m.RemainingFraction.HasValue && m.RemainingFraction.Value <= 0.0)))
                            {
                                isEligible = false;
                                ineligibilityReason = $"Relevant model '{reqKey}' is exhausted on candidate";
                                break;
                            }

                            // Weakest-link evidence: every matching row must be valid and
                            // known. One unknown or exhausted row blocks the whole key, so
                            // a single healthy row can never hide an unhealthy sibling
                            // sharing the same requested model key.
                            var validCandFractions = matchingModels
                                .Where(m => m.RemainingFraction.HasValue && double.IsFinite(m.RemainingFraction.Value))
                                .Select(m => m.RemainingFraction!.Value)
                                .ToList();

                            if (validCandFractions.Count != matchingModels.Count)
                            {
                                isEligible = false;
                                ineligibilityReason = $"Relevant model '{reqKey}' quota is unknown";
                                break;
                            }

                            double keyMinFraction = validCandFractions.Min();
                            if (keyMinFraction < minCandidateFraction)
                            {
                                isEligible = false;
                                int reqPct = (int)Math.Round(keyMinFraction * 100.0);
                                ineligibilityReason = $"Relevant model '{reqKey}' quota ({reqPct}%) is below minimum candidate threshold ({config.MinimumCandidateQuotaPercent}%)";
                                break;
                            }

                            minRelevantFraction = Math.Min(minRelevantFraction, keyMinFraction);
                            hasAnyRelevantModel = true;
                        }

                        if (isEligible && hasAnyRelevantModel)
                        {
                            remainingFraction = minRelevantFraction;
                            quotaPercent = (int)Math.Round(remainingFraction * 100.0);
                        }
                    }
                    else
                    {
                        isEligible = false;
                        ineligibilityReason = $"Relevant model '{relevantModelKeys!.First()}' quota is unknown (no observed per-model telemetry)";
                    }
                }
            }

            // Score is an informational diagnostic only. Selection is decided solely by the
            // deterministic comparator in step 4 below; the score's bonus arithmetic does
            // not influence which candidate is chosen.
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
