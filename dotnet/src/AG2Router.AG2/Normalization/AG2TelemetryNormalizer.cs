using System.Text.RegularExpressions;
using AG2Router.AG2.Rpc;
using AG2Router.Core.Models;

namespace AG2Router.AG2.Normalization;

/// <summary>
/// Normalizes raw Connect-RPC response payloads into stable, immutable Core DTOs.
/// Enforces critical domain invariants:
/// 1. Strict segregation of Prompt and Flow credit pools (never combined or summed).
/// 2. Model quotas preserved individually without artificial aggregation.
/// 3. Clamping of fractional metrics strictly to [0.0, 1.0].
/// 4. Resilient null fallback handling (zero NullReferenceExceptions).
/// </summary>
public static class AG2TelemetryNormalizer
{
    public static AccountIdentityDto? NormalizeAccountIdentity(RawUserStatusResponse? raw)
    {
        var email = raw?.UserStatus?.Email?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        var name = raw?.UserStatus?.Name?.Trim();
        var tierId = raw?.UserStatus?.UserTier?.Id;
        var tierName = raw?.UserStatus?.UserTier?.Name ?? raw?.UserStatus?.PlanStatus?.PlanInfo?.PlanName;

        return new AccountIdentityDto(
            Email: email,
            Name: string.IsNullOrWhiteSpace(name) ? null : name,
            TierId: string.IsNullOrWhiteSpace(tierId) ? null : tierId,
            TierName: string.IsNullOrWhiteSpace(tierName) ? null : tierName,
            RawStatusTimestamp: DateTime.UtcNow.ToString("o")
        );
    }

    public static QuotaSnapshotDto? NormalizeQuotaSnapshot(RawUserStatusResponse? raw)
    {
        var u = raw?.UserStatus;
        if (u == null)
        {
            return null;
        }

        var rawConfigs = u.CascadeModelConfigData?.ClientModelConfigs ?? new List<RawClientModelConfig>();
        var models = new List<ModelQuotaDto>(rawConfigs.Count);

        foreach (var cfg in rawConfigs)
        {
            if (cfg == null) continue;

            var label = !string.IsNullOrWhiteSpace(cfg.Label)
                ? cfg.Label.Trim()
                : (!string.IsNullOrWhiteSpace(cfg.ModelOrTier) ? cfg.ModelOrTier.Trim() : "Unknown Model");

            var q = cfg.QuotaInfo;
            double? fraction = q?.RemainingFraction is double f && double.IsFinite(f)
                ? Math.Clamp(f, 0.0, 1.0)
                : null;
            bool isExhausted = (q?.IsExhausted == true) || fraction == 0.0;

            models.Add(new ModelQuotaDto(
                Label: label,
                ModelOrTier: cfg.ModelOrTier?.Trim() ?? cfg.ModelId?.Trim(),
                RemainingFraction: fraction,
                ResetTime: q?.ResetTime,
                IsExhausted: isExhausted
            ));
        }

        CreditPoolDto? promptCredits = null;
        CreditPoolDto? flowCredits = null;

        if (u.PlanStatus is { } plan)
        {
            long? monthlyPrompt = plan.PlanInfo?.MonthlyPromptCredits;
            long? availPrompt = plan.AvailablePromptCredits;
            long? usedPrompt = (monthlyPrompt.HasValue && availPrompt.HasValue)
                ? Math.Max(0, monthlyPrompt.Value - availPrompt.Value)
                : null;
            promptCredits = new CreditPoolDto(
                AvailableCredits: availPrompt,
                MonthlyCredits: monthlyPrompt,
                UsedCredits: usedPrompt
            );

            long? monthlyFlow = plan.PlanInfo?.MonthlyFlowCredits;
            long? availFlow = plan.AvailableFlowCredits;
            long? usedFlow = (monthlyFlow.HasValue && availFlow.HasValue)
                ? Math.Max(0, monthlyFlow.Value - availFlow.Value)
                : null;
            flowCredits = new CreditPoolDto(
                AvailableCredits: availFlow,
                MonthlyCredits: monthlyFlow,
                UsedCredits: usedFlow
            );
        }

        var canonicalModels = CanonicalizeModelQuotas(models);

        return new QuotaSnapshotDto(
            Timestamp: DateTime.UtcNow.ToString("o"),
            Models: models,
            PromptCredits: promptCredits,
            FlowCredits: flowCredits,
            CanonicalModels: canonicalModels
        );
    }

