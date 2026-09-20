/**
 * AG2 Router - Development Runner
 *
 * Builds TypeScript, copies UI assets, and starts the local server.
 */

import { spawn } from 'node:child_process';
import * as path from 'node:path';

function run(cmd, args) {
  return new Promise((resolve, reject) => {
    const isWindows = process.platform === 'win32';
    const executable = isWindows ? `${cmd}.cmd` : cmd;
    const child = spawn(executable, args, { stdio: 'inherit', shell: isWindows });

    child.on('close', (code) => {
      if (code === 0) resolve();
      else reject(new Error(`Command ${cmd} exited with code ${code}`));
    });
  });
}

async function main() {
  console.log('[Dev] Compiling TypeScript...');
  await run('npx', ['tsc']);

  console.log('[Dev] Copying UI assets...');
  const { execSync } = await import('node:child_process');
  execSync('node scripts/copy-ui.mjs', { stdio: 'inherit' });

  console.log('[Dev] Starting AG2 Router server...');
  const appPath = path.resolve('dist', 'src', 'index.js');
  const serverProcess = spawn('node', [appPath], { stdio: 'inherit' });

  const cleanExit = () => {
    serverProcess.kill('SIGINT');
    process.exit(0);
  };

  process.on('SIGINT', cleanExit);
  process.on('SIGTERM', cleanExit);
}

main().catch((err) => {
  console.error('[Dev] Error:', err);
  process.exit(1);
});
