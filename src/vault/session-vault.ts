/**
 * AG2 Router - Encrypted Multi-Account Session Vault
 *
 * Implements persistent, encrypted storage for Antigravity 2 account sessions.
 *
 * ARCHITECTURAL SPECIFICATION:
 * - Single-Layer DPAPI Protection: Each account's session payload is individually
 *   encrypted with Windows DPAPI CurrentUser.
 * - Double encryption is strictly avoided by design.
 * - Internal Identity Framing: The encrypted plaintext contains accountId, target,
 *   and version metadata to fail closed if a record is altered or mismatched.
 * - Atomic Persistence: Writes to .tmp file before atomic rename to prevent corruption.
 * - Fail-Closed Integrity: Corrupted vault files are NEVER overwritten or wiped.
 *   They throw VaultCorruptionError to preserve user data for recovery.
 * - Storage Path: Defaults to %LOCALAPPDATA%\AG2-Router\vault\sessions.dat.
 */

import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { withCoordinatedFileAccess, writeFileAtomically } from '../persistence/file-coordination.js';
import { IDpapiProvider, WindowsDpapiProvider } from './dpapi.js';
import {
  VAULT_MAGIC,
  VAULT_SCHEMA_VERSION,
  VaultAccountRecord,
  VaultCorruptionError,
  VaultError,
  VaultFileEnvelope,
  VaultMutationReceipt,
  VaultedSessionPlaintext
} from './types.js';

export interface SessionVaultOptions {
  readonly vaultDir?: string;
  readonly dpapiProvider?: IDpapiProvider;
  readonly atomicWriter?: (filePath: string, content: string) => Promise<void>;
}

export class SessionVault {
  private readonly vaultDir: string;
  private readonly vaultFilePath: string;
  private readonly dpapiProvider: IDpapiProvider;
  private readonly atomicWriter: (filePath: string, content: string) => Promise<void>;

  constructor(options: SessionVaultOptions = {}) {
    if (options.vaultDir) {
      this.vaultDir = options.vaultDir;
    } else if (process.env.LOCALAPPDATA) {
      this.vaultDir = path.join(process.env.LOCALAPPDATA, 'AG2-Router', 'vault');
    } else {
      this.vaultDir = path.join(os.homedir(), 'AppData', 'Local', 'AG2-Router', 'vault');
    }

    this.vaultFilePath = path.join(this.vaultDir, 'sessions.dat');
    this.dpapiProvider = options.dpapiProvider || new WindowsDpapiProvider();
    this.atomicWriter = options.atomicWriter ?? writeFileAtomically;
  }

  /**
   * Return the absolute path to the sessions.dat vault file.
   */
  public getVaultPath(): string {
    return this.vaultFilePath;
  }

  /**
   * Return the vault directory path.
   */
  public getVaultDir(): string {
    return this.vaultDir;
  }

  /**
   * Encrypt and store an account session into the vault.
   * Atomically updates sessions.dat.
   */
  public async saveSession(
    accountId: string,
    sessionBlob: Buffer,
    target: string = 'gemini:antigravity'
  ): Promise<void> {
    await this.saveSessionWithReceipt(accountId, sessionBlob, target);
  }

  public async saveSessionWithReceipt(
    accountId: string,
    sessionBlob: Buffer,
    target: string = 'gemini:antigravity'
  ): Promise<VaultMutationReceipt> {
    if (!accountId || typeof accountId !== 'string') {
      throw new VaultError('accountId is required to save session');
    }
    if (!sessionBlob || sessionBlob.length === 0) {
      throw new VaultError('sessionBlob cannot be empty or null');
    }

    // 1. Prepare plaintext envelope with internal identity framing
    const plaintextPayload: VaultedSessionPlaintext = {
      version: 1,
      accountId,
      target,
      credentialBlobBase64: sessionBlob.toString('base64'),
      enrolledAt: new Date().toISOString()
    };

    const plaintextBuffer = Buffer.from(JSON.stringify(plaintextPayload), 'utf8');

    // 2. Encrypt with Windows DPAPI (CurrentUser)
    const encryptedBuffer = await this.dpapiProvider.encrypt(plaintextBuffer);
    const encryptedPayloadBase64 = encryptedBuffer.toString('base64');

    return withCoordinatedFileAccess(this.vaultFilePath, async () => {
      // 3. Load current vault envelope (fails closed if existing file is corrupted)
      const envelope = this.readEnvelope();
      const now = new Date().toISOString();
      const existing = envelope.records[accountId];
      const committedRecord = {
        accountId,
        target,
        encryptedPayloadBase64,
        createdAt: existing ? existing.createdAt : now,
        updatedAt: now
      };
      envelope.records[accountId] = committedRecord;

      // 4. Flush temp content before same-directory replacement.
      await this.writeEnvelope(envelope);
      return {
        accountId,
        committedRecord,
        previousRecord: existing
      };
    });
  }

  public async restoreIfCurrent(receipt: VaultMutationReceipt): Promise<boolean> {
    return withCoordinatedFileAccess(this.vaultFilePath, async () => {
      const envelope = this.readEnvelope();
      const current = envelope.records[receipt.accountId];
      if (!current || !this.recordsEqual(current, receipt.committedRecord)) return false;

      if (receipt.previousRecord) {
        envelope.records[receipt.accountId] = receipt.previousRecord;
      } else {
        delete envelope.records[receipt.accountId];
      }
      await this.writeEnvelope(envelope);
      return true;
    });
  }

