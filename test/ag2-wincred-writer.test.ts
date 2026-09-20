import test from 'node:test';
import assert from 'node:assert/strict';
import { AG2WinCredWriter, WinCredEntry, WinCredError } from '../src/ag2/wincred.js';

class MockWinCredWriter extends AG2WinCredWriter {
  public lastScript: string | null = null;
  public lastStdinData: string | null = null;
  public mockResponse = 'SUCCESS';
  public shouldThrow: Error | null = null;

  protected override async runPowerShellWithStdin(script: string, stdinData: string): Promise<string> {
    this.lastScript = script;
    this.lastStdinData = stdinData;

    if (this.shouldThrow) {
      throw this.shouldThrow;
    }

    return this.mockResponse;
  }
}

test('AG2WinCredWriter - Parameter validation rejects invalid inputs', async () => {
  const writer = new MockWinCredWriter();

  // Null entry
  await assert.rejects(
    () => writer.writeCredential(null as unknown as WinCredEntry),
    (err: WinCredError) => {
      assert.strictEqual(err.name, 'WinCredError');
      assert.match(err.message, /Invalid credential entry object/);
      return true;
    }
  );

  // Empty target
  await assert.rejects(
    () => writer.writeCredential({
      target: '',
      type: 1,
      userName: 'user@example.com',
      persistence: 2,
      blob: Buffer.from('data')
    }),
    (err: WinCredError) => {
      assert.match(err.message, /Target name must be a non-empty string/);
      return true;
    }
  );

  // Whitespace target
  await assert.rejects(
    () => writer.writeCredential({
      target: '   ',
      type: 1,
      userName: 'user@example.com',
      persistence: 2,
      blob: Buffer.from('data')
    }),
    (err: WinCredError) => {
      assert.match(err.message, /Target name must be a non-empty string/);
      return true;
    }
  );

  // Invalid type (must be 1)
  await assert.rejects(
    () => writer.writeCredential({
      target: 'gemini:antigravity',
      type: 2, // Domain Password
      userName: 'user@example.com',
      persistence: 2,
      blob: Buffer.from('data')
    }),
    (err: WinCredError) => {
      assert.match(err.message, /Only Generic Credential \(type 1\) is supported/);
      return true;
    }
  );

  // Empty blob
  await assert.rejects(
    () => writer.writeCredential({
      target: 'gemini:antigravity',
      type: 1,
      userName: 'user@example.com',
      persistence: 2,
      blob: Buffer.alloc(0)
    }),
    (err: WinCredError) => {
      assert.match(err.message, /Credential blob must be a non-empty Buffer/);
      return true;
    }
  );
});

test('AG2WinCredWriter - Successful write serializes generic credential and zeroes buffer', async () => {
  const writer = new MockWinCredWriter();
  const rawSecret = 'super-secret-oauth-payload-for-testing';
  const secretBuf = Buffer.from(rawSecret, 'utf-8');

  const entry: WinCredEntry = {
    target: 'gemini:antigravity',
    type: 1,
    userName: 'user@example.com',
    persistence: 2,
    blob: secretBuf
  };

  const success = await writer.writeCredential(entry);
  assert.strictEqual(success, true);
  assert.ok(writer.lastStdinData, 'stdin data must be passed');

  const parsed = JSON.parse(writer.lastStdinData);
  assert.strictEqual(parsed.target, 'gemini:antigravity');
  assert.strictEqual(parsed.userName, 'user@example.com');
  assert.strictEqual(parsed.type, 1);
  assert.strictEqual(parsed.persist, 2);
  assert.strictEqual(Buffer.from(parsed.blobBase64, 'base64').toString('utf-8'), rawSecret);

  // Buffer zeroing verification: memory hygiene
  assert.strictEqual(secretBuf.every((b) => b === 0), true, 'Input blob buffer must be zeroed after write');
});

test('AG2WinCredWriter - Error handling redacts sensitive information in exceptions', async () => {
  const writer = new MockWinCredWriter();
  const secretToken = 'ya29.a0AfH6SMD_sensitive_secret_here';
  writer.shouldThrow = new Error(`Connection failed with token: ${secretToken}`);

  const secretBuf = Buffer.from('sensitive-data', 'utf-8');
  await assert.rejects(
    () => writer.writeCredential({
      target: 'gemini:antigravity',
      type: 1,
      userName: 'user@example.com',
      persistence: 2,
      blob: secretBuf
    }),
    (err: WinCredError) => {
      assert.strictEqual(err.name, 'WinCredError');
      assert.ok(!err.message.includes(secretToken), 'Error message must not leak sensitive token');
      return true;
    }
  );

  // Buffer still zeroed on error
  assert.strictEqual(secretBuf.every((b) => b === 0), true, 'Buffer must be zeroed even on error');
});
