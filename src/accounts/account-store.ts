/**
 * Non-secret account metadata stores shared by the TypeScript oracle and .NET runtime.
 */
import { randomUUID } from 'node:crypto';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { withCoordinatedFileAccess, writeFileAtomically } from '../persistence/file-coordination.js';
import { AccountMetadata, CreateAccountInput, IAccountStore, UpdateAccountInput } from './types.js';

interface StoredData {
  version: 1;
  activeAccountId: string | null;
  accounts: AccountMetadata[];
}

const VALIDATION_STATUSES = new Set(['VALID', 'EXPIRED', 'UNVALIDATED', 'FAILED']);
const ENVELOPE_KEYS = new Set(['version', 'activeAccountId', 'accounts']);
const REQUIRED_ACCOUNT_KEYS = new Set([
  'id', 'email', 'priority', 'isReserve', 'validationStatus',
  'createdAt', 'updatedAt', 'lastActiveAt'
]);
const OPTIONAL_ACCOUNT_KEYS = new Set(['name', 'hasVaultedSession', 'notes', 'alias']);

export function resolveAccountMetadataPath(
  dataDirectory: string | undefined = process.env.DATA_DIR,
  currentDirectory: string = process.cwd(),
  localApplicationData: string | undefined = process.env.LOCALAPPDATA
): string {
  const directory = dataDirectory && dataDirectory.trim()
    ? path.resolve(currentDirectory, dataDirectory)
    : path.resolve(
        localApplicationData && localApplicationData.trim()
          ? localApplicationData
          : path.join(os.homedir(), 'AppData', 'Local'),
        'AG2-Router',
        'data');
  return path.resolve(directory, 'accounts.json');
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isValidDate(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && !Number.isNaN(Date.parse(value));
}

function accountsEqual(left: AccountMetadata, right: AccountMetadata): boolean {
  return left.id === right.id &&
    left.email === right.email &&
    left.name === right.name &&
    left.priority === right.priority &&
    left.isReserve === right.isReserve &&
    left.validationStatus === right.validationStatus &&
    left.hasVaultedSession === right.hasVaultedSession &&
    left.createdAt === right.createdAt &&
    left.updatedAt === right.updatedAt &&
    left.lastActiveAt === right.lastActiveAt &&
    left.notes === right.notes &&
    left.alias === right.alias;
}

function parseStoredData(raw: string, filePath: string): StoredData {
  if (!raw.trim()) throw new Error(`Account metadata store '${filePath}' is empty or truncated.`);

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (error: unknown) {
    throw new Error(`Account metadata store '${filePath}' contains invalid JSON: ${(error as Error).message}`);
  }

  if (!isRecord(parsed) || Object.keys(parsed).some((key) => !ENVELOPE_KEYS.has(key))) {
    throw new Error(`Account metadata store '${filePath}' violates schema version 1.`);
  }
  if (parsed.version !== 1 || !Array.isArray(parsed.accounts) ||
      !(parsed.activeAccountId === null || typeof parsed.activeAccountId === 'string')) {
    throw new Error(`Account metadata store '${filePath}' has invalid version, accounts, or activeAccountId.`);
  }

  const ids = new Set<string>();
  const emails = new Set<string>();
  const accounts: AccountMetadata[] = parsed.accounts.map((candidate, index) => {
    if (!isRecord(candidate)) throw new Error(`Account metadata record ${index} is not an object.`);
    const keys = Object.keys(candidate);
    if (keys.some((key) => !REQUIRED_ACCOUNT_KEYS.has(key) && !OPTIONAL_ACCOUNT_KEYS.has(key)) ||
        Array.from(REQUIRED_ACCOUNT_KEYS).some((key) => !(key in candidate))) {
      throw new Error(`Account metadata record ${index} violates schema version 1.`);
    }

    const id = candidate.id;
    const email = candidate.email;
    const validationStatus = candidate.validationStatus;
    if (typeof id !== 'string' || !id.trim() || typeof email !== 'string' || !email.includes('@') ||
        typeof candidate.priority !== 'number' || !Number.isInteger(candidate.priority) ||
        typeof candidate.isReserve !== 'boolean' ||
        typeof validationStatus !== 'string' || !VALIDATION_STATUSES.has(validationStatus) ||
        !isValidDate(candidate.createdAt) || !isValidDate(candidate.updatedAt) ||
        !(candidate.lastActiveAt === null || isValidDate(candidate.lastActiveAt)) ||
        !(candidate.name === undefined || typeof candidate.name === 'string') ||
        !(candidate.notes === undefined || typeof candidate.notes === 'string') ||
        !(candidate.alias === undefined || typeof candidate.alias === 'string') ||
        !(candidate.hasVaultedSession === undefined || typeof candidate.hasVaultedSession === 'boolean')) {
      throw new Error(`Account metadata record '${String(id)}' contains invalid fields.`);
    }

    const normalizedEmail = email.toLowerCase();
    if (ids.has(id) || emails.has(normalizedEmail)) {
      throw new Error(`Account metadata store contains duplicate id or email for '${id}'.`);
    }
    ids.add(id);
    emails.add(normalizedEmail);

    return candidate as unknown as AccountMetadata;
  });

  if (parsed.activeAccountId !== null && !ids.has(parsed.activeAccountId)) {
    throw new Error(`Active account id '${parsed.activeAccountId}' does not reference a stored account.`);
  }
  return { version: 1, activeAccountId: parsed.activeAccountId, accounts };
}

export class InMemoryAccountStore implements IAccountStore {
  private accounts = new Map<string, AccountMetadata>();
  private activeAccountId: string | null = null;

  public async listAccounts(): Promise<AccountMetadata[]> {
    return Array.from(this.accounts.values()).sort((a, b) => a.priority - b.priority);
  }

  public async getAccount(id: string): Promise<AccountMetadata | null> {
    return this.accounts.get(id) ?? null;
  }

  public async getAccountByEmail(email: string): Promise<AccountMetadata | null> {
    const normalized = email.trim().toLowerCase();
    return Array.from(this.accounts.values()).find((account) => account.email.toLowerCase() === normalized) ?? null;
  }

  public async addAccount(input: CreateAccountInput): Promise<AccountMetadata> {
    const existing = await this.getAccountByEmail(input.email);
    if (existing) throw new Error(`Account with email '${input.email}' already exists.`);

    const now = new Date().toISOString();
    const account: AccountMetadata = {
      id: `acc_${randomUUID().slice(0, 8)}`,
      email: input.email.trim(),
      name: input.name?.trim(),
      alias: input.alias && input.alias.trim().length > 0 ? input.alias.trim() : undefined,
      priority: input.priority ?? (this.accounts.size + 1),
      isReserve: Boolean(input.isReserve),
      validationStatus: 'UNVALIDATED',
      hasVaultedSession: Boolean(input.hasVaultedSession),
      createdAt: now,
      updatedAt: now,
      lastActiveAt: null,
      notes: input.notes
    };
    this.accounts.set(account.id, account);
    return account;
  }

  public async updateAccount(id: string, updates: UpdateAccountInput): Promise<AccountMetadata | null> {
    const existing = this.accounts.get(id);
    if (!existing) return null;
    const updated: AccountMetadata = {
      ...existing,
      name: updates.name !== undefined ? updates.name.trim() : existing.name,
      alias: updates.alias !== undefined
        ? (updates.alias.trim().length > 0 ? updates.alias.trim() : undefined)
        : existing.alias,
      priority: updates.priority !== undefined ? updates.priority : existing.priority,
      isReserve: updates.isReserve !== undefined ? updates.isReserve : existing.isReserve,
      validationStatus: updates.validationStatus ?? existing.validationStatus,
      hasVaultedSession: updates.hasVaultedSession !== undefined ? updates.hasVaultedSession : existing.hasVaultedSession,
      lastActiveAt: updates.lastActiveAt !== undefined ? updates.lastActiveAt : existing.lastActiveAt,
      notes: updates.notes !== undefined ? updates.notes : existing.notes,
      updatedAt: new Date().toISOString()
    };
    this.accounts.set(id, updated);
    return updated;
  }

  public async removeAccount(id: string): Promise<boolean> {
    const existed = this.accounts.delete(id);
    if (existed && this.activeAccountId === id) this.activeAccountId = null;
    return existed;
  }

  public async removeAccountIfUnchanged(expected: AccountMetadata): Promise<boolean> {
    const current = this.accounts.get(expected.id);
    if (!current || !accountsEqual(current, expected)) return false;
    this.accounts.delete(expected.id);
    if (this.activeAccountId === expected.id) this.activeAccountId = null;
    return true;
  }

  public async getActiveAccountId(): Promise<string | null> { return this.activeAccountId; }

  public async setActiveAccountId(id: string | null): Promise<void> {
    if (id !== null && !this.accounts.has(id)) {
      throw new Error(`Cannot set active account: account with id '${id}' not found.`);
    }
    this.activeAccountId = id;
  }

  public async compareExchangeActiveAccountId(
    expectedId: string | null,
    newId: string | null
  ): Promise<boolean> {
    if (newId !== null && !this.accounts.has(newId)) {
      throw new Error(`Cannot set active account: account with id '${newId}' not found.`);
    }
    if (this.activeAccountId !== expectedId) return false;
    this.activeAccountId = newId;
    return true;
  }

  public restoreAccounts(accounts: AccountMetadata[], activeAccountId: string | null): void {
    this.accounts.clear();
    for (const account of accounts) this.accounts.set(account.id, account);
    this.activeAccountId = activeAccountId;
  }
}

export class LocalMetadataAccountStore implements IAccountStore {
  private readonly filePath: string;
  private readonly atomicWriter: (filePath: string, content: string) => Promise<void>;
  private state: StoredData = { version: 1, activeAccountId: null, accounts: [] };
  private hasObservedExistingFile = false;

  constructor(
    filePath?: string,
    atomicWriter: (filePath: string, content: string) => Promise<void> = writeFileAtomically
  ) {
    this.filePath = path.resolve(filePath ?? resolveAccountMetadataPath());
    this.atomicWriter = atomicWriter;
  }

  public getFilePath(): string { return this.filePath; }

  private async readState(): Promise<StoredData> {
    try {
      const content = await fs.readFile(this.filePath, 'utf8');
      this.hasObservedExistingFile = true;
      return parseStoredData(content, this.filePath);
    } catch (error: unknown) {
      const nodeError = error as NodeJS.ErrnoException;
      if (nodeError.code === 'ENOENT' && !this.hasObservedExistingFile) {
        return { version: 1, activeAccountId: null, accounts: [] };
      }
      if (nodeError.code === 'ENOENT') {
        throw new Error(`Account metadata store '${this.filePath}' disappeared after it was loaded.`);
      }
      throw error;
    }
  }

  private async readCurrent(): Promise<StoredData> {
    return withCoordinatedFileAccess(this.filePath, async () => {
      this.state = await this.readState();
      return this.state;
    });
  }

  private async mutate<T>(derive: (current: StoredData) => { next: StoredData; result: T; changed: boolean }): Promise<T> {
    return withCoordinatedFileAccess(this.filePath, async () => {
      const current = await this.readState();
      this.state = current;
      const { next, result, changed } = derive(current);
      if (!changed) return result;
      await this.atomicWriter(this.filePath, JSON.stringify(next, null, 2));
      this.state = next;
      this.hasObservedExistingFile = true;
      return result;
    });
  }

  public async listAccounts(): Promise<AccountMetadata[]> {
    const current = await this.readCurrent();
    return [...current.accounts].sort((a, b) => a.priority - b.priority);
  }

  public async getAccount(id: string): Promise<AccountMetadata | null> {
    const current = await this.readCurrent();
    return current.accounts.find((account) => account.id === id) ?? null;
  }

  public async getAccountByEmail(email: string): Promise<AccountMetadata | null> {
    const current = await this.readCurrent();
    const normalized = email.trim().toLowerCase();
    return current.accounts.find((account) => account.email.toLowerCase() === normalized) ?? null;
  }

  public addAccount(input: CreateAccountInput): Promise<AccountMetadata> {
    if (!input.email?.includes('@')) throw new Error('A valid email address is required.');
    return this.mutate((current) => {
      const email = input.email.trim();
      if (current.accounts.some((account) => account.email.toLowerCase() === email.toLowerCase())) {
        throw new Error(`Account with email '${input.email}' already exists.`);
      }
      const now = new Date().toISOString();
      const account: AccountMetadata = {
        id: `acc_${randomUUID().slice(0, 8)}`,
        email,
        name: input.name?.trim(),
        alias: input.alias && input.alias.trim().length > 0 ? input.alias.trim() : undefined,
        priority: input.priority ?? current.accounts.length + 1,
        isReserve: Boolean(input.isReserve),
        validationStatus: 'UNVALIDATED',
        hasVaultedSession: Boolean(input.hasVaultedSession),
        createdAt: now,
        updatedAt: now,
        lastActiveAt: null,
        notes: input.notes
      };
      return { next: { ...current, accounts: [...current.accounts, account] }, result: account, changed: true };
    });
  }

  public updateAccount(id: string, updates: UpdateAccountInput): Promise<AccountMetadata | null> {
    return this.mutate((current) => {
      const index = current.accounts.findIndex((account) => account.id === id);
      if (index < 0) return { next: current, result: null, changed: false };
      const existing = current.accounts[index];
      const updated: AccountMetadata = {
        ...existing,
        name: updates.name !== undefined ? updates.name.trim() : existing.name,
        alias: updates.alias !== undefined
          ? (updates.alias.trim().length > 0 ? updates.alias.trim() : undefined)
          : existing.alias,
        priority: updates.priority ?? existing.priority,
        isReserve: updates.isReserve ?? existing.isReserve,
        validationStatus: updates.validationStatus ?? existing.validationStatus,
        hasVaultedSession: updates.hasVaultedSession ?? existing.hasVaultedSession,
        lastActiveAt: updates.lastActiveAt !== undefined ? updates.lastActiveAt : existing.lastActiveAt,
        notes: updates.notes !== undefined ? updates.notes : existing.notes,
        updatedAt: new Date().toISOString()
      };
      const accounts = [...current.accounts];
      accounts[index] = updated;
      return { next: { ...current, accounts }, result: updated, changed: true };
    });
  }

  public removeAccount(id: string): Promise<boolean> {
    return this.mutate((current) => {
      const accounts = current.accounts.filter((account) => account.id !== id);
      if (accounts.length === current.accounts.length) return { next: current, result: false, changed: false };
      return {
        next: { ...current, accounts, activeAccountId: current.activeAccountId === id ? null : current.activeAccountId },
        result: true,
        changed: true
      };
    });
  }

  public removeAccountIfUnchanged(expected: AccountMetadata): Promise<boolean> {
    return this.mutate((current) => {
      const candidate = current.accounts.find((account) => account.id === expected.id);
      if (!candidate || !accountsEqual(candidate, expected)) {
        return { next: current, result: false, changed: false };
      }
      return {
        next: {
          ...current,
          accounts: current.accounts.filter((account) => account.id !== expected.id),
          activeAccountId: current.activeAccountId === expected.id ? null : current.activeAccountId
        },
        result: true,
        changed: true
      };
    });
  }

  public async getActiveAccountId(): Promise<string | null> {
    return (await this.readCurrent()).activeAccountId;
  }

  public setActiveAccountId(id: string | null): Promise<void> {
    return this.mutate((current) => {
      if (id !== null && !current.accounts.some((account) => account.id === id)) {
        throw new Error(`Cannot set active account: account with id '${id}' not found.`);
      }
      return { next: { ...current, activeAccountId: id }, result: undefined, changed: current.activeAccountId !== id };
    });
  }

  public compareExchangeActiveAccountId(
    expectedId: string | null,
    newId: string | null
  ): Promise<boolean> {
    return this.mutate((current) => {
      if (newId !== null && !current.accounts.some((account) => account.id === newId)) {
        throw new Error(`Cannot set active account: account with id '${newId}' not found.`);
      }
      if (current.activeAccountId !== expectedId) {
        return { next: current, result: false, changed: false };
      }
      return {
        next: { ...current, activeAccountId: newId },
        result: true,
        changed: current.activeAccountId !== newId
      };
    });
  }
}
