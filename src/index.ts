/**
 * AG2 Router - Application Entrypoint
 *
 * Boots the lightweight loopback server, account metadata store,
 * AG2 adapter foundation, and quota router.
 */

import { LocalMetadataAccountStore } from './accounts/account-store.js';
import { AccountEnrollmentService } from './accounts/enrollment.js';
import { AG2LiveAdapter } from './ag2/adapter.js';
import { AG2WinCredReader, AG2WinCredWriter } from './ag2/wincred.js';
import { WindowsProcessController } from './ag2/process-control.js';
import { loadConfig } from './config/config.js';
import { QuotaRouter } from './router/router.js';
import { AppServer } from './server/server.js';
import { SessionVault } from './vault/session-vault.js';
import { SwitchPlanner } from './switching/planner.js';
import { SwitchTransactionCoordinator } from './switching/transaction.js';

async function bootstrap() {
  const config = loadConfig();

  const accountStore = new LocalMetadataAccountStore();
  const adapter = new AG2LiveAdapter();
  const winCredReader = new AG2WinCredReader();
  const winCredWriter = new AG2WinCredWriter();
  const processController = new WindowsProcessController();
  const sessionVault = new SessionVault();
  const enrollmentService = new AccountEnrollmentService({
    adapter,
    wincredReader: winCredReader,
    sessionVault,
    accountStore
  });
  const switchPlanner = new SwitchPlanner({
    accountStore,
    sessionVault,
    winCredReader,
    processController,
    ag2Adapter: adapter
  });
  const switchCoordinator = new SwitchTransactionCoordinator(
    {
      accountStore,
      sessionVault,
      winCredReader,
      winCredWriter,
      processController,
      ag2Adapter: adapter
    },
    { executionAuthorized: false } // HARD GATE: live execution disabled in production runtime
  );
  const router = new QuotaRouter(accountStore, adapter, config.router);
  const server = new AppServer(
    config,
    accountStore,
    adapter,
    router,
    enrollmentService,
    sessionVault,
    switchPlanner,
    switchCoordinator
  );

  // Start server
  const bound = await server.start();
  const serverUrl = `http://${bound.host}:${bound.port}`;

  console.log('====================================================');
  console.log('  AG2 Router - Antigravity 2 Account & Quota Router ');
  console.log('====================================================');
  console.log(`  Dashboard:   ${serverUrl}`);
  console.log(`  Interface:   Loopback Only (${bound.host})`);
  console.log(`  Auto-Switch: ${config.router.autoSwitchEnabled ? 'ENABLED' : 'DISABLED (Default)'}`);
  console.log(`  Vault:       ${sessionVault.getVaultPath()}`);
  console.log(`  Stage:       Encrypted Multi-Account Vault`);
  console.log('====================================================');

  // Start router evaluation loop
  router.start();

  // Graceful shutdown handling
  const shutdown = async (signal: string) => {
    console.log(`\nReceived ${signal}. Shutting down gracefully...`);
    router.stop();
    await server.stop();
    console.log('AG2 Router stopped cleanly.');
    process.exit(0);
  };

  process.on('SIGINT', () => shutdown('SIGINT'));
  process.on('SIGTERM', () => shutdown('SIGTERM'));

  process.on('unhandledRejection', (reason) => {
    console.error('Unhandled Rejection:', reason);
  });
}

bootstrap().catch((err) => {
  console.error('Failed to start AG2 Router:', err);
  process.exit(1);
});
