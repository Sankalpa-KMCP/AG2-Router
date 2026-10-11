namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Factory for constructing typed, schema-validated diagnostic events with allowlisted fields,
/// privacy-by-construction enforcement (N1), and single run-scoped sequence domain (N5).
/// </summary>
public static class DiagnosticEventFactory
{
    public static DiagnosticEvent Create(
        int eventId,
        string component,
        string operation,
        DiagnosticOutcome outcome,
        DiagnosticFailureCategory failureCategory,
        string? safeMessage = null,
        RecoveryStatus recoveryStatus = RecoveryStatus.NotAttempted,
        string? runId = null,
        string? correlationId = null,
        string? operationId = null,
        long? sequence = null,
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
        string? nextActionCode = null,
        string? applicationVersion = "0.4.1-rc.4",
        string? sourceCommit = null)
    {
        // RunId is single-owner run-scoped (N1.D, N5)
        string effectiveRunId = DiagnosticRunContext.RunId;

        // Correlation and operation IDs must be valid identifiers without caller-injected private data (N1, N2)
        string effectiveCorrelationId = DiagnosticSanitizer.SanitizeIdentifier(correlationId);
        string effectiveOperationId = DiagnosticSanitizer.SanitizeIdentifier(operationId);
        string? effectiveParentOpId = !string.IsNullOrWhiteSpace(parentOperationId)
            ? DiagnosticSanitizer.SanitizeIdentifier(parentOperationId)
            : null;

        // Privacy by construction: use closed catalog metadata and fixed safe descriptions (N1.B, N1.F)
        string fixedSafeMessage = DiagnosticEventCatalog.GetSafeDescription(eventId);

        var evt = new DiagnosticEvent
        {
            SchemaVersion = DiagnosticEvent.CurrentSchemaVersion,
            RecordType = "event",
            TimestampUtc = DateTime.UtcNow.ToString("o"),
            Sequence = sequence ?? DiagnosticRunContext.NextSequence(),
            EventInstanceId = Guid.NewGuid().ToString("N"),
            Severity = severity ?? DiagnosticEventCatalog.GetDefaultSeverity(eventId),
            EventId = eventId,
            EventName = DiagnosticEventCatalog.GetName(eventId),
            Component = DiagnosticSanitizer.SanitizeComponent(component),
            Operation = DiagnosticSanitizer.SanitizeOperation(operation),
            RunId = effectiveRunId,
            CorrelationId = effectiveCorrelationId,
            OperationId = effectiveOperationId,
            Outcome = outcome,
            FailureCategory = failureCategory,
            SafeMessage = fixedSafeMessage,
            RecoveryStatus = recoveryStatus,
            AffectedFeature = affectedFeature ?? DiagnosticEventCatalog.GetDefaultFeature(eventId),
            ParentOperationId = effectiveParentOpId,
            CauseEventId = causeEventId,
            DurationMs = durationMs,
            Attempt = attempt,
            Protocol = DiagnosticSanitizer.SanitizeProtocol(protocol),
            HttpStatus = httpStatus,
            FailurePhase = DiagnosticSanitizer.SanitizeFailurePhase(failurePhase),
            ExceptionType = DiagnosticSanitizer.SanitizeSymbol(exceptionType, 64),
            HResult = hresult,
            OriginSymbol = DiagnosticSanitizer.SanitizeSymbol(originSymbol, 64),
            NextActionCode = DiagnosticSanitizer.SanitizeSymbol(nextActionCode, 64),
            ApplicationVersion = DiagnosticSanitizer.SanitizeVersion(applicationVersion),
            SourceCommit = DiagnosticSanitizer.SanitizeCommit(sourceCommit)
        };

        // Apply complete validation BEFORE retention, enqueue, or persistence (N1.I)
        return evt.Sanitize();
    }
}
