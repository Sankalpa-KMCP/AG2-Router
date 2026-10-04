/**
 * AG2 Router - Usage request ownership controller.
 *
 * Only the latest started request may mutate dashboard state. A stale request (superseded by
 * a newer account/range selection, or invalidated by component teardown) can neither overwrite
 * data/error/unsupported state nor finalize loading that belongs to a newer request.
 * The selected scope/range are captured at request start and never reread mid-request.
 */

import type {
  UsageCollectorStatus,
  UsageModelBreakdownItem,
  UsageSummary,
  UsageTimeBucket,
} from '../api/types.js';

export type UsageRequestOutcome =
  | {
      kind: 'success';
      summary: UsageSummary;
      collector: UsageCollectorStatus | null;
      buckets: UsageTimeBucket[];
      models: UsageModelBreakdownItem[];
    }
  | { kind: 'unsupported' }
  | { kind: 'error'; message: string };

export interface UsageRequestStateCallbacks {
  /** Called synchronously when a new request becomes the latest one. */
  beginRequest(): void;
  applySuccess(payload: {
    summary: UsageSummary;
    collector: UsageCollectorStatus | null;
    buckets: UsageTimeBucket[];
    models: UsageModelBreakdownItem[];
  }): void;
  applyUnsupported(): void;
  applyError(message: string): void;
  /** Called exactly once for the latest request when it settles; never by a stale one. */
  finalizeRequest(): void;
}

export interface UsageRequestController {
  /** Starts a request; silently drops the outcome if a newer request was started since. */
  run(fetchOutcome: () => Promise<UsageRequestOutcome>): Promise<void>;
  /** Invalidates every in-flight request (component teardown / forced ownership reset). */
  invalidate(): void;
}

export function createUsageRequestController(callbacks: UsageRequestStateCallbacks): UsageRequestController {
  let generation = 0;

  return {
    async run(fetchOutcome: () => Promise<UsageRequestOutcome>): Promise<void> {
      const myGeneration = ++generation;
      callbacks.beginRequest();
      let outcome: UsageRequestOutcome;
      try {
        outcome = await fetchOutcome();
      } catch (error) {
        outcome = {
          kind: 'error',
          message: error instanceof Error ? error.message : 'Usage could not be loaded.',
        };
      }

      // A superseded request owns nothing: not data, not errors, not loading finalization.
      if (myGeneration !== generation) {
        return;
      }

      switch (outcome.kind) {
        case 'success':
          callbacks.applySuccess({
            summary: outcome.summary,
            collector: outcome.collector,
            buckets: outcome.buckets,
            models: outcome.models,
          });
          break;
        case 'unsupported':
          callbacks.applyUnsupported();
          break;
        case 'error':
          callbacks.applyError(outcome.message);
          break;
      }

      if (myGeneration === generation) {
        callbacks.finalizeRequest();
      }
    },
    invalidate(): void {
      generation++;
    },
  };
}
