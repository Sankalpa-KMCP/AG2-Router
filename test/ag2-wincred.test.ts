import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { AG2WinCredReader, WinCredError, DEFAULT_AG2_WINCRED_TARGET } from '../src/ag2/wincred.js';

describe('AG2WinCredReader (Read-Only Windows Credential Manager)', () => {
  it('should guarantee strictly read-only API surface with no write or delete methods', () => {
    const reader = new AG2WinCredReader();
    const readerObj = reader as unknown as Record<string, unknown>;
    assert.strictEqual(typeof reader.readCredential, 'function');
    assert.strictEqual(readerObj['writeCredential'], undefined);
    assert.strictEqual(readerObj['deleteCredential'], undefined);
    assert.strictEqual(readerObj['setCredential'], undefined);
    assert.strictEqual(readerObj['removeCredential'], undefined);
  });

  it('should reject invalid or empty target strings', async () => {
    const reader = new AG2WinCredReader();
    await assert.rejects(
      async () => {
        await reader.readCredential('');
      },
      (err: unknown) => {
        assert(err instanceof WinCredError);
        assert((err as Error).message.includes('Target name must be a non-empty string'));
        return true;
      }
    );
  });

  it('should return null when reading a non-existent target', async () => {
    const reader = new AG2WinCredReader();
    const nonExistentTarget = 'ag2_test_non_existent_target_xyz_12345';
    const result = await reader.readCredential(nonExistentTarget);
    assert.strictEqual(result, null);
  });

  it('should read live gemini:antigravity if present on system without mutation', async () => {
    const reader = new AG2WinCredReader();
    const result = await reader.readCredential(DEFAULT_AG2_WINCRED_TARGET);
    if (result) {
      assert.strictEqual(result.target, DEFAULT_AG2_WINCRED_TARGET);
      assert.strictEqual(result.type, 1); // Generic
      assert(result.blob instanceof Buffer);
      assert(result.blob.length > 0);
      // Verify payload is valid JSON without logging it
      const parsed = JSON.parse(result.blob.toString('utf8'));
      assert(typeof parsed === 'object' && parsed !== null);
      assert(typeof parsed.token === 'string' || typeof parsed.auth_method === 'string');
    } else {
      // On machines without active AG2 session, it should cleanly return null
      assert.strictEqual(result, null);
    }
  });

  it('should fail closed and sanitize errors if PowerShell execution encounters errors', async () => {
    class FailingWinCredReader extends AG2WinCredReader {
      protected override runPowerShellWithStdin(): Promise<string> {
        return Promise.reject(new Error('Process failed with token=secret_token_123456789'));
      }
    }
    const reader = new FailingWinCredReader();
    await assert.rejects(
      async () => {
        await reader.readCredential('test');
      },
      (err: unknown) => {
        assert(err instanceof WinCredError);
        // Ensure secret token was sanitized
        assert.strictEqual((err as Error).message.includes('secret_token_123456789'), false);
        return true;
      }
    );
  });
});
