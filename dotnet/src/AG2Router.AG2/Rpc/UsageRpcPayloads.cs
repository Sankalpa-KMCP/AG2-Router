using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AG2Router.AG2.Rpc;

/// <summary>
/// Minimal typed view of one <c>GetCascadeTrajectoryGeneratorMetadata</c> entry. Only the
/// accounting subset is declared; prompt-adjacent payload fields (systemPrompt, promptSections,
/// tool schemas, message content) are deliberately absent from this model so they are parsed
/// and immediately discarded, never logged, persisted, or exposed.
/// </summary>
public sealed record RawGeneratorMetadataEntry
{
    [JsonPropertyName("cascadeId")]
    public string? CascadeId { get; init; }

    [JsonPropertyName("executionId")]
    public string? ExecutionId { get; init; }

    [JsonPropertyName("usage")]
    public RawUsageCounters? Usage { get; init; }

    [JsonPropertyName("chatModel")]
    public RawChatModelInfo? ChatModel { get; init; }
}

/// <summary>
/// Provider token counters. The provider reports int64-like values as JSON strings, so the
/// counters are kept as <see cref="JsonElement"/> and parsed defensively: numeric and string
/// forms are both accepted, anything else fails per-call parsing without fabrication.
/// </summary>
public sealed record RawUsageCounters
{
    [JsonPropertyName("responseId")]
    public string? ResponseId { get; init; }

    [JsonPropertyName("inputTokens")]
    public JsonElement? InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public JsonElement? OutputTokens { get; init; }

    [JsonPropertyName("responseOutputTokens")]
    public JsonElement? ResponseOutputTokens { get; init; }

    [JsonPropertyName("thinkingOutputTokens")]
    public JsonElement? ThinkingOutputTokens { get; init; }

    [JsonPropertyName("cacheReadTokens")]
    public JsonElement? CacheReadTokens { get; init; }
}

/// <summary>
/// Model identity for one call. <see cref="ResponseModel"/> is the validated canonical
/// identifier; <see cref="Model"/> was observed to be a placeholder and is captured only so
/// the collector can deliberately ignore it.
/// </summary>
public sealed record RawChatModelInfo
{
    [JsonPropertyName("responseModel")]
    public string? ResponseModel { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>
/// Tolerant extraction of the generator-metadata entry list. The validated runtime returns
/// the entries as a JSON array, possibly wrapped in one object property; the extractor accepts
/// a root array, a root object property holding an array of objects, or one level of object
/// nesting before that array, and fails closed otherwise.
/// </summary>
/// <summary>
/// Minimal typed request body for <c>GetCascadeTrajectoryGeneratorMetadata</c>. Serialized with
/// <see cref="JsonSerializer"/> so every valid cascadeId (quotes, backslashes, Unicode) is
/// escaped correctly by construction — never assembled through string formatting.
/// </summary>
public sealed record GeneratorMetadataRequest
{
    [JsonPropertyName("cascadeId")]
    public string CascadeId { get; init; } = string.Empty;
}

public static class UsageRpcPayloads
{

    private static readonly JsonSerializerOptions EntrySerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<RawGeneratorMetadataEntry> ExtractEntries(JsonDocument document)
    {
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return DeserializeEntries(root);

            if (root.ValueKind == JsonValueKind.Object)
            {
                // Prefer arrays whose elements look like usage entries (they carry a "usage"
                // object); GM payloads also contain unrelated object arrays such as
                // promptSections, which must never be mistaken for entries.
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array && LooksLikeUsageEntryArray(property.Value))
                        return DeserializeEntries(property.Value);
                }

                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object)
                        continue;
                    foreach (var nested in property.Value.EnumerateObject())
                    {
                        if (nested.Value.ValueKind == JsonValueKind.Array && LooksLikeUsageEntryArray(nested.Value))
                            return DeserializeEntries(nested.Value);
                    }
                }
            }

            return [];
        }
    }

    private static bool LooksLikeUsageEntryArray(JsonElement array) =>
        array.EnumerateArray().Any(element =>
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("usage", out var usage) &&
            usage.ValueKind == JsonValueKind.Object);

    private static IReadOnlyList<RawGeneratorMetadataEntry> DeserializeEntries(JsonElement array)
    {
        var entries = new List<RawGeneratorMetadataEntry>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;
            var entry = JsonSerializer.Deserialize<RawGeneratorMetadataEntry>(element.GetRawText(), EntrySerializerOptions);
            if (entry is not null)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Parses one provider token counter: accepts JSON numbers and int64-like strings, rejects
    /// everything else. Absence is represented by a null element and stays distinct from zero.
    /// </summary>
    public static bool TryGetInt64Token(JsonElement? value, out long result)
    {
        result = 0;
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;

        if (value.Value.ValueKind == JsonValueKind.Number)
            return value.Value.TryGetInt64(out result);

        if (value.Value.ValueKind == JsonValueKind.String)
        {
            string? text = value.Value.GetString();
            return !string.IsNullOrWhiteSpace(text)
                && long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        return false;
    }
}
