/**
 * AG2 Router - Antigravity 2 Process & Port Discovery
 *
 * Implements dynamic, read-only discovery of the Antigravity 2 language server daemon.
 *
 * PERFORMANCE & RESOURCE EFFICIENCY:
 * - Cold discovery uses Windows-native inspection (PowerShell / netstat).
 * - Steady-state uses fast-path in-memory session caching with zero child process spawns.
 * - Liveness verification uses Node's native process.kill(pid, 0) without subshells.
 * - Offline scan cooldown prevents continuous CPU spinning when AG2 is closed.
 * - Circuit breaker invalidates cached credentials on dead PID or repeated RPC failures.
 */

import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { AG2RpcClient } from './rpc-client.js';
import { maskToken, sanitizeCommandLine } from './security.js';
import { AG2DiscoveryResult, AG2ProcessInfo } from './types.js';

const execFileAsync = promisify(execFile);

export interface DiscoveredProcessRaw {
  readonly pid: number;
  readonly name: string;
  readonly commandLine: string;
  readonly executablePath?: string;
}

export interface IProcessInspector {
  findProcesses(): Promise<DiscoveredProcessRaw[]>;
  getListeningPorts(pid: number): Promise<number[]>;
  isPidAlive(pid: number): boolean;
}

/**
 * Windows-native process and network inspection provider.
 */
export class WindowsProcessInspector implements IProcessInspector {
  public async findProcesses(): Promise<DiscoveredProcessRaw[]> {
    const script =
      'Get-CimInstance Win32_Process | Where-Object { $_.Name -like \'*language_server*\' } | Select-Object ProcessId, Name, CommandLine, ExecutablePath | ConvertTo-Json -Compress';

    try {
      const { stdout } = await execFileAsync('powershell.exe', ['-NoProfile', '-Command', script], {
        timeout: 8000
      });

      if (!stdout || !stdout.trim()) {
        return [];
      }

      const parsed = JSON.parse(stdout.trim()) as
        | { ProcessId?: number; Name?: string; CommandLine?: string; ExecutablePath?: string }
        | Array<{ ProcessId?: number; Name?: string; CommandLine?: string; ExecutablePath?: string }>;

      const items = Array.isArray(parsed) ? parsed : [parsed];
      return items
        .filter((item) => typeof item.ProcessId === 'number')
        .map((item) => ({
          pid: item.ProcessId as number,
          name: item.Name || '',
          commandLine: item.CommandLine || '',
          executablePath: item.ExecutablePath || undefined
        }));
    } catch {
      // Fallback to wmic.exe if PowerShell query fails or is restricted
      try {
        const { stdout } = await execFileAsync(
          'wmic.exe',
          [
            'process',
            'where',
            'name like \'%language_server%\'',
            'get',
            'ProcessId,CommandLine,Name,ExecutablePath',
            '/format:csv'
          ],
          { timeout: 8000 }
        );

        const lines = stdout.split(/\r?\n/).filter((l) => l.trim());
        const results: DiscoveredProcessRaw[] = [];

        for (const line of lines) {
          const parts = line.split(',');
          // CSV format: Node,CommandLine,ExecutablePath,Name,ProcessId
          if (parts.length >= 5) {
            const pid = parseInt(parts[4]?.trim() || '', 10);
            if (!isNaN(pid) && pid > 0) {
              results.push({
                pid,
                commandLine: parts[1]?.trim() || '',
                executablePath: parts[2]?.trim() || undefined,
                name: parts[3]?.trim() || ''
              });
            }
          }
        }
        return results;
      } catch (fallbackErr) {
        throw new Error(
          `Process enumeration failed: ${fallbackErr instanceof Error ? fallbackErr.message : String(fallbackErr)}`
        );
      }
    }
  }

  public async getListeningPorts(pid: number): Promise<number[]> {
    try {
      const { stdout } = await execFileAsync('netstat.exe', ['-ano'], { timeout: 8000 });
      const regex = new RegExp(
        `TCP\\s+(?:127\\.0\\.0\\.1|\\[::1\\]):(\\d+)\\s+.*?LISTENING\\s+${pid}\\b`,
        'gi'
      );
      const ports: number[] = [];
      let match: RegExpExecArray | null;

      while ((match = regex.exec(stdout)) !== null) {
        const port = parseInt(match[1], 10);
        if (!ports.includes(port)) {
          ports.push(port);
        }
      }

      return ports;
    } catch (err) {
      throw new Error(
        `Failed to query listening ports for PID ${pid}: ${err instanceof Error ? err.message : String(err)}`
      );
    }
  }

  public isPidAlive(pid: number): boolean {
    try {
      // Signal 0 verifies process existence without terminating it
      process.kill(pid, 0);
      return true;
    } catch (err: unknown) {
      const nodeErr = err as NodeJS.ErrnoException;
      return nodeErr.code !== 'ESRCH';
    }
  }
}

