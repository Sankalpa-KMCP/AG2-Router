/**
 * Compatibility re-export for the Node reference test suite.
 *
 * The dashboard helper implementation is owned by the authored frontend at
 * frontend/src/lib/utils/helpers.ts. Authored frontend code must not import
 * from src/dashboard; this forwarding module exists only so
 * test/frontend-dashboard.test.ts keeps exercising the same implementation
 * the frontend ships.
 */
export * from '../../frontend/src/lib/utils/helpers.js';
