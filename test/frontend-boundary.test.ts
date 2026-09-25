import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import * as path from 'node:path';

// Compiled to dist/test; the repository root is two levels above this file.
const repoRoot = fileURLToPath(new URL('../../', import.meta.url));
const frontendDir = path.join(repoRoot, 'frontend');
const forbiddenDir = path.join(repoRoot, 'src', 'dashboard');

const SCAN_EXTENSIONS = new Set(['.ts', '.tsx', '.js', '.jsx', '.mjs', '.svelte']);

const SPECIFIER_PATTERNS = [
  /(?:import|export)\s+(?:type\s+)?[^'";]*?from\s*['"]([^'"]+)['"]/g,
  /import\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
  /(?:^|[\s;])import\s+['"]([^'"]+)['"]/g
];

function listSourceFiles(dir: string): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'node_modules') continue;
      found.push(...listSourceFiles(fullPath));
    } else if (SCAN_EXTENSIONS.has(path.extname(entry.name))) {
      found.push(fullPath);
    }
  }
  return found;
}

function resolvesIntoForbiddenDir(fromDir: string, specifier: string): boolean {
  if (!specifier.startsWith('.')) return false;
  const resolved = path.resolve(fromDir, specifier);
  const relative = path.relative(forbiddenDir, resolved);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

function extractSpecifiers(content: string): string[] {
  const specifiers: string[] = [];
  for (const pattern of SPECIFIER_PATTERNS) {
    pattern.lastIndex = 0;
    for (const match of content.matchAll(pattern)) {
      if (match[1]) specifiers.push(match[1]);
    }
  }
  return specifiers;
}

describe('Authored frontend boundary', () => {
  it('contains no module specifier that resolves into src/dashboard', () => {
    assert.equal(existsSync(frontendDir), true, `Expected frontend source tree at ${frontendDir}`);
    const files = listSourceFiles(frontendDir);
    assert.ok(files.length >= 10, `Expected to scan a populated frontend tree, found ${files.length} files`);

    const violations: string[] = [];
    for (const file of files) {
      const content = readFileSync(file, 'utf8');
      for (const specifier of extractSpecifiers(content)) {
        if (resolvesIntoForbiddenDir(path.dirname(file), specifier)) {
          violations.push(`${path.relative(repoRoot, file)} -> ${specifier}`);
        }
      }
    }

    assert.deepEqual(
      violations,
      [],
      'Authored frontend code must not import from src/dashboard; move the helper implementation into frontend/'
    );
  });
});