/**
 * Cached session parameters for active AG2 daemon.
 */
export interface CachedAG2Session {
  readonly pid: number;
  readonly port: number;
  readonly protocol: 'http' | 'https';
  readonly csrfToken: string;
  readonly binaryPath?: string;
  readonly sanitizedCommandLine?: string;
  readonly discoveredAt: string;
}

export interface DetectorOptions {
  readonly inspector?: IProcessInspector;
  readonly rpcClient?: AG2RpcClient;
  readonly offlineCooldownMs?: number;
  readonly failureThreshold?: number;
}

/**
 * Antigravity 2 Process & Port Detector
 */
export class AG2ProcessDetector {
  private readonly inspector: IProcessInspector;
  private readonly rpcClient: AG2RpcClient;
  private readonly offlineCooldownMs: number;
  private readonly failureThreshold: number;

  private cachedSession: CachedAG2Session | null = null;
  private lastOfflineCheckAt = 0;
  private consecutiveFailures = 0;

  constructor(options: DetectorOptions = {}) {
    this.inspector = options.inspector || new WindowsProcessInspector();
    this.rpcClient = options.rpcClient || new AG2RpcClient();
    this.offlineCooldownMs = options.offlineCooldownMs ?? 15000; // 15 seconds cooldown
    this.failureThreshold = options.failureThreshold ?? 3;
  }

  /**
   * Return the cached session if alive and healthy.
   */
  public getCachedSession(): CachedAG2Session | null {
    if (!this.cachedSession) return null;
    if (!this.inspector.isPidAlive(this.cachedSession.pid)) {
      this.invalidateCache('Cached PID is no longer alive');
      return null;
    }
    return this.cachedSession;
  }

  /**
   * Invalidate cached session forcing cold rediscovery.
   */
  public invalidateCache(_reason?: string): void {
    if (this.cachedSession) {
      this.cachedSession = null;
    }
  }

  /**
   * Record an RPC failure to trigger circuit breaker when threshold exceeded.
   */
  public recordRpcFailure(_err?: unknown): void {
    this.consecutiveFailures++;
    if (this.consecutiveFailures >= this.failureThreshold) {
      this.invalidateCache(`Exceeded ${this.failureThreshold} consecutive RPC failures`);
      this.consecutiveFailures = 0;
    }
  }

  /**
   * Record an RPC success, resetting failure counter.
   */
  public recordRpcSuccess(): void {
    this.consecutiveFailures = 0;
  }

