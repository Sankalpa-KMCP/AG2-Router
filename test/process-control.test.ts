import test from 'node:test';
import assert from 'node:assert/strict';
import {
  parseCommandLineArguments,
  redactLaunchArgs,
  WindowsProcessController,
  ProcessControlError,
  AG2ProcessLaunchSpec
} from '../src/ag2/process-control.js';
import { IProcessInspector, DiscoveredProcessRaw } from '../src/ag2/discovery.js';

class MockProcessInspector implements IProcessInspector {
  public processes: DiscoveredProcessRaw[] = [];
  public alivePids = new Set<number>();

  public async findProcesses(): Promise<DiscoveredProcessRaw[]> {
    return this.processes;
  }

  public isPidAlive(pid: number): boolean {
    return this.alivePids.has(pid);
  }

  public async getListeningPorts(pid: number): Promise<number[]> {
    return this.isPidAlive(pid) ? [51768] : [];
  }
}

test('parseCommandLineArguments - Tokenizes Windows command line strings correctly', () => {
  assert.deepStrictEqual(parseCommandLineArguments(''), []);
  assert.deepStrictEqual(parseCommandLineArguments('   '), []);

  // Simple arguments
  assert.deepStrictEqual(
    parseCommandLineArguments('node server.js --port 8080'),
    ['node', 'server.js', '--port', '8080']
  );

  // Quoted path with spaces
  assert.deepStrictEqual(
    parseCommandLineArguments('"C:\\Program Files\\Antigravity\\language_server.exe" --port 51768'),
    ['C:\\Program Files\\Antigravity\\language_server.exe', '--port', '51768']
  );

  // Complex mixture of quotes, flags, and values
  const cmdLine = '"C:\\Users\\user\\language_server.exe" --csrf_token "secret-token-123" --flag=val --dir="D:\\test path"';
  const tokens = parseCommandLineArguments(cmdLine);
  assert.deepStrictEqual(tokens, [
    'C:\\Users\\user\\language_server.exe',
    '--csrf_token',
    'secret-token-123',
    '--flag=val',
    '--dir=D:\\test path'
  ]);
});

test('redactLaunchArgs - Sanitizes sensitive flags and tokens', () => {
  const args = [
    '--port',
    '51768',
    '--csrf_token',
    'secret-uuid-token-xyz',
    '--host_bridge_token=super-secret-bridge',
    '--model_quota',
    'standard',
    '--password',
    'mypassword123'
  ];

  const sanitized = redactLaunchArgs(args);
  assert.deepStrictEqual(sanitized, [
    '--port',
    '51768',
    '--csrf_token',
    '[REDACTED]',
    '--host_bridge_token=[REDACTED]',
    '--model_quota',
    'standard',
    '--password',
    '[REDACTED]'
  ]);
});

test('WindowsProcessController - captureLaunchSpec validates PID and executable provenance', async () => {
  const inspector = new MockProcessInspector();
  const controller = new WindowsProcessController(inspector);

  // Invalid PID
  await assert.rejects(
    () => controller.captureLaunchSpec(0),
    (err: ProcessControlError) => {
      assert.match(err.message, /Invalid PID/);
      return true;
    }
  );

  // Process not found
  await assert.rejects(
    () => controller.captureLaunchSpec(9999),
    (err: ProcessControlError) => {
      assert.match(err.message, /Process with PID 9999 was not found/);
      return true;
    }
  );

  // Non-AG2 binary (provenance failure)
  inspector.processes = [
    {
      pid: 1234,
      name: 'malicious.exe',
      executablePath: 'C:\\Windows\\System32\\cmd.exe',
      commandLine: 'cmd.exe /c calc.exe'
    }
  ];

  await assert.rejects(
    () => controller.captureLaunchSpec(1234),
    (err: ProcessControlError) => {
      assert.match(err.message, /not a recognized Antigravity 2 language server binary/);
      return true;
    }
  );

  // Valid AG2 language_server process
  inspector.processes = [
    {
      pid: 22440,
      name: 'language_server_windows_x64.exe',
      executablePath: 'C:\\Users\\user\\AppData\\Local\\Programs\\Antigravity\\language_server_windows_x64.exe',
      commandLine: '"C:\\Users\\user\\AppData\\Local\\Programs\\Antigravity\\language_server_windows_x64.exe" --port 51768 --csrf_token secret-123 --workspace "D:\\test"'
    }
  ];

  const spec = await controller.captureLaunchSpec(22440);
  assert.strictEqual(spec.pid, 22440);
  assert.strictEqual(spec.executablePath, 'C:\\Users\\user\\AppData\\Local\\Programs\\Antigravity\\language_server_windows_x64.exe');
  assert.deepStrictEqual(spec.rawArgs, ['--port', '51768', '--csrf_token', 'secret-123', '--workspace', 'D:\\test']);
  assert.deepStrictEqual(spec.sanitizedArgs, ['--port', '51768', '--csrf_token', '[REDACTED]', '--workspace', 'D:\\test']);
  assert.ok(spec.capturedAt);
});

test('WindowsProcessController - launchProcess rejects unauthorized executables', async () => {
  const inspector = new MockProcessInspector();
  const controller = new WindowsProcessController(inspector);

  const invalidSpec: AG2ProcessLaunchSpec = {
    pid: 100,
    executablePath: 'C:\\Windows\\System32\\notepad.exe',
    rawArgs: ['test.txt'],
    sanitizedArgs: ['test.txt'],
    capturedAt: new Date().toISOString()
  };

  await assert.rejects(
    () => controller.launchProcess(invalidSpec),
    (err: ProcessControlError) => {
      assert.match(err.message, /not an authorized Antigravity 2 binary/);
      return true;
    }
  );
});
