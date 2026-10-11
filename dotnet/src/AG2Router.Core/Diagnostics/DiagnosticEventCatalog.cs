namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Closed event catalog defining stable numeric IDs, names, and default classification.
/// </summary>
public static class DiagnosticEventCatalog
{
    // 1000-1999: Application Bootstrap & Lifecycle
    public const int StartupStarted = 1001;
    public const int StartupCompleted = 1002;
    public const int StartupFailed = 1003;
    public const int UnhandledFailure = 1004;
    public const int ShutdownStarted = 1005;
    public const int ShutdownCompleted = 1006;

    // 2000-2999: WebView2 & JavaScript Diagnostics
    public const int WebViewInitializationFailed = 2001;
    public const int NavigationFailed = 2002;
    public const int WebViewProcessFailed = 2003;
    public const int WebViewRecoveryAttempted = 2004;
    public const int WebViewRecoverySucceeded = 2005;
    public const int WebViewRecoveryExhausted = 2006;
    public const int JavaScriptException = 2007;
    public const int JavaScriptCaptureRejected = 2008;

    // 3000-3999: Antigravity Discovery & RPC Transport
    public const int DiscoveryFailed = 3001;
    public const int DiscoveryRecovered = 3002;
    public const int RpcProtocolMismatch = 3003;
    public const int TlsHandshakeFailed = 3004;
    public const int RpcDeadlineExceeded = 3005;
    public const int RpcCallerCancelled = 3006;
    public const int RpcBodyIncomplete = 3007;
    public const int RpcTransportFailed = 3008;
    public const int RpcRejectedRedirect = 3009;
    public const int RpcServerError = 3010;
    public const int RpcUnknownFailure = 3011;

    // 4000-4999: Telemetry & Usage Accounting
    public const int TelemetryUnavailable = 4001;
    public const int TelemetryRecovered = 4002;
    public const int UsagePartialCollection = 4003;
    public const int UsageRecovered = 4004;

    // 5000-5999: Diagnostic Sink Health
    public const int DiagnosticSinkUnavailable = 5001;
    public const int EventsSuppressed = 5002;
    public const int DiagnosticSinkRecovered = 5003;

    public static string GetName(int eventId) => eventId switch
    {
        StartupStarted => nameof(StartupStarted),
        StartupCompleted => nameof(StartupCompleted),
        StartupFailed => nameof(StartupFailed),
        UnhandledFailure => nameof(UnhandledFailure),
        ShutdownStarted => nameof(ShutdownStarted),
        ShutdownCompleted => nameof(ShutdownCompleted),

        WebViewInitializationFailed => nameof(WebViewInitializationFailed),
        NavigationFailed => nameof(NavigationFailed),
        WebViewProcessFailed => nameof(WebViewProcessFailed),
        WebViewRecoveryAttempted => nameof(WebViewRecoveryAttempted),
        WebViewRecoverySucceeded => nameof(WebViewRecoverySucceeded),
        WebViewRecoveryExhausted => nameof(WebViewRecoveryExhausted),
        JavaScriptException => nameof(JavaScriptException),
        JavaScriptCaptureRejected => nameof(JavaScriptCaptureRejected),

        DiscoveryFailed => nameof(DiscoveryFailed),
        DiscoveryRecovered => nameof(DiscoveryRecovered),
        RpcProtocolMismatch => nameof(RpcProtocolMismatch),
        TlsHandshakeFailed => nameof(TlsHandshakeFailed),
        RpcDeadlineExceeded => nameof(RpcDeadlineExceeded),
        RpcCallerCancelled => nameof(RpcCallerCancelled),
        RpcBodyIncomplete => nameof(RpcBodyIncomplete),
        RpcTransportFailed => nameof(RpcTransportFailed),
        RpcRejectedRedirect => nameof(RpcRejectedRedirect),
        RpcServerError => nameof(RpcServerError),
        RpcUnknownFailure => nameof(RpcUnknownFailure),

        TelemetryUnavailable => nameof(TelemetryUnavailable),
        TelemetryRecovered => nameof(TelemetryRecovered),
        UsagePartialCollection => nameof(UsagePartialCollection),
        UsageRecovered => nameof(UsageRecovered),

        DiagnosticSinkUnavailable => nameof(DiagnosticSinkUnavailable),
        EventsSuppressed => nameof(EventsSuppressed),
        DiagnosticSinkRecovered => nameof(DiagnosticSinkRecovered),

        _ => "UnknownDiagnosticEvent"
    };

    public static DiagnosticSeverity GetDefaultSeverity(int eventId) => eventId switch
    {
        StartupStarted => DiagnosticSeverity.Info,
        StartupCompleted => DiagnosticSeverity.Info,
        StartupFailed => DiagnosticSeverity.Critical,
        UnhandledFailure => DiagnosticSeverity.Critical,
        ShutdownStarted => DiagnosticSeverity.Info,
        ShutdownCompleted => DiagnosticSeverity.Info,

        WebViewInitializationFailed => DiagnosticSeverity.Warning,
        NavigationFailed => DiagnosticSeverity.Warning,
        WebViewProcessFailed => DiagnosticSeverity.Error,
        WebViewRecoveryAttempted => DiagnosticSeverity.Info,
        WebViewRecoverySucceeded => DiagnosticSeverity.Info,
        WebViewRecoveryExhausted => DiagnosticSeverity.Error,
        JavaScriptException => DiagnosticSeverity.Warning,
        JavaScriptCaptureRejected => DiagnosticSeverity.Warning,

        DiscoveryFailed => DiagnosticSeverity.Warning,
        DiscoveryRecovered => DiagnosticSeverity.Info,
        RpcProtocolMismatch => DiagnosticSeverity.Warning,
        TlsHandshakeFailed => DiagnosticSeverity.Warning,
        RpcDeadlineExceeded => DiagnosticSeverity.Warning,
        RpcCallerCancelled => DiagnosticSeverity.Info,
        RpcBodyIncomplete => DiagnosticSeverity.Warning,
        RpcTransportFailed => DiagnosticSeverity.Warning,
        RpcRejectedRedirect => DiagnosticSeverity.Warning,
        RpcServerError => DiagnosticSeverity.Warning,

        TelemetryUnavailable => DiagnosticSeverity.Warning,
        TelemetryRecovered => DiagnosticSeverity.Info,
        UsagePartialCollection => DiagnosticSeverity.Warning,
        UsageRecovered => DiagnosticSeverity.Info,

        DiagnosticSinkUnavailable => DiagnosticSeverity.Error,
        EventsSuppressed => DiagnosticSeverity.Warning,
        DiagnosticSinkRecovered => DiagnosticSeverity.Info,

        _ => DiagnosticSeverity.Warning
    };

