import type {
  JournalResolutionResult,
  JournalRecoveryState,
  SwitchStatusDto
} from '../api/types.js';

export const KNOWN_JOURNAL_RECOVERY_STATES = [
  'NONE',
  'ACTION_REQUIRED',
  'RESTART_REQUIRED',
  'NOT_RESOLVABLE',
  'UNKNOWN'
] as const;

export function isValidJournalRecoveryState(val: unknown): val is JournalRecoveryState {
  return typeof val === 'string' && (KNOWN_JOURNAL_RECOVERY_STATES as readonly string[]).includes(val);
}

export function validateSwitchStatusDto(raw: unknown): SwitchStatusDto | null {
  if (!raw || typeof raw !== 'object') return null;
  const obj = raw as Record<string, unknown>;
  if (typeof obj.quarantineActive !== 'boolean') return null;
  if (typeof obj.journalRecoveryState !== 'string') return null;
  if (!isValidJournalRecoveryState(obj.journalRecoveryState)) return null;
  return {
    activeTransactionId: typeof obj.activeTransactionId === 'string' ? obj.activeTransactionId : null,
    currentState: typeof obj.currentState === 'string' ? obj.currentState : 'UNKNOWN',
    lastResult: (obj.lastResult && typeof obj.lastResult === 'object') ? {
      code: String((obj.lastResult as Record<string, unknown>).code || ''),
      message: String((obj.lastResult as Record<string, unknown>).message || '')
    } : null,
    quarantineActive: obj.quarantineActive,
    journalRecoveryState: obj.journalRecoveryState
  };
}

export function canExecuteLifecycleMutation(
  isSafetyAuthoritative: boolean,
  switchStatus: { quarantineActive?: boolean; journalRecoveryState?: string } | null | undefined
): boolean {
  if (!isSafetyAuthoritative || !switchStatus) return false;
  if (typeof switchStatus.quarantineActive !== 'boolean') return false;
  if (typeof switchStatus.journalRecoveryState !== 'string') return false;
  if (!isValidJournalRecoveryState(switchStatus.journalRecoveryState)) return false;
  return switchStatus.quarantineActive === false && switchStatus.journalRecoveryState === 'NONE';
}

export function getRecoveryActivityLogMessage(
  status: string | null | undefined
): string {
  switch (status) {
    case 'ResolvedRestartRequired':
      return 'Journal resolution succeeded. AG2 Router must be restarted from the Windows system tray before normal routing resumes.';
    case 'CleanCleanupCompleted':
      return 'Switch journal cleaned up successfully. Current safety status determines whether switching can resume.';
    case 'NoJournal':
      return 'Journal resolution completed: no switch journal was present on disk.';
    case 'NotResolvable':
      return 'Journal resolution failed: switch journal artifact cannot be resolved automatically.';
    case 'ProofFailed':
      return 'Journal resolution failed: account state coherence could not be verified.';
    case 'PersistenceFailure':
      return 'Journal resolution failed: a persistence or lock error occurred.';
    default:
      return 'Journal resolution ended with an unresolved state.';
  }
}

export interface ResolutionCopy {
  title: string;
  description: string;
  alertType: 'warning' | 'info' | 'error' | 'success';
  canRetry: boolean;
  isRestartRequired: boolean;
}

/** A successful journal operation alone does not establish permission to switch. */
export function reconcileRecoveryCopy(result: Partial<JournalResolutionResult> | null,
  status: SwitchStatusDto | null, authoritative: boolean): ResolutionCopy {
  const copy = mapJournalRecoveryCopy(result);
  if (!result || !['CleanCleanupCompleted', 'NoJournal', 'ResolvedRestartRequired'].includes(result.status ?? '')) return copy;
  if (!authoritative || !status || typeof status.quarantineActive !== 'boolean' ||
      !isValidJournalRecoveryState(status.journalRecoveryState)) {
    return { title: 'Recovery Status Unverified', description: 'Journal operation finished, but current safety status could not be verified. Switching remains blocked. Refresh safety status or restart AG2 Router.',
      alertType: 'warning', canRetry: true, isRestartRequired: false };
  }
  if (status.quarantineActive || status.journalRecoveryState === 'RESTART_REQUIRED' || result.restartRequired || result.status === 'ResolvedRestartRequired') {
    return { title: 'Switching Still Blocked — Restart Required', description: 'The journal operation finished, but switching remains blocked by safety quarantine or a restart requirement. Exit AG2 Router from the Windows system tray and launch it again.',
      alertType: 'warning', canRetry: false, isRestartRequired: true };
  }
  if (status.journalRecoveryState !== 'NONE' || status.activeTransactionId || status.currentState !== 'IDLE') {
    const banner = getBannerCopy(false, status.journalRecoveryState);
    return { title: banner?.title ?? 'Switching Still Blocked', description: banner?.message ?? 'A switch transaction is still active or unresolved. Switching remains blocked until current safety status is clear.',
      alertType: 'warning', canRetry: status.journalRecoveryState === 'ACTION_REQUIRED', isRestartRequired: banner?.isRestartRequired ?? false };
  }
  return copy;
}

