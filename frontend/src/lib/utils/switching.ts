import { SwitchRequestError, type ApiClient } from '../api/client.js';
import type { SwitchResultDto } from '../api/types.js';
import type { DashboardRefreshGate } from './helpers.js';

export function getSwitchErrorMessage(error: unknown): string {
  if (error instanceof SwitchRequestError) {
    const recovery = error.manualRecoveryRequired
      ? ' Manual recovery is required; check Activity & safety before retrying.' : '';
    return `${error.code}: ${error.message}${recovery}`;
  }
  return error instanceof Error ? error.message : 'Switch failed';
}

export async function executeDashboardSwitch(id: string, dependencies: {
  client: Pick<ApiClient, 'executeSwitch'>;
  refreshGate: DashboardRefreshGate;
  invalidateSafety: () => void;
  refreshAll: () => Promise<void>;
}): Promise<SwitchResultDto> {
  dependencies.refreshGate.beginMutation();
  try {
    // Previous safety evidence cannot authorize further mutations during this attempt.
    dependencies.invalidateSafety();
    return await dependencies.client.executeSwitch(id);
  } finally {
    // End the mutation fence before issuing the new authoritative read tickets.
    dependencies.refreshGate.endMutation();
    await dependencies.refreshAll();
  }
}