    public static AffectedFeature GetDefaultFeature(int eventId) => eventId switch
    {
        >= 1000 and < 2000 => AffectedFeature.Bootstrap,
        >= 2000 and < 3000 => AffectedFeature.Dashboard,
        >= 3000 and < 3003 => AffectedFeature.Discovery,
        >= 3003 and < 4000 => AffectedFeature.RpcTransport,
        >= 4000 and < 4003 => AffectedFeature.Telemetry,
        >= 4003 and < 5000 => AffectedFeature.UsageAccounting,
        >= 5000 and < 6000 => AffectedFeature.Diagnostics,
        _ => AffectedFeature.Bootstrap
    };

    public static string GetSafeDescription(int eventId) => eventId switch
    {
        StartupStarted => "Application startup sequence initiated.",
        StartupCompleted => "Application startup completed successfully.",
        StartupFailed => "Application startup failed.",
        UnhandledFailure => "Unhandled exception in application runtime.",
        ShutdownStarted => "Application shutdown sequence initiated.",
        ShutdownCompleted => "Application shutdown completed successfully.",

        WebViewInitializationFailed => "WebView2 environment initialization failed.",
        NavigationFailed => "WebView2 navigation failed.",
        WebViewProcessFailed => "WebView2 process exited unexpectedly.",
        WebViewRecoveryAttempted => "WebView2 recovery attempt initiated.",
        WebViewRecoverySucceeded => "WebView2 recovery succeeded.",
        WebViewRecoveryExhausted => "WebView2 recovery attempts exhausted.",
        JavaScriptException => "JavaScript execution error encountered.",
        JavaScriptCaptureRejected => "JavaScript diagnostics capture rejected.",

        DiscoveryFailed => "Antigravity process discovery failed.",
        DiscoveryRecovered => "Antigravity process discovery recovered.",
        RpcProtocolMismatch => "RPC protocol mismatch detected.",
        TlsHandshakeFailed => "TLS handshake failed.",
        RpcDeadlineExceeded => "RPC probe deadline exceeded.",
        RpcCallerCancelled => "RPC probe cancelled by caller.",
        RpcBodyIncomplete => "RPC response body incomplete.",
        RpcTransportFailed => "RPC transport connection failed.",
        RpcRejectedRedirect => "RPC probe received unexpected redirect.",
        RpcServerError => "RPC probe received server error response.",
        RpcUnknownFailure => "Antigravity RPC port probe encountered an unknown failure.",

        TelemetryUnavailable => "Telemetry polling unavailable or degraded.",
        TelemetryRecovered => "Usable quota telemetry recovered successfully.",
        UsagePartialCollection => "Usage collection degraded or incomplete.",
        UsageRecovered => "Usage collection recovered successfully.",

        DiagnosticSinkUnavailable => "Diagnostic sink unavailable.",
        EventsSuppressed => "Diagnostic events suppressed due to queue pressure.",
        DiagnosticSinkRecovered => "Diagnostic sink recovered.",

        _ => "Diagnostic event recorded."
    };
}

public static class DiagnosticComponents
{
    public const string App = "App";
    public const string Bootstrap = "Bootstrap";
    public const string Dashboard = "Dashboard";
    public const string Discovery = "Discovery";
    public const string Rpc = "Rpc";
    public const string Telemetry = "Telemetry";
    public const string Usage = "Usage";
    public const string UsageAccounting = "UsageAccounting";
    public const string UsageDiscovery = "UsageDiscovery";
    public const string WebView = "WebView";
    public const string Diagnostics = "Diagnostics";
}

public static class DiagnosticOperations
{
    public const string AppStartup = "AppStartup";
    public const string AppShutdown = "AppShutdown";
    public const string AppDomainUnhandledException = "AppDomainUnhandledException";
    public const string DispatcherUnhandledException = "DispatcherUnhandledException";
    public const string DiscoverProcess = "DiscoverProcess";
    public const string ProbeConnectRpcPort = "ProbeConnectRpcPort";
    public const string CollectUsageCycle = "CollectUsageCycle";
    public const string PollTelemetry = "PollTelemetry";
    public const string CreateWebView2Environment = "CreateWebView2Environment";
    public const string RecoverWebView2Environment = "RecoverWebView2Environment";
    public const string InitializeWebView = "InitializeWebView";
    public const string RecoverWebView = "RecoverWebView";
    public const string WebViewProcessExited = "WebViewProcessExited";
    public const string WebViewNavigation = "WebViewNavigation";
    public const string ManualRetry = "ManualRetry";
    public const string CaptureJavaScriptError = "CaptureJavaScriptError";
    public const string JavaScriptRuntimeError = "JavaScriptRuntimeError";
    public const string SinkFlush = "SinkFlush";
    public const string SinkRetention = "SinkRetention";
}

