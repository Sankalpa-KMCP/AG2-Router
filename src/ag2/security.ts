/**
 * AG2 Router - Antigravity 2 Security & Secret Sanitization
 *
 * Enforces strict redaction of CSRF tokens, session secrets, command-line arguments,
 * and authorization headers before logging, error handling, or API serialization.
 */

/**
 * Mask a token string showing only prefix and suffix (e.g. "fb54...16bb")
 * or fully redact if requested or if too short to safely partial-mask.
 */
export function maskToken(token?: string | null, fullyRedact = false): string {
  if (!token || typeof token !== 'string') {
    return '';
  }
  const trimmed = token.trim();
  if (!trimmed) {
    return '';
  }
  if (fullyRedact || trimmed.length <= 8) {
    return '[REDACTED]';
  }
  return `${trimmed.slice(0, 4)}...${trimmed.slice(-4)}`;
}

/**
 * Remove sensitive flags from command lines (e.g. --csrf_token, --host_bridge_token, --token).
 */
export function sanitizeCommandLine(cmd?: string | null): string {
  if (!cmd || typeof cmd !== 'string') {
    return '';
  }

  return cmd
    // Redact --csrf_token <value> or --csrf_token=<value>
    .replace(/(--csrf_token(?:=|\s+))([^\s"']+)/gi, '$1[REDACTED]')
    // Redact --host_bridge_token=<value> or --host_bridge_token <value>
    .replace(/(--host_bridge_token(?:=|\s+))([^\s"']+)/gi, '$1[REDACTED]')
    // Redact general auth/session tokens
    .replace(/(--(?:auth|access|session)?_?token(?:=|\s+))([^\s"']+)/gi, '$1[REDACTED]')
    // Redact password arguments
    .replace(/(--password(?:=|\s+))([^\s"']+)/gi, '$1[REDACTED]');
}

/**
 * Scrub sensitive headers and tokens from any text or error string.
 */
export function redactSensitiveText(text?: string | null): string {
  if (!text || typeof text !== 'string') {
    return '';
  }

  let sanitized = sanitizeCommandLine(text);

  // Redact header occurrences: x-codeium-csrf-token, authorization, etc.
  sanitized = sanitized.replace(/(x-codeium-csrf-token:\s*)([^\r\n]+)/gi, '$1[REDACTED]');
  sanitized = sanitized.replace(/(authorization:\s*Bearer\s*)([^\r\n]+)/gi, '$1[REDACTED]');

  return sanitized;
}

/**
 * Wrap an unknown error into a sanitized Error instance with secret patterns redacted.
 */
export function sanitizeError(err: unknown): Error {
  if (err instanceof Error) {
    const cleanMessage = redactSensitiveText(err.message);
    const sanitized = new Error(cleanMessage);
    sanitized.name = err.name;
    return sanitized;
  }
  return new Error(redactSensitiveText(String(err)));
}
