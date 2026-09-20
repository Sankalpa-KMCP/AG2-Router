/**
 * AG2 Router - Read-Only Windows Credential Manager Access
 *
 * Provides strictly read-only retrieval of the active Antigravity 2 session
 * from Windows Credential Manager (`gemini:antigravity`).
 *
 * SAFETY & BOUNDARY ENFORCEMENT:
 * - This module is STRICTLY READ-ONLY.
 * - Under NO circumstances does this module contain P/Invoke definitions or
 *   methods for CredWrite, CredDelete, or credential mutation.
 * - Stdin/stdout streaming prevents credential exposure on process command lines.
 */

import { spawn } from 'node:child_process';
import { redactSensitiveText } from './security.js';

export interface WinCredEntry {
  readonly target: string;
  readonly type: number; // 1 = Generic
  readonly userName: string;
  readonly persistence: number;
  readonly blob: Buffer;
}

export class WinCredError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'WinCredError';
  }
}

export interface IWinCredReader {
  readCredential(target?: string): Promise<WinCredEntry | null>;
}

/**
 * P/Invoke script for strictly reading Windows Credentials via advapi32.dll.
 * Note: Only CredReadW and CredFree are imported. Mutation functions are excluded by design.
 */
const PS_READ_WINCRED_SCRIPT = `
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class WinCredNativeReader {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CredReadW", CharSet = CharSet.Unicode)]
    public static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credPtr);

    [DllImport("advapi32.dll")]
    public static extern void CredFree(IntPtr cred);

    public static string ReadToB64(string target) {
        IntPtr ptr;
        if (!CredRead(target, 1 /* Generic */, 0, out ptr)) {
            int err = Marshal.GetLastWin32Error();
            if (err == 1168 /* ERROR_NOT_FOUND */) {
                return "{\\"exists\\":false}";
            }
            throw new Exception("CredRead failed with Win32 error code: " + err);
        }
        try {
            CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
            byte[] bytes = new byte[c.CredentialBlobSize];
            if (c.CredentialBlobSize > 0 && c.CredentialBlob != IntPtr.Zero) {
                Marshal.Copy(c.CredentialBlob, bytes, 0, c.CredentialBlobSize);
            }
            string b64 = Convert.ToBase64String(bytes);
            string user = Marshal.PtrToStringUni(c.UserName) ?? "";
            string tgt = Marshal.PtrToStringUni(c.TargetName) ?? target;
            return string.Format(
                "{{\\"exists\\":true,\\"target\\":\\"{0}\\",\\"type\\":{1},\\"userName\\":\\"{2}\\",\\"persistence\\":{3},\\"blobBase64\\":\\"{4}\\"}}",
                tgt.Replace("\\\\", "\\\\\\\\").Replace("\\"", "\\\\\\""),
                c.Type,
                user.Replace("\\\\", "\\\\\\\\").Replace("\\"", "\\\\\\""),
                c.Persist,
                b64
            );
        } finally {
            CredFree(ptr);
        }
    }
}
"@

$stdin = [Console]::In.ReadToEnd().Trim();
if (-not $stdin) { exit 10; }
$result = [WinCredNativeReader]::ReadToB64($stdin);
[Console]::Out.Write($result);
`;

/**
 * Standard Antigravity 2 Windows Credential Manager target.
 */
export const DEFAULT_AG2_WINCRED_TARGET = 'gemini:antigravity';

/**
 * Read-only Windows Credential Manager Reader.
 */
export class AG2WinCredReader implements IWinCredReader {
  private readonly defaultTarget: string;

  constructor(defaultTarget: string = DEFAULT_AG2_WINCRED_TARGET) {
    this.defaultTarget = defaultTarget;
  }

  /**
   * Reads the active credential entry for target (default 'gemini:antigravity').
   * Returns null if target credential does not exist.
   * Never mutates or writes to Credential Manager.
   */
  public async readCredential(target: string = this.defaultTarget): Promise<WinCredEntry | null> {
    if (!target || typeof target !== 'string') {
      throw new WinCredError('Target name must be a non-empty string');
    }

    let rawJson: string;
    try {
      rawJson = await this.runPowerShellWithStdin(PS_READ_WINCRED_SCRIPT, target);
    } catch (err) {
      const msg = err instanceof Error ? redactSensitiveText(err.message) : 'Unknown WinCred spawn error';
      throw new WinCredError(`WinCred execution error: ${msg}`);
    }

    try {
      const parsed = JSON.parse(rawJson.trim()) as {
        exists?: boolean;
        target?: string;
        type?: number;
        userName?: string;
        persistence?: number;
        blobBase64?: string;
      };

      if (!parsed.exists) {
        return null;
      }

      if (!parsed.blobBase64) {
        return {
          target: parsed.target || target,
          type: parsed.type ?? 1,
          userName: parsed.userName || '',
          persistence: parsed.persistence ?? 2,
          blob: Buffer.alloc(0)
        };
      }

      const blob = Buffer.from(parsed.blobBase64, 'base64');
      return {
        target: parsed.target || target,
        type: parsed.type ?? 1,
        userName: parsed.userName || '',
        persistence: parsed.persistence ?? 2,
        blob
      };
    } catch (err) {
      const msg = err instanceof Error ? redactSensitiveText(err.message) : 'JSON parse error';
      throw new WinCredError(`Failed to parse WinCred output: ${msg}`);
    }
  }

  /**
   * Spawns PowerShell with stdin/stdout streaming.
   * Target name is passed via stdin to avoid command line inspection.
   */
  protected runPowerShellWithStdin(script: string, stdinData: string): Promise<string> {
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
        reject(new WinCredError(`Failed to spawn PowerShell: ${err.message}`));
      });

      child.on('close', (code) => {
        if (code !== 0) {
          const safeStderr = redactSensitiveText(stderrData.trim() || 'none');
          reject(new WinCredError(`PowerShell process exited with code ${code}. Stderr: ${safeStderr}`));
        } else {
          resolve(stdoutData);
        }
      });

      try {
        child.stdin.write(stdinData);
        child.stdin.end();
      } catch (writeErr) {
        reject(new WinCredError(`Failed to write to stdin: ${(writeErr as Error).message}`));
      }
    });
  }
}
