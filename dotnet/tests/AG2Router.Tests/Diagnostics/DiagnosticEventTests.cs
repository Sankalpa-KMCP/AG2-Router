using System.Net.Http;
using System.Text.Json;
using AG2Router.Core.Diagnostics;
using Xunit;

namespace AG2Router.Tests.Diagnostics;

[Collection("DiagnosticSession")]
public sealed class DiagnosticEventTests
{
    [Fact]
    public void Event_RoundTripsJson_WithAllAllowlistedFields()
    {
        var original = new DiagnosticEvent
        {
            SchemaVersion = 1,
            TimestampUtc = "2026-10-10T12:00:00.0000000Z",
            Sequence = 42,
            EventInstanceId = "test-instance-id",
            Severity = DiagnosticSeverity.Info,
            EventId = DiagnosticEventCatalog.StartupStarted,
            EventName = DiagnosticEventCatalog.GetName(DiagnosticEventCatalog.StartupStarted),
            Component = "Bootstrap",
            Operation = "AppStartup",
            RunId = "test-run-id",
            CorrelationId = "test-corr-id",
            OperationId = "test-op-id",
            Outcome = DiagnosticOutcome.Success,
            FailureCategory = DiagnosticFailureCategory.None,
            SafeMessage = DiagnosticEventCatalog.GetSafeDescription(DiagnosticEventCatalog.StartupStarted),
            RecoveryStatus = RecoveryStatus.NotAttempted,
            AffectedFeature = AffectedFeature.Bootstrap,
            DurationMs = 123.45,
            HttpStatus = 200,
            ExceptionType = "None",
            HResult = 0,
            ApplicationVersion = "0.4.1-rc.4",
            SourceCommit = "ff89ffc6c20539e2cdea575e8417a4b14e4497aa"
        };

        string json = original.ToJson();

        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"eventId\":1001", json);
        Assert.Contains("\"eventName\":\"StartupStarted\"", json);
        Assert.Contains("\"severity\":\"Info\"", json);
        Assert.Contains("\"component\":\"Bootstrap\"", json);
        Assert.Contains("\"operation\":\"AppStartup\"", json);
        Assert.Contains("\"outcome\":\"Success\"", json);
        Assert.Contains("\"failureCategory\":\"None\"", json);
        Assert.Contains("\"safeMessage\":\"Application startup sequence initiated.\"", json);
        Assert.Contains("\"correlationId\":\"test-corr-id\"", json);
        Assert.Contains("\"durationMs\":123.45", json);
        Assert.Contains("\"httpStatus\":200", json);
        Assert.Contains("\"exceptionType\":\"None\"", json);
        Assert.Contains("\"hResult\":0", json);
        Assert.Contains("\"recoveryStatus\":\"NotAttempted\"", json);
        Assert.Contains("\"affectedFeature\":\"Bootstrap\"", json);
        Assert.Contains("\"sequence\":42", json);
        Assert.Contains("\"timestampUtc\":\"2026-10-10T12:00:00.0000000Z\"", json);

