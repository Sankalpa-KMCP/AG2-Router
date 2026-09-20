/**
 * AG2 Router - Account Domain Models & Storage Interfaces
 *
 * Defines non-sensitive account metadata, state representations,
 * and the account store abstraction.
 *
 * SECURITY: Under no circumstances should plaintext OAuth tokens,
 * passwords, or secrets be placed in these structures or stored in metadata.
 */

export type AccountValidationStatus =
  | 'VALID'
  | 'EXPIRED'
  | 'UNVALIDATED'
  | 'FAILED';

/**
 * Public/non-secret metadata for a registered Antigravity 2 account.
 */
export interface AccountMetadata {
  readonly id: string;
  readonly email: string;
  readonly name?: string;
  /**
   * Router preference priority. Lower integer represents higher preference (1 is top priority).
   */
  readonly priority: number;
  /**
   * Reserve accounts are only selected if all standard accounts are exhausted.
   */
  readonly isReserve: boolean;
  readonly validationStatus: AccountValidationStatus;
  readonly hasVaultedSession?: boolean;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly lastActiveAt: string | null;
  readonly notes?: string;
}

/**
 * Input structure when registering a new account.
 */
export interface CreateAccountInput {
  readonly email: string;
  readonly name?: string;
  readonly priority?: number;
  readonly isReserve?: boolean;
  readonly hasVaultedSession?: boolean;
  readonly notes?: string;
}

/**
 * Input structure when updating an existing account's metadata.
 */
export interface UpdateAccountInput {
  readonly name?: string;
  readonly priority?: number;
  readonly isReserve?: boolean;
  readonly validationStatus?: AccountValidationStatus;
  readonly hasVaultedSession?: boolean;
  readonly lastActiveAt?: string | null;
  readonly notes?: string;
}

/**
 * Cached quota overview for an account.
 */
export interface AccountQuotaSummary {
  readonly remainingFraction: number;
  readonly promptCredits?: number;
  readonly flowCredits?: number;
  readonly lastUpdated: string;
}

/**
 * Enriched account summary presented to dashboard and routing selector.
 */
export interface AccountSummary {
  readonly metadata: AccountMetadata;
  readonly isActive: boolean;
  readonly hasVaultedSession: boolean;
  readonly quota: AccountQuotaSummary | null;
}

/**
 * Account Store contract interface.
 */
export interface IAccountStore {
  /**
   * Return all registered accounts sorted by priority.
   */
  listAccounts(): Promise<AccountMetadata[]>;

  /**
   * Find an account by its unique ID.
   */
  getAccount(id: string): Promise<AccountMetadata | null>;

  /**
   * Find an account by email address.
   */
  getAccountByEmail(email: string): Promise<AccountMetadata | null>;

  /**
   * Register a new account's non-secret metadata.
   */
  addAccount(input: CreateAccountInput): Promise<AccountMetadata>;

  /**
   * Update an existing account's non-secret metadata.
   */
  updateAccount(id: string, updates: UpdateAccountInput): Promise<AccountMetadata | null>;

  /**
   * Remove an account from storage.
   */
  removeAccount(id: string): Promise<boolean>;

  /**
   * Get the ID of the currently active account session, if known.
   */
  getActiveAccountId(): Promise<string | null>;

  /**
   * Set the active account session ID.
   */
  setActiveAccountId(id: string | null): Promise<void>;
}
