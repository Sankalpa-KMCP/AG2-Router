using System.Text.Json;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests;

public class CoreModelTests
{
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
