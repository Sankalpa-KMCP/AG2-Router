using System.Linq;
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
    public const int MaxWorkloadModelKeyLength = 128;

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

        if (config.WorkloadModelKey != null && config.WorkloadModelKey.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(config.WorkloadModelKey))
            {
                throw new ArgumentException("Workload model key cannot be whitespace-only.", nameof(config.WorkloadModelKey));
            }

            if (config.WorkloadModelKey.Length > MaxWorkloadModelKeyLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(config.WorkloadModelKey),
                    config.WorkloadModelKey.Length,
                    $"Workload model key must not exceed {MaxWorkloadModelKeyLength} characters.");
            }

            if (config.WorkloadModelKey.Any(char.IsControl))
            {
                throw new ArgumentException("Workload model key must not contain control characters.", nameof(config.WorkloadModelKey));
            }

            if (!config.WorkloadModelKey.All(IsValidModelKeyChar))
            {
                throw new ArgumentException(
                    $"Workload model key '{config.WorkloadModelKey}' contains invalid characters.",
                    nameof(config.WorkloadModelKey));
            }
        }
    }

    private static bool IsValidModelKeyChar(char c) =>
        (c >= 'a' && c <= 'z') ||
        (c >= 'A' && c <= 'Z') ||
        (c >= '0' && c <= '9') ||
        c is '_' or '-' or '.' or ':' or '/' or '@';
}
