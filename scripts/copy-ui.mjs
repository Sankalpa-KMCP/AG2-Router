/**
 * AG2 Router - UI Asset Copy Script
 *
 * Copies static assets from src/ui to dist/src/ui during build.
 */

import * as fs from 'node:fs/promises';
import * as path from 'node:path';

async function copyDir(src, dest) {
  await fs.mkdir(dest, { recursive: true });
  const entries = await fs.readdir(src, { withFileTypes: true });

  for (const entry of entries) {
    const srcPath = path.join(src, entry.name);
    const destPath = path.join(dest, entry.name);

    if (entry.isDirectory()) {
      await copyDir(srcPath, destPath);
    } else {
      await fs.copyFile(srcPath, destPath);
    }
  }
}

async function main() {
  const srcUi = path.resolve('src', 'ui');
  const distUi = path.resolve('dist', 'src', 'ui');

  try {
    await copyDir(srcUi, distUi);
    console.log(`Copied static UI assets to ${distUi}`);
  } catch (err) {
    console.error('Failed to copy UI assets:', err);
    process.exit(1);
  }
}

main();
