using AG2Router.AG2.Discovery;
using AG2Router.AG2.Rpc;
using AG2Router.App.Diagnostics;
using AG2Router.App.Services;
using AG2Router.Core.Contracts;
using AG2Router.Core.Diagnostics;
using AG2Router.Core.Models;
using Xunit;

namespace AG2Router.Tests.Diagnostics;

public sealed class TestDiagnosticSink : IDiagnosticSink
{
    private readonly List<DiagnosticEvent> _events = new();
    private readonly object _lock = new();

    public void Emit(DiagnosticEvent evt)
    {
        lock (_lock) _events.Add(evt);
    }

    public Task<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public IReadOnlyList<DiagnosticEvent> GetRecentEvents() { lock (_lock) return _events.ToList(); }
    public DiagnosticSinkHealth GetHealth() => new(true, _events.Count, _events.Count, 0, 0, null);
    public bool IsEnabled(DiagnosticSeverity severity) => true;
    public void Dispose() { }
}

public sealed class FailingDiagnosticSink : IDiagnosticSink
{
    public void Emit(DiagnosticEvent evt) => throw new InvalidOperationException("Disk failure simulation");
    public Task<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Flush failure simulation");
    public IReadOnlyList<DiagnosticEvent> GetRecentEvents() => [];
    public DiagnosticSinkHealth GetHealth() => new(false, 0, 0, 0, 0, "Disk failure");
    public bool IsEnabled(DiagnosticSeverity severity) => true;
    public void Dispose() { }
}

public sealed class WiringMockRpcClient : IAG2RpcClient
{
    public Func<int, string, string, Task<ProbeResult>>? ProbeDetailedFunc { get; set; }

    public Task<bool> ProbePortAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public Task<ProbeResult> ProbePortDetailedAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        return ProbeDetailedFunc != null
            ? ProbeDetailedFunc(port, protocol, csrfToken)
            : Task.FromResult(new ProbeResult(true, ProbeFailureReason.None, 200));
    }

    public Task<RawUserStatusResponse?> GetUserStatusAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<RawUserStatusResponse?>(new RawUserStatusResponse());
    }

    public Task<RawTrajectoriesResponse?> GetAllCascadeTrajectoriesAsync(int port, string protocol, string csrfToken, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<RawTrajectoriesResponse?>(new RawTrajectoriesResponse());
    }
}

[Collection("DiagnosticSession")]
public sealed class DiagnosticSubsystemWiringTests
{
    private static readonly DiscoveredProcessRaw ValidProc = new(
        ProcessId: 12345,
        Name: "language_server.exe",
        CommandLine: @"C:\Users\test\AppData\Local\Programs\antigravity\resources\bin\language_server.exe --standalone --override_ide_name antigravity --subclient_type hub --csrf_token 11111111-2222-3333-4444-555555555555",
        ExecutablePath: @"C:\Users\test\AppData\Local\Programs\antigravity\resources\bin\language_server.exe",
        StartTime: DateTime.UtcNow.AddHours(-1)
    );

