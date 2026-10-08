# ADR-005: Persist target-activation provenance in the switch journal

Status: ACCEPTED (implemented)

Date: 2026-10-06

## Context

Recovery resolution of a retained switch journal (`ROLLING_BACK` / `QUARANTINED`) used to invalidate all cached target quota observations whenever the journal's target differed from the proven-coherent active account. That over-invalidated untouched targets: a source-only failure (source stop or rollback-snapshot failure, where target credential application was never attempted) that still left a retained journal — for example because journal deletion failed or the process crashed before deletion — caused startup recovery to reject the target's perfectly valid routing evidence. The same-process runtime boundary was already correct (the coordinator tracked target activation in memory), but no persisted field survived the journal's later states, so a restart could not distinguish source-only from target-attempted failures.

## Decision

Extend the schema-v1 switch journal with one optional, backward-compatible field, `targetActivationProvenance`, carrying a three-state value:

- `NOT_ATTEMPTED` — persisted with the `RECORDED` write; proves the target credential boundary was never crossed. Recovery must preserve target observations.
- `MAY_HAVE_BEEN_ATTEMPTED` — persisted by the `CREDENTIAL_APPLYING` write, which already lands strictly before the credential writer is invoked, so a throwing or partially-applied credential write still leaves durable proof. Preserved verbatim through `ROLLING_BACK` and `QUARANTINED`; monotonic, never regresses. Recovery must durably invalidate target observations before the journal may be cleared.
- `UNKNOWN` — the zero value and the reading of a missing field (legacy journals). Recovery treats it conservatively like `MAY_HAVE_BEEN_ATTEMPTED` rather than assuming `NOT_ATTEMPTED`.

Recovery resolution now gates target-quota invalidation on this persisted provenance instead of the identity comparison alone. When the journal's target is the proven-coherent active account, invalidation remains unnecessary for every provenance value.

The same change introduced a monotonic pool-status generation in `NativeAutoRouter`: every identity, evidence-purge, cache, manual-switch-result, or configuration change increments it under the router state lock and clears the cached pool status, and every publication path (evaluation cycles and candidate-evidence reads) publishes only while its captured generation is still current. Manual and automatic switch-result handling invalidate the cached pool status symmetrically.

## Rationale

A nullable-free three-state enum with `UNKNOWN` as the default keeps every possible default conservative: absent data, legacy data, and future deserialization accidents all read as "assume the target may have been touched", never as "target untouched". Reusing the existing `CREDENTIAL_APPLYING` write as the persistence point costs no additional journal I/O and is already ordered before the credential writer, which is exactly the uncertainty boundary the field must capture. Gating recovery invalidation on persisted provenance removes the last over-invalidation path while preserving fail-closed behavior wherever activation uncertainty exists.

The pool-status generation closes the remaining publication race found by review: a status computation that straddles an evidence invalidation could otherwise republish an older result over the newer state.

## Alternatives considered

- Persist a two-state boolean ("activation attempted"): rejected — a missing field would deserialize to `false` ("not attempted"), silently giving legacy and corrupt records the optimistic interpretation.
- Bump the journal schema version for the new field: rejected — an optional field is backward-compatible with schema v1; a version bump would make every pre-existing journal unreadable to the strict version check for no safety gain.
- Derive provenance at recovery time from journal state (e.g., treat `CREDENTIAL_APPLYING` as attempted, `ROLLING_BACK` as not): rejected — `ROLLING_BACK` is reachable both before and after the credential boundary, so state alone cannot distinguish the two cases; this is precisely the ambiguity the field removes.
- Invalidate target evidence unconditionally during recovery (status quo): rejected — it destroys valid routing evidence after source-only failures and contradicts the documented source-only preservation guarantee.
- Fix the pool-status race by clearing the cache more often without a generation: rejected — sequential clearing was already in place; it cannot stop a concurrent computation that read an older snapshot from publishing over newer state.

## Consequences

Positive: source-only failures no longer destroy untouched target evidence across crash and restart; target-attempted failures still cannot resurrect stale positive quota; recovery retry remains idempotent; legacy journals receive conservative treatment without a migration; stale pool-status publication after concurrent invalidation is impossible.

Negative: legacy journals with ambiguous provenance can be over-invalidated (an untouched target loses its cached evidence once) — accepted because it prevents stale-positive automatic routing after uncertain target mutation; the journal record grows by one short string; router publication paths gained one generation comparison.

## Compatibility/migration impact

Old journals (without the field) remain readable and deserialize with `UNKNOWN` provenance; schema stays v1. New journals are readable by this and future code; older code would ignore the unknown property but is not expected to run against newer journals. No frontend or loopback wire contract changes; the field exists only in the durable journal record.

### Amendment (2026-10-07)

The PROMPT #019 review established that `NOT_ATTEMPTED` governs target evidence only: because the `RECORDED` write lands before the source process stop, a `RECORDED`/`NOT_ATTEMPTED` journal cannot also prove the source runtime was never interrupted. Recovery therefore requires source-runtime coherence before cleaning such journals; see [ADR-006](ADR-006-recorded-source-runtime-coherence-recovery.md). The statement above that a `NOT_ATTEMPTED` journal "can resolve normally" holds for target evidence, which remains exactly the guarantee this record introduced.

## Related

[ADR-001](ADR-001-switch-transaction-journal.md) (journal design this extends), [persistence-and-concurrency.md](../persistence-and-concurrency.md) (journal, provenance-gated resolution, pool-status generation), [domain-rules.md](../domain-rules.md) (routing invalidation rules), [invariants.md](../invariants.md). Evidence: `SwitchJournal.cs`, `SwitchJournalStore.cs`, `NativeAccountSwitchCoordinator.cs`, `NativeAutoRouter.cs`, `SwitchActivationProvenanceTests.cs`.