  /**
   * Discover running Antigravity 2 process and active Connect-RPC port.
   */
  public async discover(): Promise<AG2DiscoveryResult> {
    // 1. Fast-Path: Verify cached session liveness
    const cached = this.getCachedSession();
    if (cached) {
      const publicProcessInfo: AG2ProcessInfo = {
        pid: cached.pid,
        port: cached.port,
        protocol: cached.protocol,
        csrfToken: maskToken(cached.csrfToken),
        commandLine: cached.sanitizedCommandLine,
        binaryPath: cached.binaryPath,
        discoveredAt: cached.discoveredAt
      };

      return {
        isRunning: true,
        status: 'HEALTHY',
        processInfo: publicProcessInfo,
        message: `Antigravity 2 connected (PID ${cached.pid}, port ${cached.port} via ${cached.protocol.toUpperCase()})`
      };
    }

    // 2. Offline Cooldown check: Avoid continuous PowerShell/netstat execution while offline
    const now = Date.now();
    if (
      this.offlineCooldownMs > 0 &&
      this.lastOfflineCheckAt > 0 &&
      now - this.lastOfflineCheckAt < this.offlineCooldownMs
    ) {
      return {
        isRunning: false,
        status: 'OFFLINE',
        processInfo: null,
        message: 'Antigravity 2 is offline (cooldown active)'
      };
    }

    // 3. Cold Discovery: Enumerate language server processes
    let processes: DiscoveredProcessRaw[];
    try {
      processes = await this.inspector.findProcesses();
    } catch (err) {
      this.lastOfflineCheckAt = now;
      return {
        isRunning: false,
        status: 'OFFLINE',
        processInfo: null,
        message: `Process inspection error: ${err instanceof Error ? err.message : String(err)}`
      };
    }

    // Identify Antigravity 2 process
    const ag2Process = this.selectAntigravity2Process(processes);
    if (!ag2Process) {
      this.lastOfflineCheckAt = now;
      return {
        isRunning: false,
        status: 'OFFLINE',
        processInfo: null,
        message: 'Antigravity 2 language server is not running'
      };
    }

    // Process was identified; reset offline scan timestamp
    this.lastOfflineCheckAt = 0;

    // Extract CSRF token from command line arguments
    const csrfToken = this.extractCsrfToken(ag2Process.commandLine);
    if (!csrfToken) {
      return {
        isRunning: true,
        status: 'DEGRADED',
        processInfo: null,
        message: `Antigravity 2 running (PID ${ag2Process.pid}) but CSRF token could not be parsed`
      };
    }

    // Discover listening ports
    let listeningPorts: number[];
    try {
      listeningPorts = await this.inspector.getListeningPorts(ag2Process.pid);
    } catch (err) {
      return {
        isRunning: true,
        status: 'DEGRADED',
        processInfo: null,
        message: `Failed to query ports for PID ${ag2Process.pid}: ${err instanceof Error ? err.message : String(err)}`
      };
    }

    if (listeningPorts.length === 0) {
      return {
        isRunning: true,
        status: 'DEGRADED',
        processInfo: null,
        message: `Antigravity 2 (PID ${ag2Process.pid}) has no active loopback listening ports`
      };
    }

    // Probe candidate ports for active Connect-RPC service (HTTPS first, then HTTP)
    const probeResult = await this.probeCandidatePorts(listeningPorts, csrfToken);
    if (!probeResult) {
      return {
        isRunning: true,
        status: 'DEGRADED',
        processInfo: null,
        message: `Antigravity 2 (PID ${ag2Process.pid}) ports [${listeningPorts.join(', ')}] did not respond to Connect-RPC probe`
      };
    }

    const { port: workingPort, protocol: workingProtocol } = probeResult;
    const discoveredAt = new Date().toISOString();
    const sanitizedCmd = sanitizeCommandLine(ag2Process.commandLine);

    // Cache valid session in memory
    this.cachedSession = {
      pid: ag2Process.pid,
      port: workingPort,
      protocol: workingProtocol,
      csrfToken,
      binaryPath: ag2Process.executablePath,
      sanitizedCommandLine: sanitizedCmd,
      discoveredAt
    };
    this.consecutiveFailures = 0;

    const publicProcessInfo: AG2ProcessInfo = {
      pid: ag2Process.pid,
      port: workingPort,
      protocol: workingProtocol,
      csrfToken: maskToken(csrfToken),
      commandLine: sanitizedCmd,
      binaryPath: ag2Process.executablePath,
      discoveredAt
    };

    return {
      isRunning: true,
      status: 'DISCOVERED',
      processInfo: publicProcessInfo,
      message: `Discovered Antigravity 2 (PID ${ag2Process.pid}, port ${workingPort} via ${workingProtocol.toUpperCase()})`
    };
  }

  /**
   * Filter and identify the true Antigravity 2 language server process.
   */
  public selectAntigravity2Process(
    processes: readonly DiscoveredProcessRaw[]
  ): DiscoveredProcessRaw | null {
    return (
      processes.find((p) => {
        const nameLower = p.name.toLowerCase();
        const cmd = p.commandLine || '';
        const pathLower = (p.executablePath || '').toLowerCase();

        // Must be a language server process
        const isLanguageServer =
          nameLower.includes('language_server') || pathLower.includes('language_server');
        if (!isLanguageServer) return false;

        // Must match Antigravity 2 indicators
        const isAntigravity2 =
          cmd.includes('--standalone') ||
          (cmd.includes('--override_ide_name antigravity') && cmd.includes('--subclient_type hub')) ||
          pathLower.includes('antigravity\\resources\\bin\\language_server.exe') ||
          cmd.toLowerCase().includes('resources\\bin\\language_server.exe');

        return isAntigravity2 && /--csrf_token(?:=|\s+)[A-Za-z0-9_-]+/i.test(cmd);
      }) || null
    );
  }

  /**
   * Extract the CSRF token from process command line string.
   */
  public extractCsrfToken(commandLine: string): string | null {
    const match = commandLine.match(/--csrf_token(?:=|\s+)([A-Za-z0-9_-]+)/i);
    return match ? match[1] : null;
  }

  /**
   * Probe ports in candidate list to find the active Connect-RPC service.
   * Tests HTTPS first (the default for AG2 with --https_server_port 0), then HTTP.
   */
  private async probeCandidatePorts(
    ports: readonly number[],
    csrfToken: string
  ): Promise<{ port: number; protocol: 'http' | 'https' } | null> {
    for (const port of ports) {
      // 1. Try HTTPS
      const isHttps = await this.rpcClient.probePort(port, 'https', csrfToken);
      if (isHttps) {
        return { port, protocol: 'https' };
      }

      // 2. Try HTTP
      const isHttp = await this.rpcClient.probePort(port, 'http', csrfToken);
      if (isHttp) {
        return { port, protocol: 'http' };
      }
    }

    return null;
  }
}
