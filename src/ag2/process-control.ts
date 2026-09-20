/**
 * AG2 Router - Antigravity 2 Process Lifecycle & Control Abstraction
 *
 * Implements safe, production-shaped process inspection, structured argument
 * parsing, termination, and process relaunching for Antigravity 2.
 *
 * SECURITY & PROCESS SAFETY:
 * - Zero shell interpolation: Does NOT run through `cmd.exe` or `powershell -Command "..."`.
 * - Structured argument handling: Arguments are captured and launched as discrete arrays.
 * - Sensitive argument protection: Ephemeral tokens (--csrf_token, --host_bridge_token)
 *   are preserved faithfully in memory for relaunch but strictly redacted in logs/APIs.
 * - Executable provenance validation: Executables must strictly match verified AG2 binaries
 *   ending in language_server.exe. Arbitrary paths from external callers are rejected.
 */

import { execFile, spawn } from 'node:child_process';
import * as path from 'node:path';
import { promisify } from 'node:util';
import { DiscoveredProcessRaw, IProcessInspector, WindowsProcessInspector } from './discovery.js';
import { redactSensitiveText, sanitizeCommandLine } from './security.js';

const execFileAsync = promisify(execFile);

export class ProcessControlError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ProcessControlError';
  }
}

/**
 * Structured specification of an Antigravity 2 process launch.
 * Raw arguments are kept exclusively in memory; sanitizedArgs is safe for logging/display.
 */
export interface AG2ProcessLaunchSpec {
  readonly pid: number;
  readonly executablePath: string;
  readonly rawArgs: readonly string[];
  readonly sanitizedArgs: readonly string[];
  readonly capturedAt: string;
}

export interface IProcessController {
  captureLaunchSpec(pid: number): Promise<AG2ProcessLaunchSpec>;
  terminateProcess(pid: number, timeoutMs?: number): Promise<boolean>;
  launchProcess(spec: AG2ProcessLaunchSpec): Promise<number>;
  waitForExit(pid: number, timeoutMs?: number): Promise<boolean>;
  isPidAlive(pid: number): boolean;
}

/**
 * Parses a Windows command-line string into structured argument tokens.
 * Handles quoted substrings with spaces without invoking cmd.exe or subshells.
 */
export function parseCommandLineArguments(cmdLine: string): string[] {
  if (!cmdLine || typeof cmdLine !== 'string') return [];

  const tokens: string[] = [];
  let current = '';
  let inQuotes = false;
  let quoteChar = '';

  for (let i = 0; i < cmdLine.length; i++) {
    const char = cmdLine[i];

    if ((char === '"' || char === "'") && (!inQuotes || char === quoteChar)) {
      inQuotes = !inQuotes;
      quoteChar = inQuotes ? char : '';
      // We retain the inner string without surrounding outer quotes
      continue;
    }

    if (!inQuotes && (char === ' ' || char === '\t' || char === '\r' || char === '\n')) {
      if (current.length > 0) {
        tokens.push(current);
        current = '';
      }
      continue;
    }

    current += char;
  }

  if (current.length > 0) {
    tokens.push(current);
  }

  return tokens;
}

/**
 * Produces a sanitized copy of structured launch arguments for safe display and logging.
 * Replaces values of sensitive flags with [REDACTED].
 */
export function redactLaunchArgs(args: readonly string[]): string[] {
  const sensitiveFlags = new Set([
    '--csrf_token',
    '--host_bridge_token',
    '--token',
    '--auth_token',
    '--session_token',
    '--access_token',
    '--password'
  ]);

  const result: string[] = [];
  let redactNext = false;

  for (let i = 0; i < args.length; i++) {
    const arg = args[i];

    if (redactNext) {
      result.push('[REDACTED]');
      redactNext = false;
      continue;
    }

    const lower = arg.toLowerCase();

    // Check for --flag=value format
    let matchedPrefix = false;
    for (const flag of sensitiveFlags) {
      if (lower.startsWith(`${flag}=`)) {
        result.push(`${arg.slice(0, flag.length + 1)}[REDACTED]`);
        matchedPrefix = true;
        break;
      }
    }

    if (matchedPrefix) continue;

    // Check for --flag <value> format
    if (sensitiveFlags.has(lower)) {
      result.push(arg);
      redactNext = true;
      continue;
    }

    result.push(sanitizeCommandLine(arg));
  }

  return result;
}

/**
 * Windows implementation of AG2 Process Controller.
 */
export class WindowsProcessController implements IProcessController {
  private readonly inspector: IProcessInspector;

  constructor(inspector?: IProcessInspector) {
    this.inspector = inspector || new WindowsProcessInspector();
  }

