using System.Text.Json.Serialization;

namespace AG2Router.AG2.Rpc;

public class RawUserStatusResponse
{
    [JsonPropertyName("userStatus")]
    public RawUserStatus? UserStatus { get; set; }
}

public class RawUserStatus
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("userTier")]
    public RawUserTier? UserTier { get; set; }

    [JsonPropertyName("planStatus")]
    public RawPlanStatus? PlanStatus { get; set; }

    [JsonPropertyName("cascadeModelConfigData")]
    public RawCascadeModelConfigData? CascadeModelConfigData { get; set; }
}

public class RawUserTier
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public class RawPlanStatus
{
    [JsonPropertyName("availablePromptCredits")]
    public long? AvailablePromptCredits { get; set; }

    [JsonPropertyName("availableFlowCredits")]
    public long? AvailableFlowCredits { get; set; }

    [JsonPropertyName("planInfo")]
    public RawPlanInfo? PlanInfo { get; set; }
}

public class RawPlanInfo
{
    [JsonPropertyName("planName")]
    public string? PlanName { get; set; }

    [JsonPropertyName("teamsTier")]
    public string? TeamsTier { get; set; }

    [JsonPropertyName("monthlyPromptCredits")]
    public long? MonthlyPromptCredits { get; set; }

    [JsonPropertyName("monthlyFlowCredits")]
    public long? MonthlyFlowCredits { get; set; }
}

public class RawCascadeModelConfigData
{
    [JsonPropertyName("clientModelConfigs")]
    public List<RawClientModelConfig>? ClientModelConfigs { get; set; }
}

public class RawClientModelConfig
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("modelOrTier")]
    public string? ModelOrTier { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("quotaInfo")]
    public RawQuotaInfo? QuotaInfo { get; set; }
}

public class RawQuotaInfo
{
    [JsonPropertyName("remainingFraction")]
    public double? RemainingFraction { get; set; }

    [JsonPropertyName("resetTime")]
    public string? ResetTime { get; set; }

    [JsonPropertyName("isExhausted")]
    public bool? IsExhausted { get; set; }
}

public class RawTrajectoriesResponse
{
    [JsonPropertyName("trajectorySummaries")]
    public Dictionary<string, RawTrajectorySummary>? TrajectorySummaries { get; set; }
}

public class RawTrajectorySummary
{
    [JsonPropertyName("cascadeId")]
    public string? CascadeId { get; set; }

    [JsonPropertyName("trajectoryId")]
    public string? TrajectoryId { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("stepCount")]
    public int? StepCount { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("createdTime")]
    public string? CreatedTime { get; set; }

    [JsonPropertyName("lastModifiedTime")]
    public string? LastModifiedTime { get; set; }
}
