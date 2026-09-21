/**
 * AG2 Router - Application Configuration
 *
 * Centralizes runtime settings with secure, conservative defaults.
 *
 * SECURITY:
 * Host is strictly bound to loopback ('127.0.0.1') to prevent external LAN access.
 */

import * as path from 'node:path';
import { resolveAccountMetadataPath } from '../accounts/account-store.js';
import { DEFAULT_ROUTER_CONFIG, RouterConfig } from '../router/types.js';

export interface AppConfig {
  readonly host: string;
  readonly port: number;
  readonly storageDir: string;
  readonly uiDir: string;
  readonly router: RouterConfig;
  readonly isDev: boolean;
}

export function loadConfig(): AppConfig {
  const isDev = process.env.NODE_ENV !== 'production';

  // Port configuration
  const parsedPort = process.env.PORT ? parseInt(process.env.PORT, 10) : NaN;
  const port = !isNaN(parsedPort) && parsedPort > 0 && parsedPort <= 65535 ? parsedPort : 39250;

  // Paths
  const storageDir = process.env.DATA_DIR
    ? path.resolve(process.env.DATA_DIR)
    : path.dirname(resolveAccountMetadataPath());

  const uiDir = process.env.UI_DIR
    ? path.resolve(process.env.UI_DIR)
    : path.resolve(process.cwd(), 'src', 'ui');

  // Router configuration with environment overrides
  const autoSwitch = process.env.AUTO_SWITCH === 'true';
  const lowThreshold = process.env.LOW_QUOTA_THRESHOLD
    ? parseInt(process.env.LOW_QUOTA_THRESHOLD, 10)
    : DEFAULT_ROUTER_CONFIG.lowQuotaThresholdPercent;
  const minCandidate = process.env.MIN_CANDIDATE_QUOTA
    ? parseInt(process.env.MIN_CANDIDATE_QUOTA, 10)
    : DEFAULT_ROUTER_CONFIG.minimumCandidateQuotaPercent;
  const pollInterval = process.env.POLL_INTERVAL_MS
    ? parseInt(process.env.POLL_INTERVAL_MS, 10)
    : DEFAULT_ROUTER_CONFIG.pollingIntervalMs;

  return {
    host: '127.0.0.1', // Strict loopback binding
    port,
    storageDir,
    uiDir,
    router: {
      autoSwitchEnabled: autoSwitch,
      lowQuotaThresholdPercent: isNaN(lowThreshold) ? DEFAULT_ROUTER_CONFIG.lowQuotaThresholdPercent : lowThreshold,
      minimumCandidateQuotaPercent: isNaN(minCandidate) ? DEFAULT_ROUTER_CONFIG.minimumCandidateQuotaPercent : minCandidate,
      pollingIntervalMs: isNaN(pollInterval) ? DEFAULT_ROUTER_CONFIG.pollingIntervalMs : pollInterval
    },
    isDev
  };
}
