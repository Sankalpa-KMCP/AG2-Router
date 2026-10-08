using System.Text;
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

    private static readonly Regex SecretKeyPrefixRegex = new(
        @"(?<prefix>(?:(?<keyquote>[""'])(?<key>(?:x[_-](?:codeium[_-])?)?csrf[_-]?token|host[_-]?bridge[_-]?token|(?:access|refresh|auth|session)[_-]?token|api[_-]?key|client[_-]?secret|secret[_-]?key|token|password|secret|key|bearer|authorization)\k<keyquote>\s*[:=]\s*|\b(?<key>(?:x[_-](?:codeium[_-])?)?csrf[_-]?token|host[_-]?bridge[_-]?token|(?:access|refresh|auth|session)[_-]?token|api[_-]?key|client[_-]?secret|secret[_-]?key|token|password|secret|key|bearer|authorization)\b[ \t]*[:=][ \t]*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

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
    /// Redacts sensitive headers, JSON secret fields, and key-value secret pairs from diagnostic text or error messages.
    /// Preserves surrounding JSON and ordinary diagnostic structures while eliminating secret values.
    /// Handles delimiter-aware quoted strings (mixed quotes, escapes, raw newlines) and unquoted credentials across schemes.
    /// </summary>
    public static string RedactSensitiveText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            var sb = new StringBuilder(text.Length);
            int cursor = 0;

            while (cursor < text.Length)
            {
                var match = SecretKeyPrefixRegex.Match(text, cursor);
                if (!match.Success)
                {
                    sb.Append(text.AsSpan(cursor));
                    break;
                }

                sb.Append(text.AsSpan(cursor, match.Index - cursor));
                sb.Append(match.Value);

                int valStart = match.Index + match.Length;
                if (valStart >= text.Length)
                {
                    sb.Append("[REDACTED]");
                    break;
                }

                char firstChar = text[valStart];
                int valEnd;
                string replacement;

                if (firstChar == '"')
                {
                    int idx = valStart + 1;
                    while (idx < text.Length)
                    {
                        char c = text[idx];
                        if (c == '\\')
                        {
                            idx += 2;
                        }
                        else if (c == '"')
                        {
                            idx++;
                            break;
                        }
                        else
                        {
                            idx++;
                        }
                    }

                    valEnd = Math.Min(idx, text.Length);
                    replacement = "\"[REDACTED]\"";
                }
                else if (firstChar == '\'')
                {
                    int idx = valStart + 1;
                    while (idx < text.Length)
                    {
                        char c = text[idx];
                        if (c == '\\')
                        {
                            idx += 2;
                        }
                        else if (c == '\'')
                        {
                            idx++;
                            break;
                        }
                        else
                        {
                            idx++;
                        }
                    }

                    valEnd = Math.Min(idx, text.Length);
                    replacement = "'[REDACTED]'";
                }
                else
                {
                    var key = match.Groups["key"].Value;
                    int idx = valStart;
                    if (key.Equals("authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        while (idx < text.Length)
                        {
                            char c = text[idx];
                            if (c == '\r' || c == '\n')
                            {
                                break;
                            }
                            idx++;
                        }
                    }
                    else
                    {
                        while (idx < text.Length)
                        {
                            char c = text[idx];
                            if (char.IsWhiteSpace(c) || c == ',' || c == ';' || c == '&' || c == '}' || c == ']')
                            {
                                break;
                            }
                            idx++;
                        }
                    }

                    valEnd = idx;
                    replacement = "[REDACTED]";
                }

                sb.Append(replacement);
                cursor = Math.Max(valEnd, valStart);
            }

            return sb.ToString();
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED_DIAGNOSTIC_FAILURE]";
        }
    }

    /// <summary>
    /// Sanitizes an exception message so tokens or sensitive URL fragments are never propagated.
    /// </summary>
    public static string SanitizeError(Exception ex)
    {
        if (ex == null) return string.Empty;
        var msg = ex.Message;
        return RedactSensitiveText(SanitizeCommandLine(msg));
    }
}
