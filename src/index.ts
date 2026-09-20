/**
 * AG2 Router - Application Entrypoint
 *
 * Boots the lightweight loopback server, account metadata store,
 * AG2 adapter foundation, and quota router.
 */

import { LocalMetadataAccountStore } from './accounts/account-store.js';
import { AG2LiveAdapter } from './ag2/adapter.js';
import { loadConfig } from './config/config.js';
import { QuotaRouter } from './router/router.js';
import { AppServer } from './server/server.js';

async function bootstrap() {
  const config = loadConfig();

  const accountStore = new LocalMetadataAccountStore();
  const adapter = new AG2LiveAdapter();
  const router = new QuotaRouter(accountStore, adapter, config.router);
  const server = new AppServer(config, accountStore, adapter, router);

  // Start server
  const bound = await server.start();
  const serverUrl = `http://${bound.host}:${bound.port}`;

  console.log('====================================================');
  console.log('  AG2 Router - Antigravity 2 Account & Quota Router ');
  console.log('====================================================');
  console.log(`  Dashboard:   ${serverUrl}`);
  console.log(`  Interface:   Loopback Only (${bound.host})`);
  console.log(`  Auto-Switch: ${config.router.autoSwitchEnabled ? 'ENABLED' : 'DISABLED (Default)'}`);
  console.log(`  Stage:       Live Discovery & Telemetry`);
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
