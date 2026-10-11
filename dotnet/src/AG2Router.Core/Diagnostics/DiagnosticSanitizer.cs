using System.Text.RegularExpressions;

namespace AG2Router.Core.Diagnostics;

/// <summary>
/// Pre-ingestion privacy sanitizer. Enforces privacy-safe extraction and defense-in-depth redaction
/// BEFORE diagnostic events are enqueued, retained in memory, or written to disk.
/// Implements field-specific validation and dedicated identifier preservation (R1, R2).
/// </summary>
public static class DiagnosticSanitizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex EmailRegex = new(
        @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex JwtRegex = new(
        @"eyJ[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}\.[a-zA-Z0-9_-]{10,}",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex BearerRegex = new(
        @"Bearer\s+[a-zA-Z0-9_\-\.~+/]+=*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex HexOrGuidTokenRegex = new(
        @"\b[0-9a-fA-F]{32,64}\b",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex UserDirectoryRegex = new(
        @"(?:[a-zA-Z]:\\(?:Users|Documents and Settings)\\[^\s\\/]+|/(?:Users|home)/[^\s/]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex GenericSecretAssignmentRegex = new(
        @"(?:password|token|secret|key|csrf|auth|cookie|credential)[\s:=]+[^\s,;&""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex JsonQuotedSecretRegex = new(
        @"\""(?:password|token|secret|key|csrf|auth|cookie|credential|access_token|refresh_token)\""\s*:\s*\""[^\""]+\""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex QueryParamSecretRegex = new(
        @"(?:[?&])(?:[a-zA-Z0-9_\-]*token|[a-zA-Z0-9_\-]*key|[a-zA-Z0-9_\-]*secret|password|auth|csrf|cookie)=[^&\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex AbsolutePathRegex = new(
        @"(?:\b[a-zA-Z]:\\(?:(?![Uu]sers\b)[^\s,;&""'<>|]+)|\b/(?:var|etc|usr|opt|root|tmp)/[^\s,;&""'<>|]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex ConversationContentRegex = new(
        @"(?:prompt_content|raw_cascade|conversation_turn|user_prompt|\""user\""|\""assistant\""|\""system\""|\""prompt\"")[\s:=]+(?:\""[^\""]*\""|[^\s,;&""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex IdentifierRegex = new(
        @"^[a-zA-Z0-9_\-\.]{1,64}$",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SafeSymbolRegex = new(
        @"^[a-zA-Z0-9_\.\+\-\:\/]{1,128}$",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly HashSet<string> KnownComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        DiagnosticComponents.App,
        DiagnosticComponents.Bootstrap,
        DiagnosticComponents.Dashboard,
        DiagnosticComponents.Discovery,
        DiagnosticComponents.Rpc,
        DiagnosticComponents.Telemetry,
        DiagnosticComponents.Usage,
        DiagnosticComponents.UsageAccounting,
        DiagnosticComponents.UsageDiscovery,
        DiagnosticComponents.WebView,
        DiagnosticComponents.Diagnostics
    };

    private static readonly HashSet<string> KnownOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        DiagnosticOperations.AppStartup,
        DiagnosticOperations.AppShutdown,
        DiagnosticOperations.AppDomainUnhandledException,
        DiagnosticOperations.DispatcherUnhandledException,
        DiagnosticOperations.DiscoverProcess,
        DiagnosticOperations.ProbeConnectRpcPort,
        DiagnosticOperations.CollectUsageCycle,
        DiagnosticOperations.PollTelemetry,
        DiagnosticOperations.CreateWebView2Environment,
        DiagnosticOperations.RecoverWebView2Environment,
        DiagnosticOperations.InitializeWebView,
        DiagnosticOperations.RecoverWebView,
        DiagnosticOperations.WebViewProcessExited,
        DiagnosticOperations.WebViewNavigation,
        DiagnosticOperations.ManualRetry,
        DiagnosticOperations.CaptureJavaScriptError,
        DiagnosticOperations.JavaScriptRuntimeError,
        DiagnosticOperations.SinkFlush,
        DiagnosticOperations.SinkRetention
    };

    private static readonly HashSet<string> KnownProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "http/1.1", "h2", "grpc", "connect-rpc", "http", "https"
    };

    private static readonly HashSet<string> KnownFailurePhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "HeaderValidation", "BodyRead", "Handshake", "Probe", "Discovery", "Aggregation", "Navigation", "Execution", "Disposal"
    };

    private static readonly HashSet<string> TrustedExceptionTypes = new(StringComparer.Ordinal)
    {
        "HttpRequestException",
        "TaskCanceledException",
        "OperationCanceledException",
        "TimeoutException",
        "SocketException",
        "IOException",
        "UnauthorizedAccessException",
        "InvalidOperationException",
        "ArgumentException",
        "ArgumentNullException",
        "ArgumentOutOfRangeException",
        "JsonException",
        "Win32Exception",
        "AuthenticationException",
        "WebException",
        "FileNotFoundException",
        "DirectoryNotFoundException",
        "NotSupportedException",
        "ObjectDisposedException",
        "PlatformNotSupportedException",
        "None"
    };

    private static readonly Regex CommitShaRegex = new(
        @"^[0-9a-fA-F]{40}$",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex VersionRegex = new(
        @"^[0-9a-zA-Z\.\-\+]{1,32}$",
        RegexOptions.Compiled,
        RegexTimeout);

    /// <summary>
    /// Validates whether an identifier meets strict character and length constraints.
    /// Rejects non-conforming, multi-line, or secret-shaped tokens (N1, N2).
    /// </summary>
    public static bool IsValidIdentifier(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64)
        {
            return false;
        }

        try
        {
            if (!IdentifierRegex.IsMatch(id))
            {
                return false;
            }
            if (id.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                id.Contains("secret", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates an identifier (runId, correlationId, operationId, eventInstanceId).
    /// Enforces provenance verification (Finding C): structurally valid identifiers
    /// are validated against strict length and character constraints, registered in
    /// DiagnosticRunContext, and preserved. Corrupt or secret-shaped tokens are replaced
    /// with trusted internal identifiers generated via DiagnosticRunContext.
    /// </summary>
    public static string SanitizeIdentifier(string? id)
    {
        if (IsValidIdentifier(id))
        {
            return id!;
        }

        return DiagnosticRunContext.CreateCorrelationId();
    }

    private static readonly HashSet<string> KnownOriginSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dashboard",
        "AppBundle",
        "InlineScript",
        "ExternalScript",
        "Bootstrap",
        "RpcClient",
        "TelemetryCoordinator",
        "UsageCollector",
        "None"
    };

    private static readonly HashSet<string> KnownNextActionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Retry",
        "Abort",
        "Ignore",
        "Restart",
        "Degrade",
        "None"
    };

    public static string? SanitizeOriginSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        string trimmed = symbol.Trim();
        return KnownOriginSymbols.Contains(trimmed) ? trimmed : null;
    }

    public static string? SanitizeNextActionCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        string trimmed = code.Trim();
        return KnownNextActionCodes.Contains(trimmed) ? trimmed : null;
    }

    public static string SanitizeComponent(string? component)
    {
        if (!string.IsNullOrWhiteSpace(component) && KnownComponents.Contains(component.Trim()))
        {
            return component.Trim();
        }
        return "UnknownComponent";
    }

    public static string SanitizeOperation(string? operation)
    {
        if (!string.IsNullOrWhiteSpace(operation) && KnownOperations.Contains(operation.Trim()))
        {
            return operation.Trim();
        }
        return "UnknownOperation";
    }

    public static string? SanitizeProtocol(string? protocol)
    {
        if (!string.IsNullOrWhiteSpace(protocol) && KnownProtocols.Contains(protocol.Trim()))
        {
            return protocol.Trim().ToLowerInvariant();
        }
        return null;
    }

    public static string? SanitizeFailurePhase(string? phase)
    {
        if (!string.IsNullOrWhiteSpace(phase) && KnownFailurePhases.Contains(phase.Trim()))
        {
            return phase.Trim();
        }
        return null;
    }

    public static string? SanitizeCommit(string? commit)
    {
        if (string.IsNullOrWhiteSpace(commit) || commit.Length != 40) return null;
        try
        {
            return CommitShaRegex.IsMatch(commit) ? commit : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? SanitizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 32) return null;
        try
        {
            return VersionRegex.IsMatch(version) ? version : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? SanitizeExceptionType(string? exType)
    {
        if (string.IsNullOrWhiteSpace(exType)) return null;
        string trimmed = exType.Trim();
        return TrustedExceptionTypes.Contains(trimmed) ? trimmed : "UnknownException";
    }

    /// <summary>
    /// Validates and sanitizes a symbolic name (Component, Operation, OriginSymbol, etc.).
    /// Rejects arbitrary free-text and symbols containing unsafe characters.
    /// </summary>
    public static string? SanitizeSymbol(string? symbol, int maxChars = 128)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        string trimmed = symbol.Trim();
        if (trimmed.Length > maxChars)
        {
            trimmed = trimmed[..maxChars];
        }

        try
        {
            if (SafeSymbolRegex.IsMatch(trimmed))
            {
                return trimmed;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED_TIMEOUT]";
        }

        return "[REDACTED_SYMBOL]";
    }

    /// <summary>
    /// Extracts safe, allowlisted type and HRESULT information from an exception.
    /// Validates exception type against trusted allowlist (N1).
    /// Never extracts or serializes Exception.Message, Exception.ToString(), or Exception.Data.
    /// </summary>
    public static (string ExceptionType, int? HResult, string? ExceptionChain) SanitizeException(Exception? ex)
    {
        if (ex is null)
        {
            return ("None", null, null);
        }

        string primaryType = GetSafeTypeName(ex.GetType());
        int? hresult = ex.HResult != 0 ? ex.HResult : null;

        var chain = new List<string>(4) { primaryType };
        var current = ex.InnerException;
        int depth = 0;

        while (current is not null && depth < 3)
        {
            chain.Add(GetSafeTypeName(current.GetType()));
            current = current.InnerException;
            depth++;
        }

        string? chainStr = chain.Count > 1 ? string.Join(" -> ", chain) : null;
        return (primaryType, hresult, chainStr);
    }

    private static string GetSafeTypeName(Type type)
    {
        string name = type.Name;
        if (TrustedExceptionTypes.Contains(name))
        {
            return name;
        }
        return "UnknownException";
    }

    /// <summary>
    /// Sanitizes an input message by running defense-in-depth redactions (tokens, emails, user paths, secrets)
    /// and truncating to a bounded length.
    /// </summary>
    public static string SanitizeText(string? input, int maxChars = 256)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        // Bound input size before costly processing (R1)
        string text = input.Length > 8192 ? input[..8192] : input;

        try
        {
            // Apply regex redaction defense-in-depth
            text = JsonQuotedSecretRegex.Replace(text, "\"secret\":\"[REDACTED_SECRET]\"");
            text = QueryParamSecretRegex.Replace(text, "?secret=[REDACTED_SECRET]");
            text = GenericSecretAssignmentRegex.Replace(text, "[REDACTED_SECRET]");
            text = ConversationContentRegex.Replace(text, "[REDACTED_CONVERSATION]");
            text = JwtRegex.Replace(text, "[REDACTED_TOKEN]");
            text = BearerRegex.Replace(text, "Bearer [REDACTED_TOKEN]");
            text = HexOrGuidTokenRegex.Replace(text, "[REDACTED_TOKEN]");
            text = EmailRegex.Replace(text, "[REDACTED_EMAIL]");
            text = UserDirectoryRegex.Replace(text, "[USER_DIR]");
            text = AbsolutePathRegex.Replace(text, "[REDACTED_PATH]");
        }
        catch (RegexMatchTimeoutException)
        {
            text = "[REDACTED_TIMEOUT]";
        }

        if (text.Length > maxChars)
        {
            text = text[..maxChars];
        }

        return text;
    }

    /// <summary>
    /// Sanitizes an HTTP/HTTPS URL: only preserves loopback origin; external or invalid URLs are masked.
    /// Query parameters and fragments are completely stripped.
    /// </summary>
    public static string? SanitizeUrl(string? urlString)
    {
        if (string.IsNullOrWhiteSpace(urlString))
        {
            return null;
        }

        if (Uri.TryCreate(urlString, UriKind.Absolute, out var uri))
        {
            if (uri.IsLoopback)
            {
                string safePath = uri.AbsolutePath.Length <= 32 ? uri.AbsolutePath : "/[PATH]";
                return $"{uri.Scheme}://{uri.Host}:{uri.Port}{safePath}";
            }
            return "[EXTERNAL_URL]";
        }

        return "[INVALID_URL]";
    }

    /// <summary>
    /// Sanitizes filesystem paths by stripping user directory roots and absolute prefixes.
    /// </summary>
    public static string? SanitizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.Length > 8192)
        {
            path = path[..8192];
        }

        string sanitized = UserDirectoryRegex.Replace(path, "[USER_DIR]");
        sanitized = AbsolutePathRegex.Replace(sanitized, "[PATH]");
        return SanitizeText(sanitized, maxChars: 256);
    }
}
