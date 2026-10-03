import type { AccountMetadata } from '../api/types.js';
import type { DashboardRefreshGate } from './helpers.js';

export type AccountMutationResult = { success: true } | { success: false; message: string };
export interface ConnectAccountInput {
  email: string;
  name?: string;
  alias?: string;
  priority: number;
  isReserve: boolean;
}

interface MutationFeedback {
  refreshGate: DashboardRefreshGate;
  logActivity: (message: string) => void;
  notifyError: (message: string) => void;
}

// Use the API's error message, never its stack or an arbitrary object's representation.
// Keep both notification and activity text bounded, including for malformed responses.
function reportFailure(error: unknown, operation: string, feedback: MutationFeedback): AccountMutationResult {
  const detail = error instanceof Error ? error.message.split(/[\r\n]/, 1)[0].trim().slice(0, 256) : '';
  const message = `${operation} failed: ${detail || 'Request failed. Please try again.'}`;
  feedback.logActivity(message);
  feedback.notifyError(message);
  return { success: false, message };
}

export async function updateAlias(
  id: string, alias: string,
  dependencies: MutationFeedback & {
    updateAccountAlias: (id: string, alias: string) => Promise<{ success: boolean; account: AccountMetadata }>;
    publishAlias: (id: string, alias: AccountMetadata['alias']) => void;
  }
): Promise<AccountMutationResult> {
  dependencies.refreshGate.beginMutation();
  try {
    const result = await dependencies.updateAccountAlias(id, alias);
    if (!result.success || !result.account) throw new Error('Alias update was not confirmed.');
    dependencies.publishAlias(id, result.account.alias);
    dependencies.logActivity(`Updated alias for ${result.account.email} to "${result.account.alias || '(cleared)'}".`);
    return { success: true };
  } catch (error) {
    return reportFailure(error, 'Alias update', dependencies);
  } finally {
    dependencies.refreshGate.endMutation();
  }
}

export async function connectAccount(
  data: ConnectAccountInput,
  dependencies: MutationFeedback & {
    createAccount: (data: ConnectAccountInput) => Promise<{ account: AccountMetadata }>;
    refreshAll: () => Promise<void>;
  }
): Promise<AccountMutationResult> {
  dependencies.refreshGate.beginMutation();
  try {
    const result = await dependencies.createAccount(data);
    dependencies.logActivity(`Connected account metadata for ${result.account.email}.`);
  } catch (error) {
    return reportFailure(error, 'Account connection', dependencies);
  } finally {
    dependencies.refreshGate.endMutation();
  }
  await dependencies.refreshAll();
  return { success: true };
}
