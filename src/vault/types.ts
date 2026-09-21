/**
 * AG2 Router - Vault Types & Schema Definitions
 */

export const VAULT_MAGIC = 'AG2_ROUTER_SESSION_VAULT';
export const VAULT_SCHEMA_VERSION = 1;

/**
 * Metadata and encrypted payload for an individual stored account session.
 * Each account session is individually encrypted using Windows DPAPI CurrentUser.
 */
export interface VaultAccountRecord {
  readonly accountId: string;
  readonly target: string;
  readonly encryptedPayloadBase64: string;
  readonly createdAt: string;
  readonly updatedAt: string;
}

export interface VaultMutationReceipt {
  readonly accountId: string;
  readonly committedRecord: VaultAccountRecord;
  readonly previousRecord?: VaultAccountRecord;
}

/**
 * Top-level versioned file envelope for `sessions.dat`.
 * Contains metadata and the dictionary of account records.
 * Note: Double encryption is avoided by architectural design.
 */
export interface VaultFileEnvelope {
  readonly magic: typeof VAULT_MAGIC;
  readonly schemaVersion: number;
  readonly updatedAt: string;
  readonly records: Record<string, VaultAccountRecord>;
}

/**
 * Internal plaintext payload structure before DPAPI encryption.
 * Enforces identity framing to prevent record swapping or identity mismatches.
 */
export interface VaultedSessionPlaintext {
  readonly version: 1;
  readonly accountId: string;
  readonly target: string;
  readonly credentialBlobBase64: string;
  readonly enrolledAt: string;
}

export class VaultCorruptionError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'VaultCorruptionError';
  }
}

export class VaultError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'VaultError';
  }
}
