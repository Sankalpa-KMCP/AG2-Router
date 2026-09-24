using System.Text.Json.Serialization;

namespace AG2Router.Core.Models;

/// <summary>
/// Core constants defining the encrypted vault file specification.
/// </summary>
public static class VaultConstants
{
    public const string Magic = "AG2_ROUTER_SESSION_VAULT";
    public const int SchemaVersion = 1;
    public const string DefaultAg2WinCredTarget = "gemini:antigravity";
    public const string DefaultAg2WinCredUserName = "antigravity";
    public const int MaxAg2WinCredUserNameLength = 513;
}

/// <summary>
/// Metadata and encrypted payload for an individual stored account session.
/// Encrypted with DPAPI CurrentUser.
/// </summary>
public record VaultAccountRecord(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("encryptedPayloadBase64")] string EncryptedPayloadBase64,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt
);

/// <summary>
/// Top-level versioned file envelope for `sessions.dat`.
/// </summary>
public record VaultFileEnvelope(
    [property: JsonPropertyName("magic")] string Magic,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("records")] Dictionary<string, VaultAccountRecord> Records
);

/// <summary>
/// Internal plaintext payload structure before DPAPI encryption.
/// Enforces identity framing to prevent record swapping or target mismatch.
/// </summary>
public record VaultedSessionPlaintext(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("credentialBlobBase64")] string CredentialBlobBase64,
    [property: JsonPropertyName("enrolledAt")] string EnrolledAt
);