        var deserialized = DiagnosticEvent.FromJson(json);
        Assert.NotNull(deserialized);
        Assert.Equal(1, deserialized!.SchemaVersion);
        Assert.Equal(original.EventId, deserialized.EventId);
        Assert.Equal(original.EventName, deserialized.EventName);
        Assert.Equal(original.Severity, deserialized.Severity);
        Assert.Equal(original.Component, deserialized.Component);
        Assert.Equal(original.Operation, deserialized.Operation);
        Assert.Equal(original.Outcome, deserialized.Outcome);
        Assert.Equal(original.FailureCategory, deserialized.FailureCategory);
        Assert.Equal(original.SafeMessage, deserialized.SafeMessage);
        Assert.Equal(original.CorrelationId, deserialized.CorrelationId);
        Assert.Equal(original.DurationMs, deserialized.DurationMs);
        Assert.Equal(original.HttpStatus, deserialized.HttpStatus);
        Assert.Equal(original.ExceptionType, deserialized.ExceptionType);
        Assert.Equal(original.HResult, deserialized.HResult);
        Assert.Equal(original.RecoveryStatus, deserialized.RecoveryStatus);
        Assert.Equal(original.AffectedFeature, deserialized.AffectedFeature);
        Assert.Equal(original.Sequence, deserialized.Sequence);
        Assert.Equal(original.TimestampUtc, deserialized.TimestampUtc);
    }

    [Fact]
    public void Catalog_ContainsStableNumericIdsAndExpectedMetadata()
    {
        // Lifecycle (1001-1006)
        Assert.Equal("StartupStarted", DiagnosticEventCatalog.GetName(DiagnosticEventCatalog.StartupStarted));
        Assert.Equal("StartupCompleted", DiagnosticEventCatalog.GetName(DiagnosticEventCatalog.StartupCompleted));
        Assert.Equal("StartupFailed", DiagnosticEventCatalog.GetName(DiagnosticEventCatalog.StartupFailed));
        Assert.Equal("ShutdownStarted", DiagnosticEventCatalog.GetName(DiagnosticEventCatalog.ShutdownStarted));
        Assert.Equal("ShutdownCompleted", DiagnosticEventCatalog.GetName(DiagnosticEventCatalog.ShutdownCompleted));
        Assert.Equal("UnhandledFailure", DiagnosticEventCatalog.GetName(DiagnosticEventCatalog.UnhandledFailure));

        // WebView & UI (2001-2008)
        Assert.Equal(2001, DiagnosticEventCatalog.WebViewInitializationFailed);
        Assert.Equal(2002, DiagnosticEventCatalog.NavigationFailed);
        Assert.Equal(2003, DiagnosticEventCatalog.WebViewProcessFailed);
        Assert.Equal(2004, DiagnosticEventCatalog.WebViewRecoveryAttempted);
        Assert.Equal(2005, DiagnosticEventCatalog.WebViewRecoverySucceeded);
        Assert.Equal(2006, DiagnosticEventCatalog.WebViewRecoveryExhausted);
        Assert.Equal(2007, DiagnosticEventCatalog.JavaScriptException);
        Assert.Equal(2008, DiagnosticEventCatalog.JavaScriptCaptureRejected);

        // Discovery & RPC (3001-3008)
        Assert.Equal(3001, DiagnosticEventCatalog.DiscoveryFailed);
        Assert.Equal(3002, DiagnosticEventCatalog.DiscoveryRecovered);
        Assert.Equal(3003, DiagnosticEventCatalog.RpcProtocolMismatch);
        Assert.Equal(3004, DiagnosticEventCatalog.TlsHandshakeFailed);
        Assert.Equal(3005, DiagnosticEventCatalog.RpcDeadlineExceeded);
        Assert.Equal(3006, DiagnosticEventCatalog.RpcCallerCancelled);
        Assert.Equal(3007, DiagnosticEventCatalog.RpcBodyIncomplete);
        Assert.Equal(3008, DiagnosticEventCatalog.RpcTransportFailed);

        // Telemetry & Usage (4001-4004)
        Assert.Equal(4001, DiagnosticEventCatalog.TelemetryUnavailable);
        Assert.Equal(4002, DiagnosticEventCatalog.TelemetryRecovered);
        Assert.Equal(4003, DiagnosticEventCatalog.UsagePartialCollection);
        Assert.Equal(4004, DiagnosticEventCatalog.UsageRecovered);

        // Sink Health (5001-5003)
        Assert.Equal(5001, DiagnosticEventCatalog.DiagnosticSinkUnavailable);
        Assert.Equal(5002, DiagnosticEventCatalog.EventsSuppressed);
        Assert.Equal(5003, DiagnosticEventCatalog.DiagnosticSinkRecovered);
    }

    [Fact]
    public void Factory_GeneratesMonotonicSequenceNumbers_InSingleDomain()
    {
        DiagnosticRunContext.ResetForTests();

        var evt1 = DiagnosticEventFactory.Create(
            DiagnosticEventCatalog.StartupStarted, "Bootstrap", "AppStartup", DiagnosticOutcome.Success, DiagnosticFailureCategory.None);
        var evt2 = DiagnosticEventFactory.Create(
            DiagnosticEventCatalog.StartupCompleted, "Bootstrap", "AppStartup", DiagnosticOutcome.Success, DiagnosticFailureCategory.None);

        Assert.True(evt2.Sequence > evt1.Sequence);
        Assert.Equal(evt1.RunId, evt2.RunId);
        Assert.Equal(1, evt1.SchemaVersion);
        Assert.Equal(1, evt2.SchemaVersion);
    }

    [Fact]
    public void Factory_EnforcesPrivacyByConstruction_IgnoresHostileCallerSafeMessage()
    {
        // N1: Treat upstream text as untrusted. Factory uses closed catalog metadata and fixed safe descriptions.
        var evt = DiagnosticEventFactory.Create(
            eventId: DiagnosticEventCatalog.StartupStarted,
            component: "Bootstrap",
            operation: "AppStartup",
            outcome: DiagnosticOutcome.Success,
            failureCategory: DiagnosticFailureCategory.None,
            safeMessage: "Hostile user password=SYN_PRIVATE_PASSWORD at https://secret.com?q=SYN_PRIVATE_TOKEN"
        );

        string json = evt.ToJson();

        // Hostile content is NOT present anywhere in serialized JSON
        Assert.DoesNotContain("SYN_PRIVATE_PASSWORD", json);
        Assert.DoesNotContain("SYN_PRIVATE_TOKEN", json);
        Assert.DoesNotContain("secret.com", json);

        // Safe description from catalog is used
        Assert.Equal("Application startup sequence initiated.", evt.SafeMessage);
    }

    [Fact]
    public void Sanitizer_RedactsSecretsAndUserPaths()
    {
        string input = "User at C:\\Users\\chamidu\\secret.txt with Bearer eyJhbGciOiJIUzI1NiJ9.test and email foo@bar.com and password=secret123";
        string sanitized = DiagnosticSanitizer.SanitizeText(input, maxChars: 500);

        Assert.DoesNotContain("chamidu", sanitized);
        Assert.Contains("[USER_DIR]\\secret.txt", sanitized);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", sanitized);
        Assert.Contains("[REDACTED_TOKEN]", sanitized);
        Assert.DoesNotContain("foo@bar.com", sanitized);
        Assert.Contains("[REDACTED_EMAIL]", sanitized);
        Assert.DoesNotContain("secret123", sanitized);
        Assert.Contains("[REDACTED_SECRET]", sanitized);
    }

    [Fact]
    public void Sanitizer_BoundsExceptionChainsAndRedactsUnallowlistedTypes()
    {
        var inner3 = new InvalidOperationException("Innermost error");
        var inner2 = new HttpRequestException("Middle error", inner3);
        var inner1 = new ArgumentException("Outer error", inner2);
        var top = new Exception("Top error", inner1);

        var (type, hresult, chainStr) = DiagnosticSanitizer.SanitizeException(top);

        Assert.NotNull(chainStr);
        Assert.Contains("ArgumentException", chainStr);
        Assert.Contains("HttpRequestException", chainStr);
        Assert.Contains("InvalidOperationException", chainStr);

        // An unknown custom exception type is replaced with UnknownException (N1.G, N1.H)
        var customEx = new CustomHostileException("Malicious message");
        var (customType, _, _) = DiagnosticSanitizer.SanitizeException(customEx);
        Assert.Equal("UnknownException", customType);
    }

    private sealed class CustomHostileException : Exception
    {
        public CustomHostileException(string msg) : base(msg) { }
    }

    [Fact]
    public void Sanitizer_TruncatesOversizedMessages()
    {
        string massiveMessage = new string('A', 5000);
        string sanitized = DiagnosticSanitizer.SanitizeText(massiveMessage, maxChars: 256);

        Assert.True(sanitized.Length <= 256);
    }

    [Fact]
    public void Privacy_NegativeAssertions_RedactJsonSecrets_QuerySecrets_AbsolutePaths_AndConversationContent()
    {
        // R1: Negative assertions proving sensitive data shapes are redacted
        string jsonSecret = "Payload: {\"password\":\"SYN_PRIVATE_VALUE\"} in config";
        string sanitizedJson = DiagnosticSanitizer.SanitizeText(jsonSecret, maxChars: 500);
        Assert.DoesNotContain("SYN_PRIVATE_VALUE", sanitizedJson);
        Assert.Contains("[REDACTED_SECRET]", sanitizedJson);

        string querySecret = "Request URL: https://example.com/api?q=SYN_PRIVATE_VALUE&token=secret123";
        string sanitizedQuery = DiagnosticSanitizer.SanitizeText(querySecret, maxChars: 500);
        Assert.DoesNotContain("secret123", sanitizedQuery);
        Assert.Contains("[REDACTED_SECRET]", sanitizedQuery);

        string absolutePath = "File access at D:\\SensitiveDir\\SecretSubdir\\keys.pem";
        string sanitizedPath = DiagnosticSanitizer.SanitizeText(absolutePath, maxChars: 500);
        Assert.DoesNotContain("D:\\SensitiveDir\\SecretSubdir\\keys.pem", sanitizedPath);
        Assert.Contains("[REDACTED_PATH]", sanitizedPath);

        string convContent = "Prompt: \"user\": \"Tell me the master API password\" in response";
        string sanitizedConv = DiagnosticSanitizer.SanitizeText(convContent, maxChars: 500);
        Assert.DoesNotContain("Tell me the master API password", sanitizedConv);
        Assert.Contains("[REDACTED_CONVERSATION]", sanitizedConv);

        // Pre-bounding before regex execution prevents ReDoS on massive inputs
        string massiveInput = "Text with " + new string('X', 10000) + " trailing data";
        string sanitizedMassive = DiagnosticSanitizer.SanitizeText(massiveInput, maxChars: 128);
        Assert.True(sanitizedMassive.Length <= 128);
    }

    [Fact]
    public void Identifiers_PreserveUniqueCorrelationAndRunIds_WithoutRedaction()
    {
        // R2: Unique correlation IDs, run IDs, and operation IDs must NOT be replaced
        // by [REDACTED_TOKEN], maintaining causal correlation across cycles.
        string validGuid = "c37d4932-992a-439e-bb83-2ca0ac5521e3";
        string validRunId = "run_20261010_123456_abcdef";
        string validOpId = "op-98765";

        string cleanGuid = DiagnosticSanitizer.SanitizeIdentifier(validGuid);
        string cleanRun = DiagnosticSanitizer.SanitizeIdentifier(validRunId);
        string cleanOp = DiagnosticSanitizer.SanitizeIdentifier(validOpId);

        Assert.Equal(validGuid, cleanGuid);
        Assert.Equal(validRunId, cleanRun);
        Assert.Equal(validOpId, cleanOp);

        var evt = DiagnosticEventFactory.Create(
            DiagnosticEventCatalog.StartupStarted,
            "Bootstrap",
            "AppStartup",
            DiagnosticOutcome.Success,
            DiagnosticFailureCategory.None,
            correlationId: validGuid,
            operationId: validOpId
        );

        Assert.Equal(validGuid, evt.CorrelationId);
        Assert.Equal(validOpId, evt.OperationId);
        Assert.DoesNotContain("[REDACTED_TOKEN]", evt.CorrelationId);
        Assert.DoesNotContain("[REDACTED_TOKEN]", evt.OperationId);

        // Corrupted or dangerous identifier is sanitized safely
        string badId = "id-with-newline\nand-path/../../etc";
        string cleanBad = DiagnosticSanitizer.SanitizeIdentifier(badId);
        Assert.True(DiagnosticSanitizer.IsValidIdentifier(cleanBad));
        Assert.NotEqual(badId, cleanBad);

        // Secret-shaped identifier is rejected (N1)
        string secretId = "password123";
        string cleanSecret = DiagnosticSanitizer.SanitizeIdentifier(secretId);
        Assert.NotEqual(secretId, cleanSecret);
    }

    [Fact]
    public void Event_SkipsSegmentHeader_AndParsesAllValidJsonLines()
    {
        // R8: Line 1 of JSONL files is a valid JSON DiagnosticSegmentHeader record.
        // DiagnosticEvent.FromJson must return null for segmentHeader records and parse events.
        var header = new DiagnosticSegmentHeader
        {
            RecordType = "segmentHeader",
            Format = "AG2Router-Diagnostic-Log-v1",
            SchemaVersion = 1,
            SegmentId = "diag_20261010_120000_0001",
            RunId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
        };
        string headerJson = JsonSerializer.Serialize(header);

        var parsedHeader = DiagnosticEvent.FromJson(headerJson);
        Assert.Null(parsedHeader);

        var evt = DiagnosticEventFactory.Create(
            DiagnosticEventCatalog.StartupStarted,
            "Bootstrap",
            "AppStartup",
            DiagnosticOutcome.Success,
            DiagnosticFailureCategory.None
        );
        string evtJson = evt.ToJson();

        var parsedEvt = DiagnosticEvent.FromJson(evtJson);
        Assert.NotNull(parsedEvt);
        Assert.Equal(DiagnosticEventCatalog.StartupStarted, parsedEvt!.EventId);
        Assert.Equal("Application startup sequence initiated.", parsedEvt.SafeMessage);
    }
}
