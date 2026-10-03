/**
 * AG2 Router - UI Asset Copy Script
 *
 * Build Pipeline Context:
 * - Authored Svelte 5 / TypeScript dashboard source lives in `frontend/`.
 * - `npm run build` invokes Vite to compile the authored frontend into static
 *   production assets emitted directly to `src/ui/`.
 * - When building the Node/TypeScript reference server (`npm run build:server`),
 *   `tsc` outputs compiled JavaScript into `dist/`, but TypeScript compiler does
 *   not natively copy static non-code assets.
 * - This script bridges that gap by recursively copying `src/ui/` to `dist/src/ui/`,
 *   ensuring that `dist/src/server/server.js` finds its required static bundle files
 *   when served in production/test Node environments.
 * - Note: The native .NET application (`AG2Router.App`) bundles static UI assets
 *   separately via MSBuild linking from `src/ui/` to `wwwroot` in `AG2Router.App.csproj`.
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
