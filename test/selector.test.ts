/**
 * Test: Deterministic Candidate Selector
 */

import { describe, it } from 'node:test';
import * as assert from 'node:assert/strict';
import { AccountMetadata } from '../src/accounts/types.js';
import { selectBestCandidate } from '../src/router/selector.js';
import { DEFAULT_ROUTER_CONFIG, RouterConfig } from '../src/router/types.js';

function createMockAccount(id: string, email: string, priority = 1, isReserve = false, status: AccountMetadata['validationStatus'] = 'VALID'): AccountMetadata {
  return {
    id,
    email,
    priority,
    isReserve,
    validationStatus: status,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    lastActiveAt: null
  };
}

describe('Candidate Selector', () => {
  const config: RouterConfig = {
    ...DEFAULT_ROUTER_CONFIG,
    lowQuotaThresholdPercent: 15,
    minimumCandidateQuotaPercent: 30
  };

  it('should not switch if current account has healthy quota (>15%)', () => {
    const acc1 = createMockAccount('acc_1', 'acc1@example.com', 1);
    const acc2 = createMockAccount('acc_2', 'acc2@example.com', 2);
    const quotas = new Map([
      ['acc_1', 0.85],
      ['acc_2', 0.90]
    ]);

    const result = selectBestCandidate({
      currentAccountId: 'acc_1',
      currentQuotaFraction: 0.85,
      accounts: [acc1, acc2],
      accountQuotas: quotas,
      config
    });

    assert.equal(result.shouldSwitch, false);
    assert.equal(result.bestCandidate, null);
    assert.match(result.reason, /healthy/i);
  });

  it('should switch when current quota falls at or below threshold (<=15%)', () => {
    const acc1 = createMockAccount('acc_1', 'acc1@example.com', 1);
    const acc2 = createMockAccount('acc_2', 'acc2@example.com', 2);
    const quotas = new Map([
      ['acc_1', 0.10],
      ['acc_2', 0.75]
    ]);

    const result = selectBestCandidate({
      currentAccountId: 'acc_1',
      currentQuotaFraction: 0.10,
      accounts: [acc1, acc2],
      accountQuotas: quotas,
      config
    });

    assert.equal(result.shouldSwitch, true);
    assert.ok(result.bestCandidate);
    assert.equal(result.bestCandidate.account.id, 'acc_2');
    assert.equal(result.bestCandidate.quotaPercent, 75);
  });

  it('should exclude candidates with quota below minimum candidate threshold (<30%)', () => {
    const acc1 = createMockAccount('acc_1', 'acc1@example.com', 1);
    const acc2 = createMockAccount('acc_2', 'acc2@example.com', 2);
    const acc3 = createMockAccount('acc_3', 'acc3@example.com', 3);
    const quotas = new Map([
      ['acc_1', 0.05],
      ['acc_2', 0.25], // below 30%
      ['acc_3', 0.20]  // below 30%
    ]);

    const result = selectBestCandidate({
      currentAccountId: 'acc_1',
      currentQuotaFraction: 0.05,
      accounts: [acc1, acc2, acc3],
      accountQuotas: quotas,
      config
    });

    assert.equal(result.shouldSwitch, false);
    assert.equal(result.bestCandidate, null);
    assert.match(result.reason, /minimum quota threshold/i);
  });

  it('should exclude current account from candidates', () => {
    const acc1 = createMockAccount('acc_1', 'acc1@example.com', 1);
    const quotas = new Map([['acc_1', 0.10]]);

    const result = selectBestCandidate({
      currentAccountId: 'acc_1',
      currentQuotaFraction: 0.10,
      accounts: [acc1],
      accountQuotas: quotas,
      config
    });

    assert.equal(result.shouldSwitch, false);
    assert.equal(result.bestCandidate, null);
    assert.match(result.reason, /No secondary accounts/i);
  });

  it('should exclude EXPIRED and FAILED accounts', () => {
    const acc1 = createMockAccount('acc_1', 'acc1@example.com', 1);
    const accExpired = createMockAccount('acc_exp', 'exp@example.com', 2, false, 'EXPIRED');
    const accFailed = createMockAccount('acc_fail', 'fail@example.com', 3, false, 'FAILED');
    const accValid = createMockAccount('acc_valid', 'valid@example.com', 4, false, 'VALID');

    const quotas = new Map([
      ['acc_1', 0.10],
      ['acc_exp', 0.95],
      ['acc_fail', 0.90],
      ['acc_valid', 0.50]
    ]);

    const result = selectBestCandidate({
      currentAccountId: 'acc_1',
      currentQuotaFraction: 0.10,
      accounts: [acc1, accExpired, accFailed, accValid],
      accountQuotas: quotas,
      config
    });

    assert.equal(result.shouldSwitch, true);
    assert.ok(result.bestCandidate);
    assert.equal(result.bestCandidate.account.id, 'acc_valid');
  });

  it('should prioritize standard accounts over reserve accounts even if reserve has higher quota', () => {
    const acc1 = createMockAccount('acc_1', 'acc1@example.com', 1);
    const accStandard = createMockAccount('acc_std', 'std@example.com', 2, false);
    const accReserve = createMockAccount('acc_res', 'res@example.com', 3, true);

    const quotas = new Map([
      ['acc_1', 0.05],
      ['acc_std', 0.50],
      ['acc_res', 0.95] // higher quota, but reserve!
    ]);

    const result = selectBestCandidate({
      currentAccountId: 'acc_1',
      currentQuotaFraction: 0.05,
      accounts: [acc1, accStandard, accReserve],
      accountQuotas: quotas,
      config
    });

    assert.equal(result.shouldSwitch, true);
    assert.ok(result.bestCandidate);
    assert.equal(result.bestCandidate.account.id, 'acc_std');
  });

  it('should deterministically break ties by quota, then priority, then ID', () => {
    const acc1 = createMockAccount('acc_1', 'acc1@example.com', 1);
    const accA = createMockAccount('acc_b', 'b@example.com', 2, false);
    const accB = createMockAccount('acc_a', 'a@example.com', 1, false);

    // Identical quota: accB has lower priority number (1 < 2)
    const quotas = new Map([
      ['acc_1', 0.05],
      ['acc_b', 0.80],
      ['acc_a', 0.80]
    ]);

    const result = selectBestCandidate({
      currentAccountId: 'acc_1',
      currentQuotaFraction: 0.05,
      accounts: [acc1, accA, accB],
      accountQuotas: quotas,
      config
    });

    assert.equal(result.shouldSwitch, true);
    assert.equal(result.bestCandidate?.account.id, 'acc_a');
  });
});