    public static ActivityStatusDto NormalizeActivitySnapshot(RawTrajectoriesResponse? raw)
    {
        if (raw?.TrajectorySummaries == null || raw.TrajectorySummaries.Count == 0)
        {
            return new ActivityStatusDto(
                State: "IDLE",
                TotalTrajectories: 0,
                RunningTrajectories: 0,
                Timestamp: DateTime.UtcNow.ToString("o")
            );
        }

        var validEntries = raw.TrajectorySummaries.Values.Where(t => t != null).ToList();
        int total = validEntries.Count;
        int running = validEntries.Count(t => string.Equals(t.Status, "CASCADE_RUN_STATUS_RUNNING", StringComparison.OrdinalIgnoreCase));

        return new ActivityStatusDto(
            State: running > 0 ? "BUSY" : "IDLE",
            TotalTrajectories: total,
            RunningTrajectories: running,
            Timestamp: DateTime.UtcNow.ToString("o")
        );
    }

    private static readonly Regex VariantAnnotationRegex = new(
        @"\s*\((?:thinking|reasoning)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string CleanModelLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "Unknown Model";
        }

        string cleaned = VariantAnnotationRegex.Replace(label, string.Empty).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown Model" : cleaned;
    }

    public static string ExtractMode(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "Standard";
        }

        if (label.Contains("(thinking)", StringComparison.OrdinalIgnoreCase))
        {
            return "Thinking";
        }

        if (label.Contains("(reasoning)", StringComparison.OrdinalIgnoreCase))
        {
            return "Reasoning";
        }

        return "Standard";
    }

    public static string? SelectLatestResetTime(IEnumerable<string?> resetTimes)
    {
        var valid = resetTimes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .ToList();

        if (valid.Count == 0) return null;
        if (valid.Count == 1) return valid[0];

        var parsed = new List<(string Raw, DateTimeOffset Parsed)>();
        bool allParsed = true;
        foreach (var t in valid)
        {
            if (DateTimeOffset.TryParse(t, out var dt))
            {
                parsed.Add((t, dt));
            }
            else
            {
                allParsed = false;
                break;
            }
        }

        if (allParsed && parsed.Count == valid.Count)
        {
            return parsed.MaxBy(p => p.Parsed).Raw;
        }

        return valid.OrderByDescending(s => s, StringComparer.Ordinal).First();
    }

    public static IReadOnlyList<CanonicalModelQuotaDto> CanonicalizeModelQuotas(IReadOnlyList<ModelQuotaDto>? models)
    {
        if (models == null || models.Count == 0)
        {
            return Array.Empty<CanonicalModelQuotaDto>();
        }

        var groups = new List<(string Key, List<ModelQuotaDto> Items)>();
        for (int row = 0; row < models.Count; row++)
        {
            var model = models[row];
            // Neither a presentation label, model/tier, nor matching reset instant
            // proves that two source rows share one capacity pool.
            string? tier = string.IsNullOrWhiteSpace(model.ModelOrTier)
                ? null : model.ModelOrTier.Trim().ToLowerInvariant();
            string baseKey = tier != null ? $"tier:{tier}" : $"row:{row}";
            string key = groups.Any(g => g.Key == baseKey)
                ? $"{baseKey}:row:{row}" : baseKey;
            groups.Add((key, new List<ModelQuotaDto> { model }));
        }

        var canonical = new List<CanonicalModelQuotaDto>(groups.Count);
        foreach (var (key, items) in groups)
        {
            string displayLabel = CleanModelLabel(items[0].Label);
            if (string.IsNullOrWhiteSpace(displayLabel) || displayLabel == "Unknown Model")
            {
                displayLabel = items[0].ModelOrTier?.Trim() ?? "Unknown Model";
            }

            var modes = new List<string>();
            foreach (var item in items)
            {
                string mode = ExtractMode(item.Label);
                if (!modes.Contains(mode, StringComparer.OrdinalIgnoreCase))
                {
                    modes.Add(mode);
                }
            }

            var validFractions = items.Where(m => m.RemainingFraction.HasValue && double.IsFinite(m.RemainingFraction.Value))
                .Select(m => m.RemainingFraction!.Value).ToList();
            double? remainingFraction = validFractions.Count > 0 ? Math.Clamp(validFractions.Min(), 0.0, 1.0) : null;
            bool isExhausted = items.Any(m => m.IsExhausted) || (remainingFraction.HasValue && remainingFraction.Value <= 0.0);
            string? resetTime = SelectLatestResetTime(items.Select(m => m.ResetTime));
            string? modelOrTier = items.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.ModelOrTier))?.ModelOrTier?.Trim();

            canonical.Add(new CanonicalModelQuotaDto(
                Key: key,
                Label: displayLabel,
                ModelOrTier: modelOrTier,
                RemainingFraction: remainingFraction,
                ResetTime: resetTime,
                IsExhausted: isExhausted,
                Modes: modes
            ));
        }

        return canonical;
    }
}
