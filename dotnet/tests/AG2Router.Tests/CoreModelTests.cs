using System.Text.Json;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class CoreModelTests
{
    [Fact]
    public void CanonicalQuotaAndAccountFieldsSerializeWithFrontendWireNames()
    {
        var model = new CanonicalModelQuotaDto("tier:pro", "Pro", "pro", 0.4, null, false, ["Standard"]);
        using var modelJson = JsonDocument.Parse(JsonSerializer.Serialize(model, JsonSerializerOptions.Web));
        Assert.Equal("tier:pro", modelJson.RootElement.GetProperty("canonicalKey").GetString());
        Assert.Equal("Pro", modelJson.RootElement.GetProperty("displayLabel").GetString());

        var account = new AccountMetadata("acc_1", "user@example.com", null, 1, false,
            "VALID", false, "2026-09-20T08:00:00Z", "2026-09-20T08:00:00Z",
            LastActiveAt: "2026-09-20T09:00:00Z");
        using var accountJson = JsonDocument.Parse(JsonSerializer.Serialize(account, JsonSerializerOptions.Web));
        Assert.True(accountJson.RootElement.TryGetProperty("lastActiveAt", out _));
        Assert.False(accountJson.RootElement.TryGetProperty("lastUsedAt", out _));
    }
    [Fact]
    public void RouterConfigDto_Defaults_MatchSystemContracts()
    {
        var config = new RouterConfigDto();
        Assert.False(config.AutoSwitchEnabled);
        Assert.Equal(15, config.LowQuotaThresholdPercent);
        Assert.Equal(30, config.MinimumCandidateQuotaPercent);
        Assert.Equal(10000, config.PollingIntervalMs);
    }

    [Fact]
    public void CanonicalModelQuotaDto_Serialization_DoesNotContainPhantomFields()
    {
        var model = new CanonicalModelQuotaDto("tier:pro", "Pro", "pro", 0.4, null, false, ["Standard"]);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(model, JsonSerializerOptions.Web));
        var root = doc.RootElement;

        Assert.Equal("tier:pro", root.GetProperty("canonicalKey").GetString());
        Assert.Equal("Pro", root.GetProperty("displayLabel").GetString());
        Assert.Equal("pro", root.GetProperty("modelOrTier").GetString());
        Assert.Equal(0.4, root.GetProperty("remainingFraction").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("resetTime").ValueKind);
        Assert.False(root.GetProperty("isExhausted").GetBoolean());
        Assert.Single(root.GetProperty("modes").EnumerateArray());

        // AUD-103: Assert phantom fields are NOT emitted by backend
        Assert.False(root.TryGetProperty("timeUntilReset", out _));
        Assert.False(root.TryGetProperty("isRollingWindow", out _));
    }

    [Fact]
    public void RouterConfigDto_Serialization_EmitsAutoSwitchEnabled()
    {
        var config = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 20, MinimumCandidateQuotaPercent: 35, PollingIntervalMs: 5000);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(config, JsonSerializerOptions.Web));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("autoSwitchEnabled").GetBoolean());
        Assert.Equal(20, root.GetProperty("lowQuotaThresholdPercent").GetInt32());
        Assert.Equal(35, root.GetProperty("minimumCandidateQuotaPercent").GetInt32());
        Assert.Equal(5000, root.GetProperty("pollingIntervalMs").GetInt32());
    }

    [Fact]
    public void ModelQuotaDto_AllowsNullModelOrTier_AndSerializesTruthfully()
    {
        var modelWithNullTier = new ModelQuotaDto("Custom Model", null, 0.75, null, false);
        Assert.Null(modelWithNullTier.ModelOrTier);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(modelWithNullTier, JsonSerializerOptions.Web));
        var root = doc.RootElement;

        Assert.Equal("Custom Model", root.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("modelOrTier").ValueKind);
        Assert.Equal(0.75, root.GetProperty("remainingFraction").GetDouble());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("resetTime").ValueKind);
        Assert.False(root.GetProperty("isExhausted").GetBoolean());
    }

    [Fact]
    public void AccountMetadata_SerializesAndDeserializesCleanly()
    {
        var account = new AccountMetadata(
            Id: "acc_123",
            Email: "user@example.com",
            Name: "User One",
            Priority: 1,
            IsReserve: false,
            ValidationStatus: "VALID",
            HasVaultedSession: true,
            CreatedAt: "2026-09-20T08:00:00.000Z",
            UpdatedAt: "2026-09-20T08:00:00.000Z",
            IsActive: true
        );

        var json = JsonSerializer.Serialize(account);
        var deserialized = JsonSerializer.Deserialize<AccountMetadata>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(account.Id, deserialized.Id);
        Assert.Equal(account.Email, deserialized.Email);
        Assert.True(deserialized.HasVaultedSession);
        Assert.True(deserialized.IsActive);
        Assert.Null(deserialized.Alias);
    }

    [Fact]
    public void AccountMetadata_SerializesAndDeserializesCleanly_WithAlias()
    {
        var account = new AccountMetadata(
            Id: "acc_123",
            Email: "user@example.com",
            Name: "User One",
            Priority: 1,
            IsReserve: false,
            ValidationStatus: "VALID",
            HasVaultedSession: true,
            CreatedAt: "2026-09-20T08:00:00.000Z",
            UpdatedAt: "2026-09-20T08:00:00.000Z",
            Alias: "Work",
            IsActive: true
        );

        var json = JsonSerializer.Serialize(account);
        var deserialized = JsonSerializer.Deserialize<AccountMetadata>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(account.Id, deserialized.Id);
        Assert.Equal("Work", deserialized.Alias);
    }

    [Fact]
    public void SystemStatusDto_HandlesNullTelemetryTruthfully()
    {
        var status = new SystemStatusDto(
            Status: "ok",
            Ag2: new Ag2StatusDto(
                Connected: false,
                Status: "OFFLINE",
                Activity: null,
                Message: "Waiting for Antigravity 2"
            ),
            Router: new RouterStatusDto(
                State: "IDLE",
                AutoSwitchEnabled: false,
                ActiveAccountId: null,
                ActiveAccountEmail: null,
                PendingTargetAccountId: null,
                LastEvaluatedAt: null,
                LastDecisionReason: "Foundation un-migrated state",
                Config: new RouterConfigDto()
            ),
            Telemetry: null
        );

        Assert.Equal("ok", status.Status);
        Assert.False(status.Ag2.Connected);
        Assert.Null(status.Telemetry);
    }
}
