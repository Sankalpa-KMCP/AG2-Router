# Domain rules

This document owns durable vocabulary and intended domain invariants. It describes what correct behavior means; source and tests determine what is implemented. Current deviations are listed in [known-limitations.md](known-limitations.md).

## Status labels

- INTENDED INVARIANT: required behavior that implementation and tests should enforce.
- CURRENT IMPLEMENTATION: behavior directly evidenced by current source/tests.
- LIMITATION: known disagreement or missing proof, linked to known-limitations.md.

## Quota truthfulness

INTENDED INVARIANT:

- Unknown quota is unknown. It must not be represented, displayed, cached, ranked, or routed as known 100% remaining.
- A numeric remaining fraction is valid only when supported by upstream evidence and normalized to the range 0.0 through 1.0.
- Exhausted, unknown, unavailable, and stale are distinct states even if a conservative policy makes more than one state ineligible.
- Prompt credits and Flow credits are separate pools. Do not add them or turn them into one percentage.
- Per-model quotas remain separate unless there is evidence that variants share a quota bucket.
- Incompatible reset windows remain distinct unless upstream identity or other evidence proves a shared bucket.
- Percentages from different accounts are relative measures, not additive absolute capacity. Do not present a cross-account sum as total available quota.
- When multiple observations are proven to represent one pool, conflict resolution must be conservative and explicit.

CURRENT IMPLEMENTATION:

- AG2TelemetryNormalizer clamps observed numeric fractions and preserves prompt and Flow credit records separately.
- CanonicalizeModelQuotas groups primarily by ModelOrTier and uses the minimum fraction within a group.
- NativeAutoRouter removes models marked IsExhausted before deriving the current usable fraction from the minimum remaining non-exhausted observation. If at least one healthy model remains, an exhausted model does not lower that account-level value and can be masked for switching assessment.

The unknown and reset-window gaps are documented in [known-limitations.md](known-limitations.md); the current defaults are not the intended contract.

UNRESOLVED REQUIREMENT: the repository does not currently establish an authoritative intended account-level switching policy for a mixed snapshot containing both exhausted and healthy models. Do not infer either “any exhausted model forces switching” or “only all exhausted models force switching” until a domain decision and tests define it.

Primary evidence: AG2TelemetryNormalizer, NativeAutoRouter, AG2TelemetryNormalizerTests, and NativeAutoRouterTests.

## Account identity

INTENDED INVARIANT:

- Account identity comes from verified Antigravity telemetry, not user-entered email alone.
- Metadata, the vaulted session, the active-account marker, the live WinCred value, and the verified process identity are related records but are not interchangeable proof.
- Enrollment must bind captured credential material to the observed account and must not leave a partial new account after failure.
- A switch is complete only after the restarted, verified process reports the target identity and the metadata commit succeeds.
- Rollback must not overwrite a credential, vault record, or active selection that changed after the transaction snapshot.
- Ambiguous or stale process identity fails closed before mutation.

Transaction mechanisms and ownership are defined in [persistence-and-concurrency.md](persistence-and-concurrency.md). Threat boundaries are defined in [security-and-trust-model.md](security-and-trust-model.md).

## Routing

CURRENT IMPLEMENTATION candidate policy is evidenced by CandidateSelector:

1. Do not select the current account as its own replacement.
2. Reject expired or failed accounts.
3. Reject candidates below the configured minimum quota.
4. Require a vaulted session.
5. Reject candidates in cooldown.
6. Prefer non-reserve accounts, then higher remaining quota, then lower numeric priority, then account ID for deterministic tie-breaking.

INTENDED INVARIANT:

- Missing candidate telemetry cannot make an account eligible by pretending it has full quota.
- Automatic switching requires both a routing decision and a successful safety assessment.
- BUSY, active trajectories, unavailable telemetry, unsafe process provenance, or ambiguous identity block mutation.
- Manual recovery state disables automatic switching until an operator explicitly clears it after recovery.
- Threshold equality and rounding behavior must be defined by tests, not UI formatting.

## State machines

RoutingSafetyGate owns router progression such as IDLE, low-quota detection, pending, waiting for idle, switching, verification, cooldown, and manual recovery.

NativeAccountSwitchCoordinator owns transaction states such as preflight, snapshotting, credential application, process stop/restart, verification, finalization, rollback, and failure.

The two state machines must not be collapsed in prose or UI: the router decides whether and when to request a switch; the coordinator owns one switch transaction and its recovery result.

Primary evidence: RoutingModels.cs, SwitchModels.cs, RoutingSafetyGate, CandidateSelector, NativeAutoRouter, NativeAccountSwitchCoordinator, and their focused tests.

## Documentation boundary

This document does not define JSON property names, HTTP status codes, persistence layout, lock implementation, or security authorization. Those belong to the linked subject documents. Every invariant suitable for automation should ultimately have a focused test or schema check; prose alone is not proof.
