import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { WindowsDpapiProvider, DpapiError } from '../src/vault/dpapi.js';

describe('WindowsDpapiProvider (CurrentUser DPAPI Encryption)', () => {
  it('should reject empty or null plaintext', async () => {
    const dpapi = new WindowsDpapiProvider();
    await assert.rejects(
      async () => {
        await dpapi.encrypt(Buffer.alloc(0));
      },
      (err: unknown) => {
        assert(err instanceof DpapiError);
        assert((err as Error).message.includes('Cannot encrypt empty or null payload'));
        return true;
      }
    );
  });

  it('should reject empty or null ciphertext on decrypt', async () => {
    const dpapi = new WindowsDpapiProvider();
    await assert.rejects(
      async () => {
        await dpapi.decrypt(Buffer.alloc(0));
      },
      (err: unknown) => {
        assert(err instanceof DpapiError);
        assert((err as Error).message.includes('Cannot decrypt empty or null ciphertext'));
        return true;
      }
    );
  });

  it('should successfully encrypt and decrypt a payload (round-trip)', async () => {
    const dpapi = new WindowsDpapiProvider();
    const originalText = JSON.stringify({ accountId: 'acc-123', secret: 'sample-secret-payload' });
    const plaintext = Buffer.from(originalText, 'utf8');

    const ciphertext = await dpapi.encrypt(plaintext);
    assert(ciphertext.length > 0);
    // Ciphertext must differ from original plaintext
    assert.notDeepStrictEqual(ciphertext, Buffer.from(originalText, 'utf8'));

    // Verify best-effort zeroing on the input buffer
    assert.strictEqual(plaintext.every((byte) => byte === 0), true);

    const decrypted = await dpapi.decrypt(ciphertext);
    assert.strictEqual(decrypted.toString('utf8'), originalText);
  });

  it('should fail closed when ciphertext is corrupted or tampered with', async () => {
    const dpapi = new WindowsDpapiProvider();
    const original = Buffer.from('uncompromised-secret-data', 'utf8');
    const ciphertext = await dpapi.encrypt(original);

    // Tamper with bytes in ciphertext
    const tampered = Buffer.from(ciphertext);
    tampered[tampered.length - 1] ^= 0xff;
    tampered[tampered.length - 2] ^= 0xaa;

    await assert.rejects(
      async () => {
        await dpapi.decrypt(tampered);
      },
      (err: unknown) => {
        assert(err instanceof DpapiError);
        assert((err as Error).message.includes('DPAPI decryption failed'));
        return true;
      }
    );
  });

  it('should sanitize errors if execution encounters failures', async () => {
    class FailingDpapiProvider extends WindowsDpapiProvider {
      protected override runPowerShell(): Promise<string> {
        return Promise.reject(new Error('Fatal error containing secret token: fb5428a2a893457199'));
      }
    }

    const provider = new FailingDpapiProvider();
    await assert.rejects(
      async () => {
        await provider.encrypt(Buffer.from('test', 'utf8'));
      },
      (err: unknown) => {
        assert(err instanceof DpapiError);
        // Ensure secret token was sanitized
        assert.strictEqual((err as Error).message.includes('fb5428a2a893457199'), false);
        return true;
      }
    );
  });
});
