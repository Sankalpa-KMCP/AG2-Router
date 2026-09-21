using System.Text.RegularExpressions;

namespace AG2Router.AG2.Security;

/// <summary>
/// Security, masking, and sanitization utilities for AG2 tokens, command lines, and diagnostic text.
/// Guarantees that ephemeral credentials (CSRF, bridge tokens, passwords) never leak into logs or DTOs.
/// </summary>
public static class AG2Security
{
    private static readonly Regex CsrfTokenRegex = new(
        @"--csrf_token(?:=|\s+)([A-Za-z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HostBridgeTokenRegex = new(
        @"--host_bridge_token(?:=|\s+)([A-Za-z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GenericTokenRegex = new(
        @"--(?:auth|access|session)?_?token(?:=|\s+)([A-Za-z0-9_-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PasswordRegex = new(
        @"--password(?:=|\s+)([^\s""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SensitiveHeadersRegex = new(
        @"(x-codeium-csrf-token:\s*)([^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AuthorizationHeaderRegex = new(
        @"(authorization:\s*Bearer\s*)([^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GenericKeyValueSecretRegex = new(
        @"(\b(?:token|password|secret|key|bearer)\b\s*[:=]\s*)([^\s"",;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Masks a sensitive token string (e.g. "fb541fbe...16bb" or "[REDACTED]").
    /// </summary>
    public static string MaskToken(string? token, bool fullyRedact = false)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        if (fullyRedact || token.Length <= 8)
        {
            return "[REDACTED]";
        }

        return $"{token[..4]}...{token[^4..]}";
    }

    /// <summary>
    /// Extracts the raw CSRF token from command line arguments.
    /// </summary>
    public static string? ExtractCsrfToken(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var match = CsrfTokenRegex.Match(commandLine);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Sanitizes command-line arguments by replacing sensitive tokens with [REDACTED].
    /// </summary>
    public static string SanitizeCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var sanitized = CsrfTokenRegex.Replace(commandLine, "--csrf_token [REDACTED]");
        sanitized = HostBridgeTokenRegex.Replace(sanitized, "--host_bridge_token=[REDACTED]");
        sanitized = GenericTokenRegex.Replace(sanitized, "--token=[REDACTED]");
        sanitized = PasswordRegex.Replace(sanitized, "--password=[REDACTED]");
        return sanitized;
    }

    /// <summary>
    /// Redacts sensitive headers and key-value secret pairs from diagnostic text or error messages.
    /// </summary>
    public static string RedactSensitiveText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var redacted = SensitiveHeadersRegex.Replace(text, "$1[REDACTED]");
        redacted = AuthorizationHeaderRegex.Replace(redacted, "$1[REDACTED]");
        redacted = GenericKeyValueSecretRegex.Replace(redacted, "$1[REDACTED]");
        return redacted;
    }

    /// <summary>
    /// Sanitizes an exception message so tokens or sensitive URL fragments are never propagated.
    /// </summary>
    public static string SanitizeError(Exception ex)
    {
        var msg = ex.Message;
        return RedactSensitiveText(SanitizeCommandLine(msg));
    }
}
