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
            double fraction = q?.RemainingFraction is double f ? Math.Clamp(f, 0.0, 1.0) : 1.0;
            bool isExhausted = (q?.IsExhausted == true) || fraction <= 0.0;

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
            long monthlyPrompt = plan.PlanInfo?.MonthlyPromptCredits ?? 0;
            long availPrompt = plan.AvailablePromptCredits ?? 0;
            promptCredits = new CreditPoolDto(
                AvailableCredits: availPrompt,
                MonthlyCredits: monthlyPrompt,
                UsedCredits: Math.Max(0, monthlyPrompt - availPrompt)
            );

            long monthlyFlow = plan.PlanInfo?.MonthlyFlowCredits ?? 0;
            long availFlow = plan.AvailableFlowCredits ?? 0;
            flowCredits = new CreditPoolDto(
                AvailableCredits: availFlow,
                MonthlyCredits: monthlyFlow,
                UsedCredits: Math.Max(0, monthlyFlow - availFlow)
            );
        }

        return new QuotaSnapshotDto(
            Timestamp: DateTime.UtcNow.ToString("o"),
            Models: models,
            PromptCredits: promptCredits,
            FlowCredits: flowCredits
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
}
