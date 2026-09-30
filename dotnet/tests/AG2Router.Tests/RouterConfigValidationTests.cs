using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AG2Router.AG2.Persistence;
using AG2Router.AG2.Routing;
using AppHost = AG2Router.App.App;
using AG2Router.App.Server;
using AG2Router.App.Services;
using AG2Router.Core.Contracts;
using AG2Router.Core.Models;
using AG2Router.Core.Validation;
using Xunit;

namespace AG2Router.Tests;

public sealed class RouterConfigValidationTests : IAsyncDisposable
{
    private readonly FakeAccountStore _accountStore = new();
    private readonly FakeSessionVault _sessionVault = new();
    private readonly MockAG2Adapter _adapter = new();
    private readonly FakeSwitchCoordinator _switchCoordinator = new();
    private readonly FakeDurableFileWriter _fileWriter = new();
    private readonly RoutingSafetyGate _safetyGate = new();

    public ValueTask DisposeAsync()
    {
        _sessionVault.Dispose();
        return ValueTask.CompletedTask;
    }

    private NativeAutoRouter CreateRouter(RouterConfigDto? initialConfig = null, string? configFilePath = null)
    {
        return new NativeAutoRouter(
            _accountStore,
            _sessionVault,
            _adapter,
            _switchCoordinator,
            initialConfig: initialConfig,
            safetyGate: _safetyGate,
            configFilePath: configFilePath,
            fileWriter: _fileWriter);
    }

    // =========================================================================
    // 1. RouterConfigValidator unit tests
    // =========================================================================

