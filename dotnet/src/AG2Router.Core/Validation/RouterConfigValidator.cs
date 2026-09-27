using AG2Router.Core.Models;

namespace AG2Router.Core.Validation;

/// <summary>
/// Authoritative validation policy for router configuration.
/// Enforces valid bounds for polling intervals and quota thresholds
/// across API ingestion, runtime mutations, and persisted storage.
/// </summary>
public static class RouterConfigValidator
{
    public const int MinLowQuotaThresholdPercent = 5;
    public const int MaxLowQuotaThresholdPercent = 50;
    public const int MinCandidateQuotaPercent = 10;
    public const int MaxCandidateQuotaPercent = 90;
    public const int MinPollingIntervalMs = 1;

    /// <summary>
    /// Validates that the provided configuration values satisfy domain constraints.
    /// Throws ArgumentNullException if config is null.
    /// Throws ArgumentOutOfRangeException if any value is out of bounds.
    /// </summary>
    public static void Validate(RouterConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.PollingIntervalMs < MinPollingIntervalMs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config.PollingIntervalMs),
                config.PollingIntervalMs,
                $"Polling interval must be at least {MinPollingIntervalMs} ms.");
        }

        if (config.LowQuotaThresholdPercent < MinLowQuotaThresholdPercent ||
            config.LowQuotaThresholdPercent > MaxLowQuotaThresholdPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config.LowQuotaThresholdPercent),
                config.LowQuotaThresholdPercent,
                $"Low quota threshold must be between {MinLowQuotaThresholdPercent}% and {MaxLowQuotaThresholdPercent}%.");
        }

        if (config.MinimumCandidateQuotaPercent < MinCandidateQuotaPercent ||
            config.MinimumCandidateQuotaPercent > MaxCandidateQuotaPercent)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config.MinimumCandidateQuotaPercent),
                config.MinimumCandidateQuotaPercent,
                $"Minimum candidate quota must be between {MinCandidateQuotaPercent}% and {MaxCandidateQuotaPercent}%.");
        }
    }
}