  /**
   * Decrypt and retrieve an account session from the vault.
   * Fails closed if the identity framing does not match accountId or target.
   */
  public async getSession(accountId: string): Promise<Buffer | null> {
    if (!accountId || typeof accountId !== 'string') {
      throw new VaultError('accountId is required to retrieve session');
    }

    const record = await withCoordinatedFileAccess(this.vaultFilePath, async () =>
      this.readEnvelope().records[accountId]);
    if (!record) {
      return null;
    }

    const ciphertext = Buffer.from(record.encryptedPayloadBase64, 'base64');
    let decryptedBuffer: Buffer;
    try {
      decryptedBuffer = await this.dpapiProvider.decrypt(ciphertext);
    } catch (err) {
      throw new VaultCorruptionError(
        `Failed to decrypt vaulted session for account '${accountId}': ${err instanceof Error ? err.message : String(err)}`
      );
    }

    let plaintext: VaultedSessionPlaintext;
    try {
      plaintext = JSON.parse(decryptedBuffer.toString('utf8')) as VaultedSessionPlaintext;
    } catch {
      throw new VaultCorruptionError(
        `Vault record for account '${accountId}' decrypted but contained malformed JSON payload`
      );
    } finally {
      // Memory hygiene
      decryptedBuffer.fill(0);
    }

    // Identity Framing Verification
    if (plaintext.version !== 1) {
      throw new VaultCorruptionError(
        `Unsupported vaulted session version '${plaintext.version}' for account '${accountId}'`
      );
    }
    if (plaintext.accountId !== accountId) {
      throw new VaultCorruptionError(
        `Identity framing violation: payload accountId '${plaintext.accountId}' does not match record '${accountId}'`
      );
    }
    if (plaintext.target !== record.target) {
      throw new VaultCorruptionError(
        `Target mismatch: payload target '${plaintext.target}' does not match record target '${record.target}'`
      );
    }

    return Buffer.from(plaintext.credentialBlobBase64, 'base64');
  }

  /**
   * Check if a session exists for the given accountId without decrypting it.
   */
  public async hasSession(accountId: string): Promise<boolean> {
    if (!accountId) return false;
    return withCoordinatedFileAccess(this.vaultFilePath, async () =>
      Boolean(this.readEnvelope().records[accountId]));
  }

  /**
   * Remove a session from the vault.
   */
  public async removeSession(accountId: string): Promise<boolean> {
    if (!accountId) return false;

    return withCoordinatedFileAccess(this.vaultFilePath, async () => {
      const envelope = this.readEnvelope();
      if (!envelope.records[accountId]) return false;
      delete envelope.records[accountId];
      await this.writeEnvelope(envelope);
      return true;
    });
  }

  /**
   * List all account IDs stored in the vault.
   */
  public async listStoredAccountIds(): Promise<string[]> {
    return withCoordinatedFileAccess(this.vaultFilePath, async () =>
      Object.keys(this.readEnvelope().records));
  }

  /**
   * Read and parse the vault file envelope.
   * Throws VaultCorruptionError if the file exists but is corrupted.
   */
  private readEnvelope(): VaultFileEnvelope {
    if (!fs.existsSync(this.vaultFilePath)) {
      return {
        magic: VAULT_MAGIC,
        schemaVersion: VAULT_SCHEMA_VERSION,
        updatedAt: new Date().toISOString(),
        records: {}
      };
    }

    let rawText: string;
    try {
      rawText = fs.readFileSync(this.vaultFilePath, 'utf8');
    } catch (err) {
      throw new VaultError(`Failed to read vault file: ${err instanceof Error ? err.message : String(err)}`);
    }

    if (!rawText || !rawText.trim()) {
      throw new VaultCorruptionError(`Vault file at '${this.vaultFilePath}' is empty or truncated`);
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(rawText);
    } catch {
      throw new VaultCorruptionError(
        `Vault file at '${this.vaultFilePath}' contains invalid JSON and cannot be parsed`
      );
    }

    const envelope = parsed as Partial<VaultFileEnvelope>;
    if (envelope.magic !== VAULT_MAGIC) {
      throw new VaultCorruptionError(
        `Vault file at '${this.vaultFilePath}' does not have valid magic identifier '${VAULT_MAGIC}'`
      );
    }

    if (envelope.schemaVersion !== VAULT_SCHEMA_VERSION) {
      throw new VaultCorruptionError(
        `Unsupported vault schema version '${envelope.schemaVersion}' (expected ${VAULT_SCHEMA_VERSION})`
      );
    }

    if (!envelope.records || typeof envelope.records !== 'object') {
      throw new VaultCorruptionError(`Vault file at '${this.vaultFilePath}' missing valid records dictionary`);
    }

    return envelope as VaultFileEnvelope;
  }

  private recordsEqual(left: VaultAccountRecord, right: VaultAccountRecord): boolean {
    return left.accountId === right.accountId &&
      left.target === right.target &&
      left.encryptedPayloadBase64 === right.encryptedPayloadBase64 &&
      left.createdAt === right.createdAt &&
      left.updatedAt === right.updatedAt;
  }

  /**
   * Write the vault file envelope atomically via a temporary file.
   */
  private async writeEnvelope(envelope: VaultFileEnvelope): Promise<void> {
    const updatedEnvelope: VaultFileEnvelope = {
      ...envelope,
      updatedAt: new Date().toISOString()
    };

    const serialized = JSON.stringify(updatedEnvelope, null, 2);
    try {
      await this.atomicWriter(this.vaultFilePath, serialized);
    } catch (err) {
      throw new VaultError(`Failed to atomically write vault file: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
}
