/**
 * AG2 Router - Cross-Platform Test Runner
 *
 * Discovers compiled test files in dist/test and executes them using
 * Node.js built-in test runner across Windows and Linux (Node 20 & 22).
 */

import { spawn } from 'node:child_process';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

// Resolve project root and test directory independently of current working directory
const __dirname = path.dirname(fileURLToPath(import.meta.url));
const projectRoot = path.resolve(__dirname, '..');
const testDir = path.resolve(projectRoot, 'dist', 'test');

async function discoverTestFiles(dir) {
  let entries;
  try {
    entries = await fs.readdir(dir, { withFileTypes: true });
  } catch (err) {
    if (err.code === 'ENOENT') {
      console.error(`[Test Runner] Error: Test directory "${dir}" does not exist.`);
      console.error('[Test Runner] Please run "npm run build" before running tests.');
      process.exit(1);
    }
    throw err;
  }

  const testFiles = [];
  for (const entry of entries) {
    if (entry.isFile() && entry.name.endsWith('.test.js')) {
      // Use relative paths from projectRoot for clean test reporter output
      testFiles.push(path.join('dist', 'test', entry.name));
    }
  }

  // Deterministic lexicographical sort across all OS filesystems
  return testFiles.sort();
}

async function main() {
  const testFiles = await discoverTestFiles(testDir);

  if (testFiles.length === 0) {
    console.error(`[Test Runner] Error: No *.test.js files found in "${testDir}".`);
    console.error('[Test Runner] Ensure tests compile to dist/test or verify build output.');
    process.exit(1);
  }

  const child = spawn(process.execPath, ['--test', ...testFiles], {
    cwd: projectRoot,
    stdio: 'inherit',
  });

  // Forward termination signals to child process
  const forwardSignal = (signal) => {
    if (!child.killed) {
      child.kill(signal);
    }
  };
  process.on('SIGINT', () => forwardSignal('SIGINT'));
  process.on('SIGTERM', () => forwardSignal('SIGTERM'));

  child.on('error', (err) => {
    console.error('[Test Runner] Failed to spawn test process:', err);
    process.exit(1);
  });

  // Listen to 'close' to guarantee all inherited stdio streams are fully flushed
  child.on('close', (code, signal) => {
    if (code !== null) {
      process.exit(code);
    } else if (signal) {
      try {
        process.kill(process.pid, signal);
      } catch {
        process.exit(1);
      }
    } else {
      process.exit(1);
    }
  });
}

main().catch((err) => {
  console.error('[Test Runner] Unexpected failure:', err);
  process.exit(1);
});