    [Fact]
    public void Validate_NullConfig_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => RouterConfigValidator.Validate(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-500)]
    public void Validate_PollingIntervalZeroOrNegative_ThrowsArgumentOutOfRangeException(int interval)
    {
        var config = new RouterConfigDto(PollingIntervalMs: interval);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.PollingIntervalMs), ex.ParamName);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_LowQuotaThresholdBelowMinimum_ThrowsArgumentOutOfRangeException(int threshold)
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: threshold);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.LowQuotaThresholdPercent), ex.ParamName);
    }

    [Theory]
    [InlineData(51)]
    [InlineData(100)]
    public void Validate_LowQuotaThresholdAboveMaximum_ThrowsArgumentOutOfRangeException(int threshold)
    {
        var config = new RouterConfigDto(LowQuotaThresholdPercent: threshold);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.LowQuotaThresholdPercent), ex.ParamName);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_MinimumCandidateQuotaBelowMinimum_ThrowsArgumentOutOfRangeException(int threshold)
    {
        var config = new RouterConfigDto(MinimumCandidateQuotaPercent: threshold);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.MinimumCandidateQuotaPercent), ex.ParamName);
    }

    [Theory]
    [InlineData(91)]
    [InlineData(100)]
    public void Validate_MinimumCandidateQuotaAboveMaximum_ThrowsArgumentOutOfRangeException(int threshold)
    {
        var config = new RouterConfigDto(MinimumCandidateQuotaPercent: threshold);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.MinimumCandidateQuotaPercent), ex.ParamName);
    }

    [Theory]
    [InlineData(1, 5, 10)]
    [InlineData(1, 50, 90)]
    [InlineData(10000, 15, 30)]
    [InlineData(5000, 5, 90)]
    [InlineData(60000, 50, 10)]
    public void Validate_ValidBoundaryValues_Succeeds(int interval, int lowThreshold, int minCandidate)
    {
        var config = new RouterConfigDto(
            PollingIntervalMs: interval,
            LowQuotaThresholdPercent: lowThreshold,
            MinimumCandidateQuotaPercent: minCandidate);
        RouterConfigValidator.Validate(config);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gemini-2.5-pro")]
    [InlineData("claude-3-5-sonnet")]
    [InlineData("openai/gpt-4o")]
    [InlineData("model_1.0:test@alpha")]
    public void Validate_WorkloadModelKey_ValidValues_Succeeds(string? modelKey)
    {
        var config = new RouterConfigDto(WorkloadModelKey: modelKey);
        RouterConfigValidator.Validate(config);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Validate_WorkloadModelKey_WhitespaceOnly_ThrowsArgumentException(string whitespaceKey)
    {
        var config = new RouterConfigDto(WorkloadModelKey: whitespaceKey);
        var ex = Assert.Throws<ArgumentException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.WorkloadModelKey), ex.ParamName);
        Assert.Contains("Workload model key cannot be whitespace-only.", ex.Message);
    }

    [Fact]
    public void Validate_WorkloadModelKey_Exceeds128Chars_ThrowsArgumentOutOfRangeException()
    {
        var longKey = new string('a', 129);
        var config = new RouterConfigDto(WorkloadModelKey: longKey);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.WorkloadModelKey), ex.ParamName);
        Assert.Equal(129, ex.ActualValue);
        Assert.Contains("Workload model key must not exceed 128 characters.", ex.Message);
    }

    [Theory]
    [InlineData("gemini\npro")]
    [InlineData("gemini\rpro")]
    [InlineData("gemini\tpro")]
    [InlineData("gemini\0pro")]
    public void Validate_WorkloadModelKey_ContainsControlCharacters_ThrowsArgumentException(string keyWithControl)
    {
        var config = new RouterConfigDto(WorkloadModelKey: keyWithControl);
        var ex = Assert.Throws<ArgumentException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.WorkloadModelKey), ex.ParamName);
        Assert.Contains("Workload model key must not contain control characters.", ex.Message);
    }

    [Theory]
    [InlineData("gemini pro")]
    [InlineData("gemini*pro")]
    [InlineData("gemini$pro")]
    [InlineData("gemini#pro")]
    [InlineData("gemini pro 2")]
    public void Validate_WorkloadModelKey_ContainsInvalidCharacters_ThrowsArgumentException(string keyWithInvalidChar)
    {
        var config = new RouterConfigDto(WorkloadModelKey: keyWithInvalidChar);
        var ex = Assert.Throws<ArgumentException>(() => RouterConfigValidator.Validate(config));
        Assert.Equal(nameof(config.WorkloadModelKey), ex.ParamName);
        Assert.Contains("contains invalid characters.", ex.Message);
    }

    [Fact]
    public void RouterConfigDto_MissingWorkloadModelKeyInJson_DeserializesAsNull()
    {
        var json = "{\"AutoSwitchEnabled\":true,\"LowQuotaThresholdPercent\":15,\"MinimumCandidateQuotaPercent\":30,\"PollingIntervalMs\":10000}";
        var deserialized = JsonSerializer.Deserialize<RouterConfigDto>(json);
        Assert.NotNull(deserialized);
        Assert.Null(deserialized.WorkloadModelKey);
    }

    // =========================================================================
    // 2. NativeAutoRouter validation tests
    // =========================================================================

    [Theory]
    [InlineData(0, 15, 30)]
    [InlineData(-10, 15, 30)]
    [InlineData(5000, 4, 30)]
    [InlineData(5000, 55, 30)]
    [InlineData(5000, 15, 9)]
    [InlineData(5000, 15, 95)]
    public async Task NativeAutoRouter_UpdateConfig_InvalidValues_ThrowsAndDoesNotPersistOrApply(
        int interval, int lowThreshold, int minCandidate)
    {
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30, PollingIntervalMs: 10000);
        await using var router = CreateRouter(initialConfig: initial, configFilePath: @"C:\fake\config.json");

        var invalidUpdate = new RouterConfigDto(
            AutoSwitchEnabled: false,
            LowQuotaThresholdPercent: lowThreshold,
            MinimumCandidateQuotaPercent: minCandidate,
            PollingIntervalMs: interval);

        Assert.Throws<ArgumentOutOfRangeException>(() => router.UpdateConfig(invalidUpdate));

        // State must remain strictly unchanged
        Assert.Equal(initial, router.GetConfig());

        // File writer must not have been invoked
        Assert.Null(_fileWriter.LastWrittenContent);
    }

    [Fact]
    public async Task NativeAutoRouter_UpdateConfig_ValidUpdate_AppliesAndPersists()
    {
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30, PollingIntervalMs: 10000);
        await using var router = CreateRouter(initialConfig: initial, configFilePath: @"C:\fake\config.json");

        var validUpdate = new RouterConfigDto(
            AutoSwitchEnabled: false,
            LowQuotaThresholdPercent: 25,
            MinimumCandidateQuotaPercent: 40,
            PollingIntervalMs: 5000);

        var result = router.UpdateConfig(validUpdate);

        Assert.Equal(validUpdate, result);
        Assert.Equal(validUpdate, router.GetConfig());
        Assert.NotNull(_fileWriter.LastWrittenContent);
        Assert.Contains("\"LowQuotaThresholdPercent\": 25", _fileWriter.LastWrittenContent);
    }

    [Fact]
    public void NativeAutoRouter_Constructor_InvalidInitialConfig_ThrowsArgumentOutOfRangeException()
    {
        var invalid = new RouterConfigDto(PollingIntervalMs: -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRouter(initialConfig: invalid));
    }

    [Fact]
    public async Task NativeAutoRouter_Constructor_InvalidPersistedConfig_ThrowsInvalidDataException()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ag2_test_cfg_invalid_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(configPath,
                "{\"AutoSwitchEnabled\":false,\"LowQuotaThresholdPercent\":2,\"MinimumCandidateQuotaPercent\":30,\"PollingIntervalMs\":10000}");

            var ex = Assert.Throws<InvalidDataException>(() => { _ = new NativeAutoRouter(
                _accountStore, _sessionVault, _adapter, _switchCoordinator,
                configFilePath: configPath); });

            Assert.Contains("LowQuotaThresholdPercent", ex.Message);
            Assert.Contains("5% and 50%", ex.Message);
            Assert.Contains("2", ex.Message);
            Assert.NotNull(ex.InnerException);
            Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task NativeAutoRouter_Constructor_MalformedJsonConfig_ThrowsInvalidDataException()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ag2_test_cfg_malformed_{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(configPath, "{ not valid json at all }");

            Assert.Throws<InvalidDataException>(() => { _ = new NativeAutoRouter(
                _accountStore, _sessionVault, _adapter, _switchCoordinator,
                configFilePath: configPath); });
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task NativeAutoRouter_Constructor_LoadsAndPreservesWorkloadModelKeyFromConfig()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ag2_test_cfg_model_{Guid.NewGuid():N}.json");
        try
        {
            var initialJson = "{\"AutoSwitchEnabled\":true,\"LowQuotaThresholdPercent\":15,\"MinimumCandidateQuotaPercent\":30,\"PollingIntervalMs\":10000,\"WorkloadModelKey\":\"gemini-2.5-pro\"}";
            await File.WriteAllTextAsync(configPath, initialJson);

            await using var router = new NativeAutoRouter(
                _accountStore, _sessionVault, _adapter, _switchCoordinator,
                configFilePath: configPath);

            Assert.Equal("gemini-2.5-pro", router.GetConfig().WorkloadModelKey);
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task NativeAutoRouter_UpdateConfig_PersistsWorkloadModelKey()
    {
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30, PollingIntervalMs: 10000);
        await using var router = CreateRouter(initialConfig: initial, configFilePath: @"C:\fake\config.json");

        var validUpdate = new RouterConfigDto(
            AutoSwitchEnabled: true,
            LowQuotaThresholdPercent: 20,
            MinimumCandidateQuotaPercent: 30,
            PollingIntervalMs: 10000,
            WorkloadModelKey: "claude-3-5-sonnet");

        var result = router.UpdateConfig(validUpdate);
        Assert.Equal("claude-3-5-sonnet", result.WorkloadModelKey);
        Assert.Equal(validUpdate, router.GetConfig());
        Assert.NotNull(_fileWriter.LastWrittenContent);
        Assert.Contains("\"WorkloadModelKey\": \"claude-3-5-sonnet\"", _fileWriter.LastWrittenContent);
    }

    // =========================================================================
    // 3. TelemetryPollingCoordinator startup safety tests
    // =========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5000)]
    public void TelemetryPollingCoordinator_Constructor_NonPositiveInterval_ThrowsArgumentOutOfRangeException(int ms)
    {
        var interval = TimeSpan.FromMilliseconds(ms);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TelemetryPollingCoordinator(_adapter, interval: interval));
        Assert.Equal("interval", ex.ParamName);
    }

    [Fact]
    public async Task TelemetryPollingCoordinator_Constructor_ValidInterval_Succeeds()
    {
        await using var coordinator = new TelemetryPollingCoordinator(_adapter, interval: TimeSpan.FromSeconds(5));
        Assert.NotNull(coordinator.CurrentStatus);
    }

    // =========================================================================
    // 4. LoopbackServer POST /api/config validation tests
    // =========================================================================

    [Theory]
    [InlineData(0, 15, 30)]
    [InlineData(-100, 15, 30)]
    [InlineData(5000, 4, 30)]
    [InlineData(5000, 55, 30)]
    [InlineData(5000, 15, 8)]
    [InlineData(5000, 15, 95)]
    public async Task LoopbackServer_PostConfig_InvalidPayload_Returns400BadRequest(
        int interval, int lowThreshold, int minCandidate)
    {
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30, PollingIntervalMs: 10000);
        await using var router = CreateRouter(initialConfig: initial);

        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(requestedPort: 0, autoRouter: router);
            using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };

            var invalidPayload = new RouterConfigDto(
                AutoSwitchEnabled: false,
                LowQuotaThresholdPercent: lowThreshold,
                MinimumCandidateQuotaPercent: minCandidate,
                PollingIntervalMs: interval);

            using var response = await client.PostAsJsonAsync("/api/config", invalidPayload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
            Assert.False(string.IsNullOrWhiteSpace(errorProp.GetString()));

            // Verify configuration was not mutated
            Assert.Equal(initial, router.GetConfig());
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoopbackServer_PostConfig_ValidPayload_Returns200AndUpdatesConfig()
    {
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30, PollingIntervalMs: 10000);
        await using var router = CreateRouter(initialConfig: initial);

        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(requestedPort: 0, autoRouter: router);
            using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };

            var validPayload = new RouterConfigDto(
                AutoSwitchEnabled: false,
                LowQuotaThresholdPercent: 20,
                MinimumCandidateQuotaPercent: 40,
                PollingIntervalMs: 8000);

            using var response = await client.PostAsJsonAsync("/api/config", validPayload);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(20, doc.RootElement.GetProperty("config").GetProperty("lowQuotaThresholdPercent").GetInt32());

            Assert.Equal(validPayload, router.GetConfig());
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoopbackServer_PostConfig_ValidWorkloadModelKey_Returns200AndRoundtripsViaGet()
    {
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30, PollingIntervalMs: 10000);
        await using var router = CreateRouter(initialConfig: initial);

        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(requestedPort: 0, autoRouter: router);
            using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };

            var validPayload = new RouterConfigDto(
                AutoSwitchEnabled: true,
                LowQuotaThresholdPercent: 20,
                MinimumCandidateQuotaPercent: 35,
                PollingIntervalMs: 5000,
                WorkloadModelKey: "gemini-2.5-pro");

            using var postResponse = await client.PostAsJsonAsync("/api/config", validPayload);
            Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);

            string postBody = await postResponse.Content.ReadAsStringAsync();
            using var postDoc = JsonDocument.Parse(postBody);
            Assert.True(postDoc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("gemini-2.5-pro", postDoc.RootElement.GetProperty("config").GetProperty("workloadModelKey").GetString());

            using var getResponse = await client.GetAsync("/api/config");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

            string getBody = await getResponse.Content.ReadAsStringAsync();
            using var getDoc = JsonDocument.Parse(getBody);
            Assert.Equal("gemini-2.5-pro", getDoc.RootElement.GetProperty("config").GetProperty("workloadModelKey").GetString());
            Assert.Equal("gemini-2.5-pro", router.GetConfig().WorkloadModelKey);
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("gemini\npro")]
    [InlineData("gemini pro")]
    public async Task LoopbackServer_PostConfig_InvalidWorkloadModelKey_Returns400BadRequest(string invalidKey)
    {
        var initial = new RouterConfigDto(AutoSwitchEnabled: true, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30, PollingIntervalMs: 10000);
        await using var router = CreateRouter(initialConfig: initial);

        var server = new LoopbackServer();
        try
        {
            await server.StartAsync(requestedPort: 0, autoRouter: router);
            using var client = new HttpClient { BaseAddress = new Uri(server.BoundUrl) };

            var invalidPayload = new RouterConfigDto(
                AutoSwitchEnabled: true,
                LowQuotaThresholdPercent: 15,
                MinimumCandidateQuotaPercent: 30,
                PollingIntervalMs: 10000,
                WorkloadModelKey: invalidKey);

            using var response = await client.PostAsJsonAsync("/api/config", invalidPayload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
            Assert.False(string.IsNullOrWhiteSpace(errorProp.GetString()));

            // Verify configuration was not mutated
            Assert.Equal(initial, router.GetConfig());
        }
        finally
        {
            await server.StopAsync();
            await server.DisposeAsync();
        }
    }

    // =========================================================================
    // 5. Startup error formatting tests
    // =========================================================================

    [Fact]
    public void FormatStartupErrorMessage_SurfacesSpecificInvalidFieldAndBound_ForInvalidDataExceptionWrappingArgumentOutOfRangeException()
    {
        const string fullPath = @"C:\Users\ExampleUser\AppData\Local\AG2-Router\data\config.json";
        var inner = new ArgumentOutOfRangeException("LowQuotaThresholdPercent", 2, "Low quota threshold must be between 5% and 50%.");
        var ex = new InvalidDataException($@"Router configuration file '{fullPath}' contains invalid configuration.", inner);

        var formatted = AppHost.FormatStartupErrorMessage(ex);

        Assert.DoesNotContain(fullPath, formatted);
        Assert.DoesNotContain("ExampleUser", formatted);
        Assert.DoesNotContain(@"C:\Users", formatted);
        Assert.Contains("'config.json'", formatted);
        Assert.Contains("Router configuration file 'config.json' contains invalid configuration", formatted);
        Assert.StartsWith("An error occurred while starting AG2 Router: ", formatted);
        Assert.Contains("LowQuotaThresholdPercent", formatted);
        Assert.Contains("5% and 50%", formatted);
        Assert.Contains("2", formatted);

        Assert.Contains(fullPath, ex.Message);
        Assert.Contains(fullPath, ex.ToString());
    }

    [Fact]
    public void FormatStartupErrorMessage_WhenExceptionAlreadyContainsInnerMessage_PreservesDetailWithoutDuplication()
    {
        const string fullPath = @"C:\Users\ExampleUser\AppData\Local\AG2-Router\data\config.json";
        var inner = new ArgumentOutOfRangeException("LowQuotaThresholdPercent", 2, "Low quota threshold must be between 5% and 50%.");
        var ex = new InvalidDataException($@"Router configuration file '{fullPath}' contains invalid configuration: {inner.Message}", inner);

        var formatted = AppHost.FormatStartupErrorMessage(ex);

        Assert.DoesNotContain(fullPath, formatted);
        Assert.DoesNotContain("ExampleUser", formatted);
        Assert.DoesNotContain(@"C:\Users", formatted);
        Assert.Contains("'config.json'", formatted);
        Assert.Contains("Router configuration file 'config.json' contains invalid configuration", formatted);
        Assert.StartsWith("An error occurred while starting AG2 Router: ", formatted);
        Assert.Contains("LowQuotaThresholdPercent", formatted);
        Assert.Contains("5% and 50%", formatted);
        Assert.Contains("2", formatted);

        int count = formatted.Split("LowQuotaThresholdPercent").Length - 1;
        Assert.Equal(1, count);

        Assert.Contains(fullPath, ex.Message);
        Assert.Contains(fullPath, ex.ToString());
    }

    [Fact]
    public void FormatStartupErrorMessage_SanitizesTokensAndSecrets_AndDoesNotIncludeStackTraces()
    {
        Exception caught;
        try
        {
            throw new InvalidDataException(
                "Startup failed with --token=secret-token-12345 --password=super-secret-password and token: bearer-secret-token\r\n   at AG2Router.App.App.OnStartup(StartupEventArgs e)\r\n   at System.Windows.Application.Run()");
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        var formatted = AppHost.FormatStartupErrorMessage(caught);

        Assert.DoesNotContain("secret-token-12345", formatted);
        Assert.DoesNotContain("super-secret-password", formatted);
        Assert.DoesNotContain("bearer-secret-token", formatted);
        Assert.Contains("[REDACTED]", formatted);

        Assert.DoesNotContain("at AG2Router.App.App.OnStartup", formatted);
        Assert.DoesNotContain("at System.Windows.Application.Run", formatted);
        Assert.DoesNotContain("   at ", formatted);
        Assert.DoesNotContain(nameof(RouterConfigValidationTests), formatted);
    }

    [Fact]
    public void FormatStartupErrorMessage_WithApostropheInPath_SanitizesConfigPathWithoutLeakingApostropheOrTail()
    {
        const string fullPath = @"C:\Users\O'Connor\AppData\Local\AG2-Router\data\config.json";
        var inner = new ArgumentOutOfRangeException("LowQuotaThresholdPercent", 2, "Low quota threshold must be between 5% and 50%.");
        var ex = new InvalidDataException($@"Router configuration file '{fullPath}' contains invalid configuration.", inner);

        var formatted = AppHost.FormatStartupErrorMessage(ex);

        Assert.DoesNotContain(fullPath, formatted);
        Assert.DoesNotContain("O'Connor", formatted);
        Assert.DoesNotContain(@"Connor\AppData", formatted);
        Assert.DoesNotContain(@"C:\Users", formatted);

        Assert.Contains("'config.json'", formatted);
        Assert.Contains("LowQuotaThresholdPercent", formatted);
        Assert.Contains("5% and 50%", formatted);
        Assert.Contains("2", formatted);

        Assert.Contains(fullPath, ex.Message);
        Assert.Contains(fullPath, ex.ToString());
    }

    [Fact]
    public void FormatStartupErrorMessage_MalformedJsonWithApostrophePath_SanitizesPathAndRetainsParseDetail()
    {
        const string fullPath = @"C:\Users\O'Connor\AppData\Local\AG2-Router\data\config.json";
        const string parseDetail = "Expected start of object, found 'n' at line 1 position 2.";
        var inner = new JsonException(parseDetail);
        var ex = new InvalidDataException($@"Router configuration file '{fullPath}' is malformed.", inner);

        var formatted = AppHost.FormatStartupErrorMessage(ex);

        Assert.DoesNotContain(fullPath, formatted);
        Assert.DoesNotContain("O'Connor", formatted);
        Assert.DoesNotContain(@"Connor\AppData", formatted);
        Assert.DoesNotContain(@"C:\Users", formatted);

        Assert.Contains("'config.json'", formatted);
        Assert.Contains("is malformed", formatted);
        Assert.Contains(parseDetail, formatted);

        Assert.Contains(fullPath, ex.Message);
        Assert.Contains(fullPath, ex.ToString());
    }

    [Theory]
    [InlineData(@"C:\Users\ExampleUser\AppData\Local\AG2-Router\data\config.json", "ExampleUser", @"C:\Users")]
    [InlineData(@"C:\Users\O'Connor\AppData\Local\AG2-Router\data\config.json", "O'Connor", @"C:\Users")]
    [InlineData(@"C:\Users\Example User\AppData\Local\AG2 Router\data\config.json", "Example User", @"C:\Users")]
    [InlineData(@"C:\Users\Józef\AppData\Local\AG2-Router\data\config.json", "Józef", @"C:\Users")]
    [InlineData(@"c:\uSeRs\o'cOnNoR\aPpDaTa\lOcAl\Ag2-RoUtEr\DaTa\cOnFiG.jSoN", "o'cOnNoR", @"c:\uSeRs")]
    [InlineData(@"\\server\share\User Data\AG2-Router\config.json", "server", @"\\server\share")]
    public void FormatStartupErrorMessage_PathEdgeCases_SanitizesConfigPathSafely(
        string fullPath, string userOrServer, string driveOrUncPrefix)
    {
        var inner = new ArgumentOutOfRangeException("LowQuotaThresholdPercent", 2, "Low quota threshold must be between 5% and 50%.");
        var ex = new InvalidDataException($@"Router configuration file '{fullPath}' contains invalid configuration.", inner);

        var formatted = AppHost.FormatStartupErrorMessage(ex);

        Assert.DoesNotContain(fullPath, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userOrServer, formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(driveOrUncPrefix, formatted, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("'config.json'", formatted);
        Assert.Contains("Router configuration file 'config.json' contains invalid configuration", formatted);
        Assert.StartsWith("An error occurred while starting AG2 Router: ", formatted);

        Assert.Contains("LowQuotaThresholdPercent", formatted);
        Assert.Contains("5% and 50%", formatted);
        Assert.Contains("2", formatted);

        Assert.Contains(fullPath, ex.Message);
        Assert.Contains(fullPath, ex.ToString());
    }

    // =========================================================================
    // Test Fakes
    // =========================================================================

    private sealed class FakeDurableFileWriter : IDurableFileWriter
    {
        public Exception? Failure { get; set; }
        public string? LastWrittenPath { get; private set; }
        public string? LastWrittenContent { get; private set; }

        public Task WriteAtomicAsync(string destinationPath, string content, CancellationToken cancellationToken)
        {
            if (Failure != null) return Task.FromException(Failure);
            LastWrittenPath = destinationPath;
            LastWrittenContent = content;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccountStore : IAccountStore
    {
        public Task<bool> RestoreAccountIfUnchangedAsync(AccountMetadata expectedCurrent, AccountMetadata previous, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Dictionary<string, AccountMetadata> Accounts { get; } = new(StringComparer.Ordinal);
        public string? ActiveAccountId { get; set; }

        public Task<IReadOnlyList<AccountMetadata>> ListAccountsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AccountMetadata>>(Accounts.Values.ToList());

        public Task<AccountMetadata?> GetAccountAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.TryGetValue(id, out var a) ? a : null);

        public Task<AccountMetadata?> GetAccountByEmailAsync(string email, CancellationToken cancellationToken = default)
            => Task.FromResult(Accounts.Values.FirstOrDefault(a => string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));

        public Task<string?> GetActiveAccountIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ActiveAccountId);

        public Task SetActiveAccountIdAsync(string? id, CancellationToken cancellationToken = default)
        {
            ActiveAccountId = id;
            return Task.CompletedTask;
        }

        public Task<AccountMetadata> AddAccountAsync(CreateAccountInput input, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<AccountMetadata?> UpdateAccountAsync(string id, UpdateAccountInput updates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAccountAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> RemoveAccountIfUnchangedAsync(AccountMetadata expected, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> CompareExchangeActiveAccountIdAsync(string? expectedId, string? newId, CancellationToken cancellationToken = default)
        {
            if (ActiveAccountId == expectedId)
            {
                ActiveAccountId = newId;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<AccountMetadata?> TryFinalizeSwitchAsync(string? expectedActiveId, string targetId, UpdateAccountInput updates, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeSessionVault : ISessionVault, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ag2_test_vault_{Guid.NewGuid():N}");
        public HashSet<string> StoredIds { get; } = new(StringComparer.Ordinal);

        public Task<bool> HasSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Contains(accountId));

        public Task<byte[]?> GetSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(StoredIds.Contains(accountId) ? new byte[] { 1, 2, 3 } : null);

        public Task<bool> RemoveSessionAsync(string accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(StoredIds.Remove(accountId));

        public Task<IReadOnlyList<string>> ListStoredAccountIdsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(StoredIds.ToList());

        public string GetVaultPath() => Path.Combine(_directory, "vault");

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }



    private sealed class FakeSwitchCoordinator : INativeAccountSwitchCoordinator
    {
        public NativeSwitchStatus GetStatus() => new(null, NativeSwitchStates.Idle, null);

        public Task<JournalResolutionResult> ResolveQuarantinedJournalAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new JournalResolutionResult(JournalResolutionStatus.NoJournal, "No switch journal present."));

        public Task<NativeSwitchResult> SwitchAsync(string targetAccountId, CancellationToken cancellationToken = default)
            => Task.FromResult(new NativeSwitchResult(
                TransactionId: "tx_1",
                Success: true,
                Code: SwitchResultCodes.Success,
                State: NativeSwitchStates.Complete,
                TargetAccountId: targetAccountId,
                TargetEmail: "target@example.com",
                PreviousAccountId: "curr",
                PreviousEmail: "curr@example.com",
                Message: "Success",
                StagesCompleted: Array.Empty<string>(),
                StartedAt: DateTimeOffset.UtcNow.ToString("O"),
                FinishedAt: DateTimeOffset.UtcNow.ToString("O"),
                ManualRecoveryRequired: false
            ));

        public Task CoordinateShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