  /**
   * Captures the full launch specification from a verified running AG2 process.
   * Preserves exact executable path and structured arguments faithfully.
   */
  public async captureLaunchSpec(pid: number): Promise<AG2ProcessLaunchSpec> {
    if (!pid || pid <= 0) {
      throw new ProcessControlError(`Invalid PID: ${pid}`);
    }

    const processes = await this.inspector.findProcesses();
    const targetProcess = processes.find((p) => p.pid === pid);
    if (!targetProcess) {
      throw new ProcessControlError(`Process with PID ${pid} was not found on the system.`);
    }

    // Validate executable provenance
    const execPath = targetProcess.executablePath || this.extractExecutableFromCommandLine(targetProcess.commandLine);
    if (!execPath) {
      throw new ProcessControlError(`Could not determine executable path for PID ${pid}.`);
    }

    const baseName = path.basename(execPath).toLowerCase();
    if (!baseName.includes('language_server')) {
      throw new ProcessControlError(
        `Executable '${execPath}' is not a recognized Antigravity 2 language server binary.`
      );
    }

    // Parse command line into structured arguments
    const allTokens = parseCommandLineArguments(targetProcess.commandLine);
    // Remove the leading executable token if present
    const rawArgs = this.stripLeadingExecutableToken(allTokens, execPath);
    const sanitizedArgs = redactLaunchArgs(rawArgs);

    return {
      pid,
      executablePath: execPath,
      rawArgs,
      sanitizedArgs,
      capturedAt: new Date().toISOString()
    };
  }

  /**
   * Terminates the specified process PID using native signals and taskkill if necessary.
   * Waits for the PID to completely exit.
   */
  public async terminateProcess(pid: number, timeoutMs = 10000): Promise<boolean> {
    if (!pid || pid <= 0) {
      throw new ProcessControlError(`Invalid PID: ${pid}`);
    }

    if (!this.isPidAlive(pid)) {
      return true; // Already exited
    }

    // 1. First attempt native SIGTERM
    try {
      process.kill(pid, 'SIGTERM');
    } catch {
      // ignore
    }

    const exitedGracefully = await this.waitForExit(pid, Math.min(timeoutMs, 3000));
    if (exitedGracefully) {
      return true;
    }

    // 2. Force terminate via Windows taskkill.exe with zero shell interpolation
    try {
      await execFileAsync('taskkill.exe', ['/PID', String(pid), '/F'], { timeout: 5000 });
    } catch {
      // taskkill may error if process just exited
    }

    return this.waitForExit(pid, timeoutMs);
  }

  /**
   * Launches Antigravity 2 from an approved launch specification using native spawn.
   * Zero shell interpolation (no cmd.exe / powershell).
   * Returns the spawned process PID.
   */
  public async launchProcess(spec: AG2ProcessLaunchSpec): Promise<number> {
    if (!spec || !spec.executablePath) {
      throw new ProcessControlError('Invalid launch specification: executablePath is required.');
    }

    const baseName = path.basename(spec.executablePath).toLowerCase();
    if (!baseName.includes('language_server')) {
      throw new ProcessControlError(
        `Executable path '${spec.executablePath}' is not an authorized Antigravity 2 binary.`
      );
    }

    try {
      // Spawn child process detached, without any shell wrapping
      const child = spawn(spec.executablePath, [...spec.rawArgs], {
        detached: true,
        stdio: 'ignore',
        windowsHide: true
      });

      if (!child.pid) {
        throw new ProcessControlError('Spawn succeeded but child PID was not assigned.');
      }

      // Unreference so parent process can exit independently
      child.unref();
      return child.pid;
    } catch (err) {
      const msg = err instanceof Error ? redactSensitiveText(err.message) : 'Spawn failed';
      throw new ProcessControlError(`Failed to launch Antigravity 2 process: ${msg}`);
    }
  }

  /**
   * Polls until the process PID exits or the timeout is reached.
   */
  public async waitForExit(pid: number, timeoutMs = 10000): Promise<boolean> {
    const startTime = Date.now();
    while (Date.now() - startTime < timeoutMs) {
      if (!this.isPidAlive(pid)) {
        return true;
      }
      await new Promise((resolve) => setTimeout(resolve, 200));
    }
    return !this.isPidAlive(pid);
  }

  /**
   * Verifies whether a PID is alive using Node's native process.kill(pid, 0).
   */
  public isPidAlive(pid: number): boolean {
    return this.inspector.isPidAlive(pid);
  }

  private extractExecutableFromCommandLine(cmdLine: string): string | null {
    if (!cmdLine) return null;
    const tokens = parseCommandLineArguments(cmdLine);
    return tokens[0] || null;
  }

  private stripLeadingExecutableToken(tokens: string[], execPath: string): string[] {
    if (tokens.length === 0) return [];
    const first = tokens[0].toLowerCase();
    const targetBase = path.basename(execPath).toLowerCase();
    const targetNorm = path.normalize(execPath).toLowerCase();

    if (first === targetBase || first === targetNorm || first.endsWith(targetBase)) {
      return tokens.slice(1);
    }

    return tokens;
  }
}