export function mapJournalRecoveryCopy(result: Partial<JournalResolutionResult> | null | undefined): ResolutionCopy {
  if (!result || !result.status) {
    return {
      title: 'Resolution Incomplete',
      description: 'An unexpected response was received during resolution.',
      alertType: 'error',
      canRetry: true,
      isRestartRequired: false
    };
  }

  switch (result.status) {
    case 'CleanCleanupCompleted':
      return {
        title: 'Switch Journal Cleaned Up',
        description: 'The switch journal was cleaned up successfully. No further recovery action is required.',
        alertType: 'success',
        canRetry: false,
        isRestartRequired: false
      };

    case 'ResolvedRestartRequired':
      return {
        title: 'Recovery Completed — Restart Required',
        description: 'The switch journal was verified and successfully removed. AG2 Router must be restarted from the Windows system tray before normal routing can resume.',
        alertType: 'info',
        canRetry: false,
        isRestartRequired: true
      };

    case 'NoJournal':
      return {
        title: 'No Journal Present',
        description: 'No switch journal is present on disk.',
        alertType: 'info',
        canRetry: false,
        isRestartRequired: false
      };

    case 'NotResolvable':
      if (result.reasonCode === 'CORRUPT_JOURNAL') {
        return {
          title: 'Corrupt Journal Detected',
          description: 'Automatic recovery is unavailable due to an unresolvable corrupted switch journal artifact.',
          alertType: 'error',
          canRetry: false,
          isRestartRequired: false
        };
      }
      if (result.reasonCode === 'UNSUPPORTED_VERSION') {
        return {
          title: 'Unsupported Journal Schema',
          description: 'Automatic recovery is unavailable due to an unresolvable switch journal schema version.',
          alertType: 'error',
          canRetry: false,
          isRestartRequired: false
        };
      }
      return {
        title: 'Automatic Resolution Not Available',
        description: 'Automatic recovery is unavailable due to an unresolvable switch journal artifact.',
        alertType: 'error',
        canRetry: false,
        isRestartRequired: false
      };

    case 'ProofFailed':
      switch (result.reasonCode) {
        case 'LIVE_IDENTITY_UNAVAILABLE':
          return {
            title: 'Live Identity Unavailable',
            description: 'Live Antigravity identity is unavailable. Please ensure Antigravity is running with an active account, then retry recovery.',
            alertType: 'warning',
            canRetry: true,
            isRestartRequired: false
          };
        case 'LIVE_IDENTITY_MISMATCH':
          return {
            title: 'Identity Mismatch',
            description: 'Live Antigravity identity does not match the active account metadata. Please switch to the expected account in Antigravity or update metadata.',
            alertType: 'warning',
            canRetry: true,
            isRestartRequired: false
          };
        case 'NO_ACTIVE_ACCOUNT':
          return {
            title: 'No Active Account',
            description: 'No active account is set in metadata.',
            alertType: 'warning',
            canRetry: false,
            isRestartRequired: false
          };
        case 'ACTIVE_ACCOUNT_NOT_FOUND':
          return {
            title: 'Active Account Not Found',
            description: 'The active account was not found in metadata.',
            alertType: 'warning',
            canRetry: false,
            isRestartRequired: false
          };
        case 'ACCOUNT_NOT_ENROLLED':
          return {
            title: 'Account Not Validly Enrolled',
            description: 'The active account is not validly enrolled or has no vaulted session.',
            alertType: 'warning',
            canRetry: false,
            isRestartRequired: false
          };
        case 'CREDENTIAL_MISSING':
          return {
            title: 'Credential Missing',
            description: 'Windows Credential Manager entry is missing or empty.',
            alertType: 'warning',
            canRetry: false,
            isRestartRequired: false
          };
        case 'VAULT_SESSION_MISSING':
          return {
            title: 'Vault Session Missing',
            description: 'Vaulted session for the active account is missing or empty.',
            alertType: 'warning',
            canRetry: false,
            isRestartRequired: false
          };
        case 'CREDENTIAL_MISMATCH':
          return {
            title: 'Credential Mismatch',
            description: 'Windows Credential Manager payload does not match the vaulted session.',
            alertType: 'warning',
            canRetry: true,
            isRestartRequired: false
          };
        case 'CONCURRENT_MUTATION':
          return {
            title: 'Concurrent Change Detected',
            description: 'State changed concurrently during resolution proof. Please retry.',
            alertType: 'warning',
            canRetry: true,
            isRestartRequired: false
          };
        default:
          return {
            title: 'Coherence Verification Failed',
            description: 'Account state coherence could not be verified across metadata, credentials, and live identity.',
            alertType: 'warning',
            canRetry: true,
            isRestartRequired: false
          };
      }

    case 'PersistenceFailure':
      switch (result.reasonCode) {
        case 'LOCK_TIMEOUT':
          return {
            title: 'Lock Contention Timeout',
            description: 'Lock acquisition timed out during journal resolution. Please retry.',
            alertType: 'error',
            canRetry: true,
            isRestartRequired: false
          };
        case 'LEASE_ACQUISITION_FAILED':
          return {
            title: 'Cross-Process Contention',
            description: 'Cross-process lease acquisition failed. Ensure no other AG2 Router process is active.',
            alertType: 'error',
            canRetry: true,
            isRestartRequired: false
          };
        case 'IO_ERROR':
          return {
            title: 'Journal Access Error',
            description: 'An I/O error occurred while accessing the switch journal.',
            alertType: 'error',
            canRetry: true,
            isRestartRequired: false
          };
        case 'UNSUPPORTED_PLATFORM':
          return {
            title: 'Platform Not Supported',
            description: 'Conditional switch journal deletion is not supported on this platform.',
            alertType: 'error',
            canRetry: false,
            isRestartRequired: false
          };
        case 'DELETE_FAILED':
          return {
            title: 'Journal Deletion Failed',
            description: 'State was verified coherent, but the switch journal could not be deleted from disk. Please verify file permissions and retry.',
            alertType: 'error',
            canRetry: true,
            isRestartRequired: false
          };
        default:
          return {
            title: 'Persistence Failure',
            description: 'A persistence failure occurred during journal resolution.',
            alertType: 'error',
            canRetry: true,
            isRestartRequired: false
          };
      }

    default:
      return {
        title: 'Resolution Notice',
        description: 'An unexpected resolution state occurred.',
        alertType: 'error',
        canRetry: true,
        isRestartRequired: false
      };
  }
}

