/**
 * AG2 Router - Windows DPAPI Data Protection Provider
 *
 * Utilizes Windows Data Protection API (DPAPI) scoped to the CurrentUser.
 * Data is encrypted/decrypted via pure stdin/stdout piping to PowerShell
 * System.Security.Cryptography.ProtectedData.
 *
 * SECURITY & MEMORY HYGIENE:
 * - Plaintext secrets are piped via stdin and never touch process arguments or disk.
 * - Ciphertexts fail closed if tampered with or if accessed by a different Windows user.
 * - Plaintext input buffers are wiped with zeros (best-effort memory hygiene; note that
 *   due to V8 engine string pooling and PowerShell runtime allocations, this is best-effort
 *   rather than guaranteed cryptographic erasure).
 */

import { spawn } from 'node:child_process';
import { redactSensitiveText } from '../ag2/security.js';

export class DpapiError extends Error {
  constructor(message: string, public readonly originalError?: string) {
    super(message);
    this.name = 'DpapiError';
  }
}

export interface IDpapiProvider {
  /**
   * Encrypts plaintext buffer using Windows DPAPI scoped to CurrentUser.
   */
  encrypt(plaintext: Buffer): Promise<Buffer>;

  /**
   * Decrypts ciphertext buffer using Windows DPAPI scoped to CurrentUser.
   * Throws DpapiError on tampering or corruption.
   */
  decrypt(ciphertext: Buffer): Promise<Buffer>;
}

const PS_PROTECT_SCRIPT = `
Add-Type -AssemblyName System.Security;
$stdin = [Console]::In.ReadToEnd();
if (-not $stdin) { exit 10 }
$bytes = [Convert]::FromBase64String($stdin.Trim());
$enc = [System.Security.Cryptography.ProtectedData]::Protect(
    $bytes,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::CurrentUser
);
[Console]::Out.Write([Convert]::ToBase64String($enc));
`;

const PS_UNPROTECT_SCRIPT = `
Add-Type -AssemblyName System.Security;
$stdin = [Console]::In.ReadToEnd();
if (-not $stdin) { exit 10 }
$bytes = [Convert]::FromBase64String($stdin.Trim());
$dec = [System.Security.Cryptography.ProtectedData]::Unprotect(
    $bytes,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::CurrentUser
);
[Console]::Out.Write([Convert]::ToBase64String($dec));
`;

export class WindowsDpapiProvider implements IDpapiProvider {
  /**
   * Encrypts plaintext buffer using Windows DPAPI CurrentUser scope.
   * Performs best-effort zeroing on the input buffer.
   */
  public async encrypt(plaintext: Buffer): Promise<Buffer> {
    if (!plaintext || plaintext.length === 0) {
      throw new DpapiError('Cannot encrypt empty or null payload');
    }

    const base64Input = plaintext.toString('base64');

    try {
      const encryptedBase64 = await this.runPowerShell(PS_PROTECT_SCRIPT, base64Input);
      return Buffer.from(encryptedBase64.trim(), 'base64');
    } catch (err) {
      const msg = err instanceof Error ? redactSensitiveText(err.message) : 'Encryption failed';
      throw new DpapiError(`DPAPI encryption failed: ${msg}`);
    } finally {
      // Best-effort memory hygiene: zero out the plaintext buffer
      plaintext.fill(0);
    }
  }

  /**
   * Decrypts ciphertext buffer using Windows DPAPI CurrentUser scope.
   * Fails closed if ciphertext is corrupted, tampered with, or under a different Windows user.
   */
  public async decrypt(ciphertext: Buffer): Promise<Buffer> {
    if (!ciphertext || ciphertext.length === 0) {
      throw new DpapiError('Cannot decrypt empty or null ciphertext');
    }

    const base64Input = ciphertext.toString('base64');

    try {
      const decryptedBase64 = await this.runPowerShell(PS_UNPROTECT_SCRIPT, base64Input);
      return Buffer.from(decryptedBase64.trim(), 'base64');
    } catch (err) {
      const msg = err instanceof Error ? redactSensitiveText(err.message) : 'Decryption failed';
      throw new DpapiError(`DPAPI decryption failed (corrupted ciphertext or invalid user): ${msg}`);
    }
  }

  /**
   * Executes PowerShell with stdin/stdout streaming for DPAPI operations.
   */
  protected runPowerShell(script: string, stdinData: string): Promise<string> {
    return new Promise((resolve, reject) => {
      const child = spawn(
        'powershell.exe',
        ['-NoProfile', '-NonInteractive', '-Command', script],
        {
          windowsHide: true,
          stdio: ['pipe', 'pipe', 'pipe']
        }
      );

      let stdoutData = '';
      let stderrData = '';

      child.stdout.on('data', (chunk) => {
        stdoutData += chunk.toString();
      });

      child.stderr.on('data', (chunk) => {
        stderrData += chunk.toString();
      });

      child.on('error', (err) => {
        reject(new DpapiError(`Failed to spawn PowerShell for DPAPI: ${err.message}`));
      });

      child.on('close', (code) => {
        if (code !== 0) {
          const safeStderr = redactSensitiveText(stderrData.trim() || 'none');
          reject(new DpapiError(`PowerShell DPAPI process exited with code ${code}. Stderr: ${safeStderr}`));
        } else {
          resolve(stdoutData);
        }
      });

      try {
        child.stdin.write(stdinData);
        child.stdin.end();
      } catch (writeErr) {
        reject(new DpapiError(`Failed to pipe data to DPAPI stdin: ${(writeErr as Error).message}`));
      }
    });
  }
}
