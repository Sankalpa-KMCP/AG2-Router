using AG2Router.Core.Diagnostics;

namespace AG2Router.App.Diagnostics;

/// <summary>
/// Ambient runtime session holding the active diagnostic sink and delegating to the single
/// run-scoped monotonic sequence domain and run ID owner (DiagnosticRunContext, N5).
/// Provides safe event creation helpers and pre-admission buffering with persistent promotion (R11).
/// </summary>
public static class DiagnosticSession
{
    private static IDiagnosticSink _currentSink = NullDiagnosticSink.Instance;

    public static string RunId => DiagnosticRunContext.RunId;
    public static string ApplicationVersion { get; set; } = "0.4.1-rc.4";
    public static string? SourceCommit { get; set; }

    public static IDiagnosticSink Current
    {
        get => Volatile.Read(ref _currentSink);
        set => Volatile.Write(ref _currentSink, value ?? NullDiagnosticSink.Instance);
    }

    public static MemoryDiagnosticSink InitializePreAdmission(int capacity = 256)
    {
        var memSink = new MemoryDiagnosticSink(capacity);
        Current = memSink;
        return memSink;
    }

    public static void Initialize(IDiagnosticSink sink, string? runId = null)
    {
        Current = sink;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            DiagnosticRunContext.ResetForTests(runId);
        }
    }

    public static void PromoteToPersistent(IDiagnosticSink persistentSink)
    {
        ArgumentNullException.ThrowIfNull(persistentSink);
        var priorSink = Current;
        if (priorSink is MemoryDiagnosticSink memSink)
        {
            var buffered = memSink.GetRecentEvents();
            foreach (var evt in buffered)
            {
                try
                {
                    persistentSink.Emit(evt);
                }
                catch { }
            }
        }
        Current = persistentSink;
    }

    public static void Reset()
    {
        Current = NullDiagnosticSink.Instance;
        DiagnosticRunContext.ResetForTests();
    }

    public static IReadOnlyList<DiagnosticEvent> GetRecentEvents() => Current.GetRecentEvents();
    public static DiagnosticSinkHealth GetHealth() => Current.GetHealth();

    public static long NextSequence() => DiagnosticRunContext.NextSequence();

    public static DiagnosticEvent CreateEvent(
        int eventId,
        string component,
        string operation,
        DiagnosticOutcome outcome,
        DiagnosticFailureCategory failureCategory,
        string? safeMessage = null,
        RecoveryStatus recoveryStatus = RecoveryStatus.NotAttempted,
        string? correlationId = null,
        string? operationId = null,
        DiagnosticSeverity? severity = null,
        AffectedFeature? affectedFeature = null,
        string? parentOperationId = null,
        int? causeEventId = null,
        double? durationMs = null,
        int? attempt = null,
        string? protocol = null,
        int? httpStatus = null,
        string? failurePhase = null,
        string? exceptionType = null,
        int? hresult = null,
        string? originSymbol = null,
        string? nextActionCode = null)
    {
        return DiagnosticEventFactory.Create(
            eventId: eventId,
            component: component,
            operation: operation,
            outcome: outcome,
            failureCategory: failureCategory,
            safeMessage: safeMessage,
            recoveryStatus: recoveryStatus,
            runId: RunId,
            correlationId: correlationId,
            operationId: operationId,
            sequence: NextSequence(),
            severity: severity,
            affectedFeature: affectedFeature,
            parentOperationId: parentOperationId,
            causeEventId: causeEventId,
            durationMs: durationMs,
            attempt: attempt,
            protocol: protocol,
            httpStatus: httpStatus,
            failurePhase: failurePhase,
            exceptionType: exceptionType,
            hresult: hresult,
            originSymbol: originSymbol,
            nextActionCode: nextActionCode,
            applicationVersion: ApplicationVersion,
            sourceCommit: SourceCommit
        );
    }

    public static void Emit(
        int eventId,
        string component,
        string operation,
        DiagnosticOutcome outcome,
        DiagnosticFailureCategory failureCategory,
        string? safeMessage = null,
        RecoveryStatus recoveryStatus = RecoveryStatus.NotAttempted,
        string? correlationId = null,
        string? operationId = null,
        DiagnosticSeverity? severity = null,
        AffectedFeature? affectedFeature = null,
        string? parentOperationId = null,
        int? causeEventId = null,
        double? durationMs = null,
        int? attempt = null,
        string? protocol = null,
        int? httpStatus = null,
        string? failurePhase = null,
        string? exceptionType = null,
        int? hresult = null,
        string? originSymbol = null,
        string? nextActionCode = null)
    {
        var evt = CreateEvent(
            eventId, component, operation, outcome, failureCategory, safeMessage,
            recoveryStatus, correlationId, operationId, severity, affectedFeature,
            parentOperationId, causeEventId, durationMs, attempt, protocol,
            httpStatus, failurePhase, exceptionType, hresult, originSymbol, nextActionCode);

        try
        {
            Current.Emit(evt);
        }
        catch
        {
            // Defensive: logging failure must never disrupt application execution
        }
    }
}