export interface BannerCopy {
  bannerType: 'warning' | 'info' | 'error';
  title: string;
  message: string;
  showActionButton: boolean;
  actionLabel?: string;
  isRestartRequired: boolean;
}

export function getBannerCopy(
  quarantineActive: boolean,
  journalRecoveryState: JournalRecoveryState
): BannerCopy | null {
  if (journalRecoveryState === 'ACTION_REQUIRED') {
    return {
      bannerType: 'warning',
      title: 'Safety Quarantine Active: Switch Recovery Required',
      message: 'A prior account switch did not finish cleanly. Router automatic switching and account mutations are paused. You can run recovery to verify account coherence and resolve the journal.',
      showActionButton: true,
      actionLabel: 'Resolve Journal...',
      isRestartRequired: false
    };
  }

  if (journalRecoveryState === 'RESTART_REQUIRED') {
    return {
      bannerType: 'info',
      title: 'Switch Journal Resolved: Restart Required',
      message: 'The switch journal was successfully resolved and removed. AG2 Router must be restarted from the Windows system tray before normal switching and routing can resume.',
      showActionButton: false,
      isRestartRequired: true
    };
  }

  if (journalRecoveryState === 'NOT_RESOLVABLE') {
    return {
      bannerType: 'error',
      title: 'Safety Quarantine Active: Journal Not Resolvable',
      message: 'Automatic recovery is unavailable due to an unresolvable switch journal artifact. Automatic switching and account mutations are disabled.',
      showActionButton: false,
      isRestartRequired: false
    };
  }

  if (journalRecoveryState === 'UNKNOWN') {
    return {
      bannerType: 'error',
      title: 'Safety Quarantine Active: Recovery State Unknown',
      message: 'Router safety quarantine is active because journal recovery state could not be safely established. Please restart AG2 Router from the system tray.',
      showActionButton: false,
      isRestartRequired: true
    };
  }

  if (quarantineActive) {
    return {
      bannerType: 'warning',
      title: 'Safety Quarantine Active',
      message: 'AG2 Router is currently in safety quarantine due to an unproven session or lifecycle state. Normal switching is blocked. Please restart AG2 Router from the system tray.',
      showActionButton: false,
      isRestartRequired: true
    };
  }

  return null;
}
