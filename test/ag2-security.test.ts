/**
 * Test: AG2 Security & Token Redaction
 */

import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import {
  maskToken,
  redactSensitiveText,
  sanitizeCommandLine,
  sanitizeError
} from '../src/ag2/security.js';

describe('AG2 Security & Token Redaction', () => {
  it('should mask tokens safely without exposing full secrets', () => {
    const token = '12345678-abcd-ef01-2345-6789abcdef01';
    const masked = maskToken(token);
    assert.equal(masked, '1234...ef01');
    assert.doesNotMatch(masked, /12345678-abcd/);

    // Short tokens are fully redacted
    assert.equal(maskToken('short12'), '[REDACTED]');
    // Explicit full redaction
    assert.equal(maskToken(token, true), '[REDACTED]');
    // Null or empty
    assert.equal(maskToken(null), '');
    assert.equal(maskToken(''), '');
  });

  it('should sanitize CSRF tokens and secrets from command line arguments', () => {
    const rawCmd =
      'C:\\path\\language_server.exe --standalone --csrf_token test-csrf-token-12345 --host_bridge_token=secret-hb-token-98765 --app_data_dir antigravity';

    const cleanCmd = sanitizeCommandLine(rawCmd);
    assert.match(cleanCmd, /--csrf_token \[REDACTED\]/);
    assert.match(cleanCmd, /--host_bridge_token=\[REDACTED\]/);
    assert.doesNotMatch(cleanCmd, /test-csrf-token-12345/);
    assert.doesNotMatch(cleanCmd, /secret-hb-token-98765/);
    assert.match(cleanCmd, /--app_data_dir antigravity/);
  });

  it('should redact sensitive HTTP headers and tokens from text', () => {
    const text =
      'Failed with header x-codeium-csrf-token: secret-csrf-token-abc\nAuthorization: Bearer secret-bearer-xyz';
    const cleanText = redactSensitiveText(text);

    assert.doesNotMatch(cleanText, /secret-csrf-token-abc/);
    assert.doesNotMatch(cleanText, /secret-bearer-xyz/);
    assert.match(cleanText, /x-codeium-csrf-token: \[REDACTED\]/);
    assert.match(cleanText, /authorization: Bearer \[REDACTED\]/i);
  });

  it('should sanitize Error objects without leaking tokens in message or stack', () => {
    const rawErr = new Error('RPC error with --csrf_token super-secret-token-xyz failed');
    const sanitized = sanitizeError(rawErr);

    assert.ok(sanitized instanceof Error);
    assert.doesNotMatch(sanitized.message, /super-secret-token-xyz/);
    assert.match(sanitized.message, /--csrf_token \[REDACTED\]/);
  });
});
