/**
 * Test: AG2 Adapter Boundary & Foundation
 */

import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import { AG2AdapterFoundation } from '../src/ag2/adapter.js';
import { NotImplementedError } from '../src/ag2/types.js';

describe('AG2AdapterFoundation', () => {
  const adapter = new AG2AdapterFoundation();

  it('should return truthful OFFLINE discovery in foundation stage', async () => {
    const discovery = await adapter.discover();
    assert.equal(discovery.isRunning, false);
    assert.equal(discovery.status, 'OFFLINE');
    assert.equal(discovery.processInfo, null);
    assert.match(discovery.message || '', /reserved for the live integration stage/i);
  });

  it('should return null for current account in foundation stage', async () => {
    const account = await adapter.getCurrentAccount();
    assert.equal(account, null);
  });

  it('should return null for quota snapshot in foundation stage', async () => {
    const quota = await adapter.getQuota();
    assert.equal(quota, null);
  });

  it('should return UNKNOWN for activity state in foundation stage', async () => {
    const activity = await adapter.getActivityState();
    assert.equal(activity.state, 'UNKNOWN');
    assert.equal(activity.runningTrajectories, 0);
  });

  it('should explicitly throw NotImplementedError on switchAccount', async () => {
    await assert.rejects(
      async () => {
        await adapter.switchAccount({
          targetAccountId: 'acc_1',
          targetEmail: 'test@example.com'
        });
      },
      (err: unknown) => {
        assert.ok(err instanceof NotImplementedError);
        assert.equal(err.code, 'NOT_IMPLEMENTED');
        assert.match(err.message, /switchAccount/);
        return true;
      }
    );
  });

  it('should explicitly throw NotImplementedError on verifyAccount', async () => {
    await assert.rejects(
      async () => {
        await adapter.verifyAccount('test@example.com');
      },
      (err: unknown) => {
        assert.ok(err instanceof NotImplementedError);
        assert.equal(err.code, 'NOT_IMPLEMENTED');
        assert.match(err.message, /verifyAccount/);
        return true;
      }
    );
  });
});
