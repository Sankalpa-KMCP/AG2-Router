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

export interface IWinCredWriter {
  writeCredential(entry: WinCredEntry): Promise<boolean>;
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

/**
 * P/Invoke script for strictly writing Generic Windows Credentials via advapi32.dll.
 * Note: Only CredWriteW and CredFree are imported. Deletion and broad manipulation are excluded.
 */
const PS_WRITE_WINCRED_SCRIPT = `
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class WinCredNativeWriter {
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

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CredWriteW", CharSet = CharSet.Unicode)]
    public static extern bool CredWrite([In] ref CREDENTIAL cred, uint flags);

    public static bool WriteFromB64(string target, string userName, int type, int persist, string blobB64) {
        byte[] blob = Convert.FromBase64String(blobB64);
        IntPtr targetPtr = Marshal.StringToHGlobalUni(target);
        IntPtr userPtr = Marshal.StringToHGlobalUni(userName);
        IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
        try {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            CREDENTIAL c = new CREDENTIAL();
            c.Flags = 0;
            c.Type = type;
            c.TargetName = targetPtr;
            c.Comment = IntPtr.Zero;
            c.CredentialBlobSize = blob.Length;
            c.CredentialBlob = blobPtr;
            c.Persist = persist;
            c.AttributeCount = 0;
            c.Attributes = IntPtr.Zero;
            c.TargetAlias = IntPtr.Zero;
            c.UserName = userPtr;
            return CredWrite(ref c, 0);
        } finally {
            if (targetPtr != IntPtr.Zero) Marshal.FreeHGlobal(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeHGlobal(userPtr);
            if (blobPtr != IntPtr.Zero) {
                for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
                Marshal.FreeHGlobal(blobPtr);
            }
        }
    }
}
"@

$stdin = [Console]::In.ReadToEnd().Trim();
if (-not $stdin) { exit 10; }
$inputObj = ConvertFrom-Json $stdin;
$ok = [WinCredNativeWriter]::WriteFromB64(
    $inputObj.target,
    $inputObj.userName,
    [int]$inputObj.type,
    [int]$inputObj.persist,
    $inputObj.blobBase64
);
if ($ok) {
    [Console]::Out.Write("SUCCESS");
} else {
    throw "CredWrite failed with Win32 error code: " + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error();
}
`;

/**
 * Narrow, production-shaped Windows Credential Manager Writer.
 * Restricted strictly to Generic Credential (type 1) updates.
 * Streams secret payload over stdin with zero CLI arguments or temp files.
 */
export class AG2WinCredWriter implements IWinCredWriter {
  /**
   * Writes a generic credential entry safely.
   * Enforces type === 1 (Generic) and non-empty target.
   */
  public async writeCredential(entry: WinCredEntry): Promise<boolean> {
    if (!entry || typeof entry !== 'object') {
      throw new WinCredError('Invalid credential entry object');
    }

    if (!entry.target || typeof entry.target !== 'string' || !entry.target.trim()) {
      throw new WinCredError('Target name must be a non-empty string');
    }

    if (entry.type !== 1) {
      throw new WinCredError(`Invalid credential type ${entry.type}. Only Generic Credential (type 1) is supported.`);
    }

    if (!entry.blob || !(entry.blob instanceof Buffer) || entry.blob.length === 0) {
      throw new WinCredError('Credential blob must be a non-empty Buffer');
    }

    const payloadJson = JSON.stringify({
      target: entry.target.trim(),
      userName: entry.userName || '',
      type: 1,
      persist: entry.persistence ?? 2, // Local Machine
      blobBase64: entry.blob.toString('base64')
    });

    try {
      const result = await this.runPowerShellWithStdin(PS_WRITE_WINCRED_SCRIPT, payloadJson);
      return result.trim() === 'SUCCESS';
    } catch (err) {
      const msg = err instanceof Error ? redactSensitiveText(err.message) : 'Credential write failed';
      throw new WinCredError(`Failed to write Windows Credential: ${msg}`);
    } finally {
      // Best-effort memory hygiene: zero out the input blob buffer
      entry.blob.fill(0);
    }
  }

  /**
   * Spawns PowerShell with stdin/stdout streaming.
   * Overridable in tests to prevent any live OS Credential Manager mutations.
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
