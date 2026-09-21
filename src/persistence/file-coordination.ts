import * as fs from 'node:fs/promises';
import * as path from 'node:path';

const pathLocks = new Map<string, Promise<void>>();
const LOCK_RETRY_MS = 25;
const LOCK_TIMEOUT_MS = 15_000;

function canonicalPath(filePath: string): string {
  const full = path.resolve(filePath);
  return process.platform === 'win32' ? full.toLowerCase() : full;
}

async function withInProcessPathLock<T>(filePath: string, action: () => Promise<T>): Promise<T> {
  const key = canonicalPath(filePath);
  const previous = pathLocks.get(key) ?? Promise.resolve();
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const tail = previous.catch(() => undefined).then(() => gate);
  pathLocks.set(key, tail);

  await previous.catch(() => undefined);
  try {
    return await action();
  } finally {
    release();
    if (pathLocks.get(key) === tail) pathLocks.delete(key);
  }
}

async function acquireCrossProcessLease(filePath: string): Promise<() => Promise<void>> {
  const lockPath = `${path.resolve(filePath)}.lock`;
  await fs.mkdir(path.dirname(lockPath), { recursive: true });
  const started = Date.now();

  while (true) {
    try {
      const handle = await fs.open(lockPath, 'wx', 0o600);
      try {
        if (process.platform === 'win32') {
          // Windows keeps the name reserved while this open handle is delete-pending and
          // removes it automatically on close or process termination. This avoids unsafe
          // PID/token stale-lock reclamation races.
          await fs.unlink(lockPath);
        }
        await handle.writeFile(JSON.stringify({ pid: process.pid, createdAt: new Date().toISOString() }), 'utf8');
        await handle.sync();
      } catch (error) {
        await handle.close().catch(() => undefined);
        await fs.unlink(lockPath).catch(() => undefined);
        throw error;
      }

      return async () => {
        await handle.close().catch(() => undefined);
        if (process.platform !== 'win32') await fs.unlink(lockPath).catch(() => undefined);
      };
    } catch (error: unknown) {
      const nodeError = error as NodeJS.ErrnoException;
      if (!['EEXIST', 'EACCES', 'EPERM'].includes(nodeError.code ?? '')) throw error;

      if (Date.now() - started >= LOCK_TIMEOUT_MS) {
        throw new Error(`Timed out waiting for persistence lock '${lockPath}'.`);
      }
      await new Promise((resolve) => setTimeout(resolve, LOCK_RETRY_MS));
    }
  }
}

export async function withCoordinatedFileAccess<T>(
  filePath: string,
  action: () => Promise<T>
): Promise<T> {
  return withInProcessPathLock(filePath, async () => {
    const release = await acquireCrossProcessLease(filePath);
    try {
      return await action();
    } finally {
      await release();
    }
  });
}

/**
 * Flushes a same-directory temp file before atomic rename. This guarantees that
 * the replacement is never attempted with merely buffered file content; it does
 * not claim perfect directory-entry durability on every filesystem/controller.
 */
export async function writeFileAtomically(filePath: string, content: string): Promise<void> {
  const fullPath = path.resolve(filePath);
  await fs.mkdir(path.dirname(fullPath), { recursive: true });
  const tempPath = `${fullPath}.${process.pid}.${Date.now()}.${Math.random().toString(36).slice(2)}.tmp`;
  let handle: fs.FileHandle | undefined;
  let replaced = false;

  try {
    handle = await fs.open(tempPath, 'wx', 0o600);
    await handle.writeFile(content, 'utf8');
    await handle.sync();
    await handle.close();
    handle = undefined;
    await fs.rename(tempPath, fullPath);
    replaced = true;
  } finally {
    if (handle) await handle.close().catch(() => undefined);
    if (!replaced) await fs.unlink(tempPath).catch(() => undefined);
  }
}
