namespace AG2Router.Core.Diagnostics;

public enum DiagnosticSeverity
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

public enum DiagnosticOutcome
{
    Success,
    Failure,
    Degraded,
    Partial,
    Cancelled,
    Unknown
}

public enum DiagnosticFailureCategory
{
    None,
    ProtocolMismatch,
    TlsHandshakeFailure,
    ProbeTimeout,
    Cancelled,
    IncompleteResponseBody,
    RejectedRedirect,
    ServerError,
    TransportError,
    DiscoveryError,
    InitializationError,
    NavigationError,
    ProcessCrash,
    UncaughtException,
    ConfigurationError,
    StorageError,
    CapacityExceeded,
    SinkFailure,
    Unknown
}

public enum RecoveryStatus
{
    NotAttempted,
    Attempted,
    Succeeded,
    Failed,
    Exhausted
}

public enum AffectedFeature
{
    Bootstrap,
    Dashboard,
    Discovery,
    RpcTransport,
    Telemetry,
    UsageAccounting,
    Diagnostics
}
