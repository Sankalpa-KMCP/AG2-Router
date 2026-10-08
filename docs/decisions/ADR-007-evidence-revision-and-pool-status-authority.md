# ADR-007: Durable evidence revision and authoritative pool-status publication

Status: ACCEPTED (implemented)

Date: 2026-10-07

## Context

The router-local pool-status generation (ADR-005-era work) observes only mutations made inside the router. Durable evidence mutations performed externally — coordinator target-evidence invalidation, recovery-resolution invalidation, telemetry observation writes — were invisible to it, so a status request could serve candidate rows read from newer evidence alongside a cached pool status computed from older evidence. Separately, the dashboard's candidate-evidence read synthesized a pool assessment by calling `AssessPoolStatus` with an empty candidate-evaluation collection, which always reported "no usable candidate" even when candidate rows showed a healthy usable account — a fabricated, semantically invalid status.

## Decision

1. The quota observation store exposes an evidence-revision boundary: `GetObservationSnapshotAsync` returns the consolidated observation rows and the durable revision they were read at from one consistent load, and `GetObservationRevisionAsync` returns the current revision. In the durable store the revision is the document's update timestamp, written strictly monotonically on every committed mutation (a same-tick or clamped caller timestamp advances by one tick), so it changes across every shipped writer, every store instance, and across processes, and is stable across non-mutating reads. Interface defaults report a constant revision so non-revision stores make the check trivially pass.
2. The router pairs the published pool status with the evidence revision its observations were read at. Publication (evaluation cycles) requires the routing-context generation and a re-read evidence revision to still match the snapshot; candidate-evidence reads validate both before serving the cache and lazily drop a cache whose revision no longer matches.
3. The dashboard synthesizes no pool assessment. Pool status comes only from authoritative evaluations through the validated cache; when no current evaluated status exists, `PoolStatus` is null/unavailable while the candidate rows remain current. The empty-evaluations fallback was removed.

## Rationale

A durable-state-derived revision needs no cross-instance or cross-process coordination and survives restarts by construction, which a per-instance counter cannot guarantee (multiple `DurableQuotaObservationStore` instances over one file exist in shipped construction and tests). Pairing status with the revision makes staleness detectable by every consumer without coordinator-to-router coupling, and publishing under both generation and revision closes the remaining window where durable evidence could move between a computation and its publication. Serving null instead of a fabricated assessment follows the repository's truthfulness rules: unavailable is honest, a contradicted "no usable candidate" is not.

## Alternatives considered

- Per-instance in-memory revision counter: rejected — misses mutations through other store instances over the same document and revisions restart at process boundaries.
- Reusing `SelectBestCandidate` from the dashboard with a synthetic active-quota input: rejected — the selector requires live active-quota telemetry the dashboard read deliberately avoids, and manufacturing inputs repeats the fabrication defect. An extracted evaluation-only selector core was unnecessary once the cache is revision-validated.
- Coordinator-to-router notification callbacks for invalidation: rejected — direct coupling across subsystem ownership boundaries; the revision boundary achieves the same guarantee through the shared store.

## Consequences

Positive: durable evidence revisions make stale pool cache detectable without a new persistence subsystem; the dashboard does not fabricate an assessment that contradicts its candidate rows. Exact synchronous and asynchronous freshness boundaries are clarified by the amendment below.

Negative: each evaluation and dashboard read performs one extra small durable read (revision re-check); a pool status may be briefly unavailable between an evidence mutation and the next authoritative evaluation — an honest gap; document-timestamp values in the observation store can advance by one tick beyond a caller-supplied clamped timestamp.

## Compatibility/migration impact

`IQuotaObservationStore` gains two members with revision-neutral defaults, so existing implementations (including test fakes) compile and behave unchanged. `QuotaObservationSnapshot` is a new Core model. The dashboard's `PoolStatus` remains nullable and frontend types already declare it optional; no UI change. Document `updatedAt` semantics are unchanged except for the strict monotonicity guarantee.

## Amendment: UTC revision identity and synchronous status freshness

Revision identity uses `UpdatedAt.UtcDateTime.Ticks`: offsets describe representations, not separate revision domains. Historical offset-bearing files remain readable; new mutation timestamps are UTC-normalized and strictly advance by instant, including equal/older proposals. A maximum UTC timestamp cannot be advanced, so the mutation fails without committing. Snapshot rows and revision still come from one document load.

The store additionally exposes `KnownObservationRevision`, a thread-safe last successfully loaded/committed revision, updated after persistence succeeds and before mutation completion. Synchronous `GetStatus()` validates the cache against it. Shipped coordinator, recovery, router, and planner share the same store; this closes their immediate invalidation gap without callbacks or synchronous disk I/O. Async accessors continue rereading durable revisions for independent instances/processes. Revision-neutral stores default to zero for all revision members.

Publication and async serving also check the known revision under the router state lock, preventing a shared-store commit between durable revision read and lock acquisition from republishing old READY. Independent writes after the last read remain an observational race; synchronous known revision does not claim disk freshness. This refines the earlier broad claim of immediate freshness for every consumer. Actual switch admission independently revalidates durable evidence and does not use cached pool status as authorization. Null cache remains temporary: a later real evaluation can publish current status.

This amendment retains the existing revision/cache decision, adds no journal schema or UI change, and avoids both event lifetime management and a broader async consumer migration. Evidence: `EvidenceRevisionStatusHardeningTests.cs` and the recovery test's direct synchronous assertion.

## Related

[ADR-005](ADR-005-target-activation-provenance.md), [persistence-and-concurrency.md](../persistence-and-concurrency.md), [domain-rules.md](../domain-rules.md). Evidence: `QuotaObservationModels.cs`, `IQuotaObservationStore.cs`, `DurableQuotaObservationStore.cs`, `NativeAutoRouter.cs`, `RecordedSourceRuntimeRecoveryTests.cs`, `SwitchActivationProvenanceTests.cs`.