    [Fact]
    public async Task SyntheticRpcMismatch_EmitsRpcProtocolMismatchDiagnosticEvent()
    {
        var testSink = new TestDiagnosticSink();
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { ValidProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 8000 }),
            IsPidAliveFunc = (_, _) => true
        };

        var rpc = new WiringMockRpcClient
        {
            ProbeDetailedFunc = (port, proto, token) => Task.FromResult(new ProbeResult(false, ProbeFailureReason.ProtocolMismatch, 400))
        };

        var detector = new AG2ProcessDetector(
            inspector: inspector,
            rpcClient: rpc,
            offlineCooldown: TimeSpan.Zero,
            diagnosticSink: testSink
        );

        var result = await detector.DiscoverAsync();

        Assert.Equal("DEGRADED", result.Status);
        var events = testSink.GetRecentEvents();

        // Must record RpcProtocolMismatch
        var mismatchEvent = events.FirstOrDefault(e => e.EventId == DiagnosticEventCatalog.RpcProtocolMismatch);
        Assert.NotNull(mismatchEvent);
        Assert.Equal(DiagnosticFailureCategory.ProtocolMismatch, mismatchEvent!.FailureCategory);
        Assert.Equal(DiagnosticOutcome.Failure, mismatchEvent.Outcome);
        Assert.Equal(AffectedFeature.RpcTransport, mismatchEvent.AffectedFeature);

        // DiscoveryFailed also emitted
        Assert.Contains(events, e => e.EventId == DiagnosticEventCatalog.DiscoveryFailed);
    }

    [Fact]
    public async Task CallerCancelled_EmitsRpcCallerCancelled_DistinctFromDeadlineExceeded()
    {
        var testSink = new TestDiagnosticSink();
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { ValidProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 8000 }),
            IsPidAliveFunc = (_, _) => true
        };

        var rpc = new WiringMockRpcClient
        {
            ProbeDetailedFunc = (port, proto, token) => Task.FromResult(new ProbeResult(false, ProbeFailureReason.Cancelled, 0))
        };

        var detector = new AG2ProcessDetector(inspector, rpc, TimeSpan.Zero, 3, testSink);
        await detector.DiscoverAsync();

        var events = testSink.GetRecentEvents();
        var cancelEvent = events.FirstOrDefault(e => e.EventId == DiagnosticEventCatalog.RpcCallerCancelled);
        Assert.NotNull(cancelEvent);
        Assert.Equal(DiagnosticOutcome.Cancelled, cancelEvent!.Outcome);
        Assert.Equal(DiagnosticFailureCategory.Cancelled, cancelEvent.FailureCategory);
        Assert.Equal(DiagnosticSeverity.Info, cancelEvent.Severity);

        // Test timeout separately
        var timeoutSink = new TestDiagnosticSink();
        rpc.ProbeDetailedFunc = (port, proto, token) => Task.FromResult(new ProbeResult(false, ProbeFailureReason.ProbeTimeout, 0));
        var timeoutDetector = new AG2ProcessDetector(inspector, rpc, TimeSpan.Zero, 3, timeoutSink);
        await timeoutDetector.DiscoverAsync();

        var timeoutEvents = timeoutSink.GetRecentEvents();
        var timeoutEvent = timeoutEvents.FirstOrDefault(e => e.EventId == DiagnosticEventCatalog.RpcDeadlineExceeded);
        Assert.NotNull(timeoutEvent);
        Assert.Equal(DiagnosticOutcome.Failure, timeoutEvent!.Outcome);
        Assert.Equal(DiagnosticFailureCategory.ProbeTimeout, timeoutEvent.FailureCategory);
    }

    [Fact]
    public async Task FailedDiscoveryThenRecovery_EmitsDiscoveryRecovered()
    {
        var testSink = new TestDiagnosticSink();
        var inspector = new MockProcessInspector
        {
            FindProcessesFunc = () => Task.FromResult<IReadOnlyList<DiscoveredProcessRaw>>(new[] { ValidProc }),
            GetListeningPortsFunc = _ => Task.FromResult<IReadOnlyList<int>>(new[] { 8000 }),
            IsPidAliveFunc = (_, _) => true
        };

        bool succeed = false;
        var rpc = new WiringMockRpcClient
        {
            ProbeDetailedFunc = (port, proto, token) => Task.FromResult(
                succeed
                    ? new ProbeResult(true, ProbeFailureReason.None, 200)
                    : new ProbeResult(false, ProbeFailureReason.TransportError, 0))
        };

        var detector = new AG2ProcessDetector(inspector, rpc, TimeSpan.Zero, 3, testSink);

        // Cycle 1: Degraded
        var res1 = await detector.DiscoverAsync();
        Assert.Equal("DEGRADED", res1.Status);
        Assert.Contains(testSink.GetRecentEvents(), e => e.EventId == DiagnosticEventCatalog.DiscoveryFailed);

        // Cycle 2: Recovered
        succeed = true;
        var res2 = await detector.DiscoverAsync();
        Assert.Equal("DISCOVERED", res2.Status);

        var events = testSink.GetRecentEvents();
        var recovered = events.FirstOrDefault(e => e.EventId == DiagnosticEventCatalog.DiscoveryRecovered);
        Assert.NotNull(recovered);
        Assert.Equal(RecoveryStatus.Succeeded, recovered!.RecoveryStatus);
        Assert.Equal(DiagnosticOutcome.Success, recovered.Outcome);
    }

    [Fact]
    public void WebView_RecoveryCycle_CorrelatesViaSharedCorrelationId()
    {
        var testSink = new TestDiagnosticSink();
        DiagnosticSession.Initialize(testSink);

        string corrId = Guid.NewGuid().ToString("N");

        DiagnosticSession.Emit(
            eventId: DiagnosticEventCatalog.WebViewInitializationFailed,
            component: "Dashboard",
            operation: "InitializeWebView",
            outcome: DiagnosticOutcome.Failure,
            failureCategory: DiagnosticFailureCategory.InitializationError,
            safeMessage: "WebView initialization failed.",
            correlationId: corrId
        );

        DiagnosticSession.Emit(
            eventId: DiagnosticEventCatalog.WebViewRecoveryAttempted,
            component: "Dashboard",
            operation: "RecoverWebView",
            outcome: DiagnosticOutcome.Degraded,
            failureCategory: DiagnosticFailureCategory.InitializationError,
            safeMessage: "Attempting recovery.",
            recoveryStatus: RecoveryStatus.Attempted,
            correlationId: corrId
        );

        DiagnosticSession.Emit(
            eventId: DiagnosticEventCatalog.WebViewRecoverySucceeded,
            component: "Dashboard",
            operation: "RecoverWebView",
            outcome: DiagnosticOutcome.Success,
            failureCategory: DiagnosticFailureCategory.None,
            safeMessage: "WebView recovered.",
            recoveryStatus: RecoveryStatus.Succeeded,
            correlationId: corrId
        );

        var events = testSink.GetRecentEvents();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(corrId, e.CorrelationId));

        DiagnosticSession.Reset();
    }

    [Fact]
    public void JsRuntimeDiagnostics_SanitizesSensitiveAndOversizedMessages_WithoutThrowing()
    {
        var testSink = new TestDiagnosticSink();
        DiagnosticSession.Initialize(testSink);

        string massiveSecretPayload = "Error at C:\\Users\\chamidu\\code.js: token=secret_token_12345 " + new string('Z', 2000);

        // Must not throw
        JsRuntimeDiagnostics.RecordError("SyntaxError", massiveSecretPayload);

        var events = testSink.GetRecentEvents();
        Assert.NotEmpty(events);
        var jsEvt = events.Last();
        Assert.Equal(DiagnosticEventCatalog.JavaScriptException, jsEvt.EventId);
        // N1: Arbitrary caller message is excluded from SafeMessage; fixed catalog description is used
        Assert.Equal("JavaScript execution error encountered.", jsEvt.SafeMessage);
        Assert.DoesNotContain("chamidu", jsEvt.SafeMessage);

        DiagnosticSession.Reset();
    }

    [Fact]
    public void LoggingFailure_CannotDisruptDomainOperations()
    {
        // When sink throws on every call
        var failingSink = new FailingDiagnosticSink();
        DiagnosticSession.Initialize(failingSink);

        // DiagnosticSession.Emit must catch and not throw
        var ex1 = Record.Exception(() =>
        {
            DiagnosticSession.Emit(
                eventId: DiagnosticEventCatalog.StartupStarted,
                component: "Test",
                operation: "TestOp",
                outcome: DiagnosticOutcome.Success,
                failureCategory: DiagnosticFailureCategory.None,
                safeMessage: "Test msg"
            );
        });
        Assert.Null(ex1);

        // JsRuntimeDiagnostics must catch and not throw
        var ex2 = Record.Exception(() =>
        {
            JsRuntimeDiagnostics.RecordError("TestKind", "TestDetail");
        });
        Assert.Null(ex2);

        DiagnosticSession.Reset();
    }

    [Fact]
    public void JsRuntimeDiagnostics_AssertNotProductionAppData_BlocksProductionPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var prodPath = System.IO.Path.Combine(localAppData, "AG2-Router", "js_runtime_errors.json");

        Assert.Throws<InvalidOperationException>(() =>
        {
            JsRuntimeDiagnostics.AssertNotProductionAppData(prodPath);
        });

        // Safe temp or test paths should not throw
        var safePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_js_errors.json");
        JsRuntimeDiagnostics.AssertNotProductionAppData(safePath);
        JsRuntimeDiagnostics.AssertNotProductionAppData(":memory:");
    }

    [Fact]
    public void Factory_NeverProducesAllZeroRunId()
    {
        var evt = DiagnosticEventFactory.Create(
            eventId: DiagnosticEventCatalog.StartupStarted,
            component: "Bootstrap",
            operation: "AppStartup",
            outcome: DiagnosticOutcome.Success,
            failureCategory: DiagnosticFailureCategory.None,
            safeMessage: "Test run ID"
        );

        Assert.NotEqual("00000000000000000000000000000000", evt.RunId);
        Assert.False(string.IsNullOrWhiteSpace(evt.RunId));
    }

    [Fact]
    public void PreIngestionSanitization_SanitizesDirectlyConstructedEvents()
    {
        var evt = new DiagnosticEvent
        {
            SchemaVersion = 1,
            TimestampUtc = DateTime.UtcNow.ToString("o"),
            Sequence = 1,
            EventInstanceId = Guid.NewGuid().ToString("N"),
            Severity = DiagnosticSeverity.Error,
            EventId = DiagnosticEventCatalog.UnhandledFailure,
            EventName = "UnhandledFailure",
            Component = "C:\\Users\\chamidu\\Component",
            Operation = "Run",
            RunId = "run-1",
            CorrelationId = "corr-1",
            OperationId = "op-1",
            Outcome = DiagnosticOutcome.Failure,
            FailureCategory = DiagnosticFailureCategory.UncaughtException,
            SafeMessage = "Leak: password=supersecret at C:\\Users\\chamidu\\app.js",
            RecoveryStatus = RecoveryStatus.NotAttempted,
            AffectedFeature = AffectedFeature.Bootstrap
        };

        var sanitized = evt.Sanitize();
        Assert.DoesNotContain("chamidu", sanitized.SafeMessage);
        Assert.DoesNotContain("supersecret", sanitized.SafeMessage);
        Assert.Equal("Unhandled exception in application runtime.", sanitized.SafeMessage);
        Assert.DoesNotContain("chamidu", sanitized.Component);
        Assert.Equal("UnknownComponent", sanitized.Component);
    }

    [Fact]
    public async Task TelemetryPolling_RequiresAffirmativeQuotaTelemetry_ForRecovery_NotMerelyConnected()
    {
        var testSink = new TestDiagnosticSink();
        var mockAdapter = new MockAG2Adapter
        {
            GetStatusFunc = _ => Task.FromResult(new Ag2StatusDto(false, "OFFLINE", null, "Offline")),
            GetQuotaFunc = _ => Task.FromResult<QuotaSnapshotDto?>(null)
        };

        var coordinator = new TelemetryPollingCoordinator(
            mockAdapter,
            interval: TimeSpan.FromMilliseconds(50),
            autoRouter: null,
            log: null,
            diagnosticSink: testSink
        );

        var routerStatus = new RouterStatusDto(
            "IDLE", false, null, null, null, null, "Mock reason",
            new RouterConfigDto(AutoSwitchEnabled: false, LowQuotaThresholdPercent: 15, MinimumCandidateQuotaPercent: 30));

        // 1. Initial status is offline -> emits TelemetryUnavailable
        var offlineStatus = new SystemStatusDto(
            Status: "OFFLINE",
            Ag2: new Ag2StatusDto(false, "OFFLINE", null, "Offline"),
            Router: routerStatus,
            Telemetry: null
        );
        coordinator.TryApplyStatus(1, offlineStatus);

        var events = testSink.GetRecentEvents();
        Assert.Contains(events, e => e.EventId == DiagnosticEventCatalog.TelemetryUnavailable);

        // 2. Loopback daemon connected, but NO usable quota telemetry (Telemetry.Quota is null) -> MUST NOT emit TelemetryRecovered (R9, N6)
        var account = new AccountIdentityDto("user@example.com", "User", "tier", "Tier", DateTime.UtcNow.ToString("o"));
        var connectedWithoutQuota = new SystemStatusDto(
            Status: "CONNECTED",
            Ag2: new Ag2StatusDto(true, "CONNECTED", null, "Connected"),
            Router: routerStatus,
            Telemetry: new TelemetryDto(account, null, null, null, null)
        );
        coordinator.TryApplyStatus(2, connectedWithoutQuota);

        events = testSink.GetRecentEvents();
        Assert.DoesNotContain(events, e => e.EventId == DiagnosticEventCatalog.TelemetryRecovered);

        // 3. Quota present but has empty models and null credits -> empty observation, MUST NOT emit TelemetryRecovered (N6)
        var emptyQuota = new QuotaSnapshotDto(DateTime.UtcNow.ToString("o"), new List<ModelQuotaDto>(), null, null);
        var connectedWithEmptyQuota = new SystemStatusDto(
            Status: "CONNECTED",
            Ag2: new Ag2StatusDto(true, "CONNECTED", null, "Connected"),
            Router: routerStatus,
            Telemetry: new TelemetryDto(account, emptyQuota, null, null, DateTime.UtcNow.ToString("o"))
        );
        coordinator.TryApplyStatus(3, connectedWithEmptyQuota);

        events = testSink.GetRecentEvents();
        Assert.DoesNotContain(events, e => e.EventId == DiagnosticEventCatalog.TelemetryRecovered);

        // 4. Quota present but all models have null fractions -> all-unknown observation, MUST NOT emit TelemetryRecovered (N6)
        var unknownModel = new ModelQuotaDto("gpt-4", "GPT-4", null, null, false);
        var unknownQuota = new QuotaSnapshotDto(DateTime.UtcNow.ToString("o"), new[] { unknownModel }, null, null);
        var connectedWithUnknownQuota = new SystemStatusDto(
            Status: "CONNECTED",
            Ag2: new Ag2StatusDto(true, "CONNECTED", null, "Connected"),
            Router: routerStatus,
            Telemetry: new TelemetryDto(account, unknownQuota, null, null, DateTime.UtcNow.ToString("o"))
        );
        coordinator.TryApplyStatus(4, connectedWithUnknownQuota);

        events = testSink.GetRecentEvents();
        Assert.DoesNotContain(events, e => e.EventId == DiagnosticEventCatalog.TelemetryRecovered);

        // 5. Meaningful usable quota evidence arrives (valid numerical fraction or explicitly observed zero) -> EMITS TelemetryRecovered (N6)
        var validModel = new ModelQuotaDto("gpt-4", "GPT-4", 0.85, "2026-10-10T12:00:00Z", false);
        var usableQuota = new QuotaSnapshotDto(DateTime.UtcNow.ToString("o"), new[] { validModel }, null, null);
        var connectedWithUsableQuota = new SystemStatusDto(
            Status: "CONNECTED",
            Ag2: new Ag2StatusDto(true, "CONNECTED", null, "Connected"),
            Router: routerStatus,
            Telemetry: new TelemetryDto(account, usableQuota, null, 85.0, DateTime.UtcNow.ToString("o"))
        );
        coordinator.TryApplyStatus(5, connectedWithUsableQuota);

        events = testSink.GetRecentEvents();
        var recovered = events.FirstOrDefault(e => e.EventId == DiagnosticEventCatalog.TelemetryRecovered);
        Assert.NotNull(recovered);
        Assert.Equal(DiagnosticOutcome.Success, recovered!.Outcome);
        Assert.Equal(RecoveryStatus.Succeeded, recovered.RecoveryStatus);

        await coordinator.DisposeAsync();
    }

    [Fact]
    public void UsageCollection_EmitsTypedFailureCategory_AndRequiresHealthyInstancesForRecovery()
    {
        // R9: UsageCollectorDiagnostics carries typed FailureCategory; recovery requires InstancesHealthy > 0
        var testSink = new TestDiagnosticSink();
        DiagnosticSession.Initialize(testSink);

        // Degraded cycle with StorageError
        var diagDegraded = new UsageCollectorDiagnostics
        {
            BaselineEstablished = false,
            LastCollectionAttemptAtUtc = DateTimeOffset.UtcNow,
            LastSuccessfulCollectionAtUtc = null,
            LastError = "A usage accounting conflict was detected.",
            InstancesDiscovered = 1,
            InstancesHealthy = 1,
            IntegrityAvailable = false,
            FailureCategory = DiagnosticFailureCategory.StorageError
        };

        // Emit via DiagnosticSession
        DiagnosticSession.Emit(
            eventId: DiagnosticEventCatalog.UsagePartialCollection,
            component: "UsageAccounting",
            operation: "CollectUsageCycle",
            outcome: DiagnosticOutcome.Degraded,
            failureCategory: diagDegraded.FailureCategory,
            safeMessage: diagDegraded.LastError,
            severity: DiagnosticSeverity.Warning,
            affectedFeature: AffectedFeature.UsageAccounting
        );

        var events = testSink.GetRecentEvents();
        var degEvent = events.FirstOrDefault(e => e.EventId == DiagnosticEventCatalog.UsagePartialCollection);
        Assert.NotNull(degEvent);
        Assert.Equal(DiagnosticFailureCategory.StorageError, degEvent!.FailureCategory);

        DiagnosticSession.Reset();
    }

    [Fact]
    public void ManualWebViewRecovery_EstablishesDistinctCorrelatedCycle()
    {
        // R10: Manual retry initiates distinct correlated recovery cycle ID
        var testSink = new TestDiagnosticSink();
        DiagnosticSession.Initialize(testSink);

        string manualCycleId = Guid.NewGuid().ToString("N");

        DiagnosticSession.Emit(
            eventId: DiagnosticEventCatalog.WebViewRecoveryAttempted,
            component: "Dashboard",
            operation: "ManualRetry",
            outcome: DiagnosticOutcome.Degraded,
            failureCategory: DiagnosticFailureCategory.None,
            safeMessage: "Manual recovery initiated by user.",
            recoveryStatus: RecoveryStatus.Attempted,
            correlationId: manualCycleId,
            severity: DiagnosticSeverity.Info
        );

        DiagnosticSession.Emit(
            eventId: DiagnosticEventCatalog.WebViewRecoverySucceeded,
            component: "Dashboard",
            operation: "ManualRetry",
            outcome: DiagnosticOutcome.Success,
            failureCategory: DiagnosticFailureCategory.None,
            safeMessage: "Manual retry succeeded.",
            recoveryStatus: RecoveryStatus.Succeeded,
            correlationId: manualCycleId,
            severity: DiagnosticSeverity.Info
        );

        var events = testSink.GetRecentEvents();
        Assert.Equal(2, events.Count);
        Assert.Equal(manualCycleId, events[0].CorrelationId);
        Assert.Equal(manualCycleId, events[1].CorrelationId);
        Assert.Equal(RecoveryStatus.Attempted, events[0].RecoveryStatus);
        Assert.Equal(RecoveryStatus.Succeeded, events[1].RecoveryStatus);

        DiagnosticSession.Reset();
    }
}
