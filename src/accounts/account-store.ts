/**
 * AG2 Router - Account Metadata Storage Implementations
 *
 * Provides thread-safe, non-secret metadata storage for registered accounts.
 * Supports both In-Memory and Local Atomic Filesystem persistence.
 *
 * NOTE: Contains ONLY non-sensitive metadata (email, priority, label).
 * Secrets (OAuth tokens/sessions) are never placed in this store.
 */

import { randomUUID } from 'node:crypto';
import * as fs from 'node:fs/promises';
import * as path from 'node:path';
import {
  AccountMetadata,
  CreateAccountInput,
  IAccountStore,
  UpdateAccountInput
} from './types.js';

interface StoredData {
  version: 1;
  activeAccountId: string | null;
  accounts: AccountMetadata[];
}

/**
 * In-Memory Account Store suitable for testing and isolated runtime environments.
 */
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
    for (const acc of this.accounts.values()) {
      if (acc.email.toLowerCase() === normalized) {
        return acc;
      }
    }
    return null;
  }

  public async addAccount(input: CreateAccountInput): Promise<AccountMetadata> {
    const existing = await this.getAccountByEmail(input.email);
    if (existing) {
      throw new Error(`Account with email '${input.email}' already exists.`);
    }

    const now = new Date().toISOString();
    const account: AccountMetadata = {
      id: `acc_${randomUUID().slice(0, 8)}`,
      email: input.email.trim(),
      name: input.name?.trim(),
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
    if (!existing) {
      return null;
    }

    const updated: AccountMetadata = {
      ...existing,
      name: updates.name !== undefined ? updates.name.trim() : existing.name,
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
    if (existed && this.activeAccountId === id) {
      this.activeAccountId = null;
    }
    return existed;
  }

  public async getActiveAccountId(): Promise<string | null> {
    return this.activeAccountId;
  }

  public async setActiveAccountId(id: string | null): Promise<void> {
    if (id !== null && !this.accounts.has(id)) {
      throw new Error(`Cannot set active account: account with id '${id}' not found.`);
    }
    this.activeAccountId = id;
  }

  public restoreAccounts(accounts: AccountMetadata[], activeAccountId: string | null): void {
    this.accounts.clear();
    for (const acc of accounts) {
      this.accounts.set(acc.id, acc);
    }
    this.activeAccountId = activeAccountId;
  }
}

/**
 * Filesystem-backed non-secret account metadata store.
 * Writes are performed atomically using a temporary file and atomic rename.
 */
export class LocalMetadataAccountStore implements IAccountStore {
  private inMemory: InMemoryAccountStore = new InMemoryAccountStore();
  private filePath: string;
  private isLoaded = false;
  private writeLock = Promise.resolve();

  constructor(filePath?: string) {
    this.filePath = filePath || path.resolve(process.cwd(), 'data', 'accounts.json');
  }

  private async ensureLoaded(): Promise<void> {
    if (this.isLoaded) return;

    try {
      const content = await fs.readFile(this.filePath, 'utf-8');
      const data: StoredData = JSON.parse(content);
      if (Array.isArray(data.accounts)) {
        this.inMemory.restoreAccounts(data.accounts, data.activeAccountId ?? null);
      }
    } catch (err: unknown) {
      const nodeErr = err as NodeJS.ErrnoException;
      if (nodeErr.code !== 'ENOENT') {
        throw new Error(`Failed to read account metadata store from ${this.filePath}: ${nodeErr.message}`);
      }
      // If file does not exist, start with empty data
    }

    this.isLoaded = true;
  }

  private async persist(): Promise<void> {
    // Queue writes to prevent overlapping fs operations
    this.writeLock = this.writeLock.then(async () => {
      const accounts = await this.inMemory.listAccounts();
      const activeAccountId = await this.inMemory.getActiveAccountId();
      const data: StoredData = {
        version: 1,
        activeAccountId,
        accounts
      };

      const dir = path.dirname(this.filePath);
      await fs.mkdir(dir, { recursive: true });

      const tmpFile = `${this.filePath}.${Date.now()}.${Math.random().toString(36).slice(2)}.tmp`;
      const json = JSON.stringify(data, null, 2);
      await fs.writeFile(tmpFile, json, 'utf-8');
      await fs.rename(tmpFile, this.filePath);
    });

    return this.writeLock;
  }

  public async listAccounts(): Promise<AccountMetadata[]> {
    await this.ensureLoaded();
    return this.inMemory.listAccounts();
  }

  public async getAccount(id: string): Promise<AccountMetadata | null> {
    await this.ensureLoaded();
    return this.inMemory.getAccount(id);
  }

  public async getAccountByEmail(email: string): Promise<AccountMetadata | null> {
    await this.ensureLoaded();
    return this.inMemory.getAccountByEmail(email);
  }

  public async addAccount(input: CreateAccountInput): Promise<AccountMetadata> {
    await this.ensureLoaded();
    const created = await this.inMemory.addAccount(input);
    await this.persist();
    return created;
  }

  public async updateAccount(id: string, updates: UpdateAccountInput): Promise<AccountMetadata | null> {
    await this.ensureLoaded();
    const updated = await this.inMemory.updateAccount(id, updates);
    if (updated) {
      await this.persist();
    }
    return updated;
  }

  public async removeAccount(id: string): Promise<boolean> {
    await this.ensureLoaded();
    const removed = await this.inMemory.removeAccount(id);
    if (removed) {
      await this.persist();
    }
    return removed;
  }

  public async getActiveAccountId(): Promise<string | null> {
    await this.ensureLoaded();
    return this.inMemory.getActiveAccountId();
  }

  public async setActiveAccountId(id: string | null): Promise<void> {
    await this.ensureLoaded();
    await this.inMemory.setActiveAccountId(id);
    await this.persist();
  }
}
