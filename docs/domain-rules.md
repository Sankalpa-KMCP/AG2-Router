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
- Missing or non-finite credit fields remain unknown, not observed zero; used credits require both monthly and available values.
- Per-model quotas remain separate unless there is evidence that variants share a quota bucket.
- Incompatible reset windows remain distinct unless upstream identity or other evidence proves a shared bucket.
- Percentages from different accounts are relative measures, not additive absolute capacity. Do not present a cross-account sum as total available quota.
- When multiple observations are proven to represent one pool, conflict resolution must be conservative and explicit.

CURRENT IMPLEMENTATION:

- AG2TelemetryNormalizer clamps observed numeric fractions and preserves prompt and Flow credit records separately.
- AG2TelemetryNormalizer and the TypeScript reference normalizer retain missing and non-finite model fractions as unknown. CanonicalizeModelQuotas preserves each source row because model/tier, presentation labels, and even equal reset instants do not prove shared capacity. No proximity tolerance or prefix match is used.
- NativeAutoRouter evaluates live active quota and candidate eligibility only for the explicitly configured workload model, tied to the verified active identity. Another exhausted model does not create routing pressure. Missing, unknown, or changed requested-model evidence cancels the automatic plan.
- Candidate observations originate from coherent live account quota telemetry and persist per account and canonical model. Production selection and execution admission use the same durable observation, not the legacy short-lived in-memory fallback. Newly enrolled accounts without an observation remain ineligible.
- Activity telemetry is IDLE only when an explicitly observed keyed trajectory collection is empty or every observed status is a known inactive status. Missing payloads, malformed collections, and unrecognized statuses remain UNKNOWN; RUNNING evidence remains BUSY.
- CandidateSelector requires known capacity for every pool matching a relevant model key; one healthy row cannot hide another matching unknown or exhausted row.
- The dashboard renders missing quota as UNKNOWN, distinct from an observed zero/exhausted quota.
- The Overview shows Gemini and Claude provider summaries derived from, but never written back to, individual model rows. Each summary uses the lowest observed fraction; unknown rows prevent a healthy percentage unless exhaustion is observed. Differing or incomplete reset evidence does not become one shared reset time. The detailed model rows remain visible in Telemetry & Quotas, and routing keeps its model-specific inputs.

Primary evidence: AG2TelemetryNormalizer, NativeAutoRouter, AG2TelemetryNormalizerTests, and NativeAutoRouterTests.

## Workload model and candidate evidence

CURRENT IMPLEMENTATION:

- WorkloadModelKey is explicit user-configured routing intent, exposed by dashboard Settings and persisted with routing configuration. AG2LiveAdapter associates that intent with the live active identity; it does not observe the currently selected IDE model dropdown.
- Model keys are trimmed and lowercased invariantly, then matched exactly against raw telemetry ModelOrTier keys. Friendly labels, provider summaries, prefixes, and unrelated low quota do not substitute for the configured model. Unset intent or an absent/unknown requested-model quota fails closed.
- Inactive observations may qualify only when their local observation time is not in the future and their age is strictly less than two hours. At two hours or later they are stale. Missing, unknown, invalid, exhausted, or below-minimum evidence is ineligible. resetTime is descriptive evidence and never creates replenished quota.
- Newer telemetry supersedes older evidence even when its fraction is unknown. Older healthy evidence cannot revive it. Relevant duplicate rows use weakest-link evidence: any unknown row prevents a healthy fraction, otherwise the lowest known fraction applies. Differently timed duplicate records remain unknown; incompatible reset windows do not become a shared reset. This conservative eligibility projection does not fabricate aggregate quota or assert that distinct pools are one pool.
- An automatic plan binds the exact durable account/model observation used for selection. Admission rereads it and requires the same record, current routing intent/configuration, and the same freshness/eligibility rules, including immediately before stopping the source process.
- When live target verification disproves cached evidence after target activation has begun, durable invalidation rejects all currently persisted model observations for that target account (account-wide, using the production store's atomic account-wide invalidation). Source and unrelated-account observations are preserved. Cooldown alone cannot later make the disproven records eligible; fresh qualifying live evidence is required.

Storage versions, malformed-document handling, and conditional writes belong in [persistence-and-concurrency.md](persistence-and-concurrency.md). Evidence: CandidateSelector, QuotaObservationEvidence, DurableQuotaObservationStore, DurableRoutingEvidenceTests, and TargetQuotaVerificationSwitchTests.

## Account identity

INTENDED INVARIANT:

- Account identity comes from verified Antigravity telemetry, not user-entered email alone.
- The WinCred entry at `gemini:antigravity` uses `UserName` as keyring metadata, not as account identity. The canonical upstream value is `antigravity`; structurally valid legacy email-valued or other noncanonical usernames remain admissible. Enrollment binds the session to verified telemetry and rejects a changed credential blob or username during capture.
- Metadata, the vaulted session, the active-account marker, the live WinCred value, and the verified process identity are related records but are not interchangeable proof.
- Enrollment must bind captured credential material to the observed account and must not leave a partial new account after failure.
- A switch is complete only after the restarted, verified process reports the target identity and the metadata commit succeeds.
- Rollback must not overwrite a credential, vault record, or active selection that changed after the transaction snapshot.
- Ambiguous or stale process identity fails closed before mutation.
- The live account email must match the persisted active account before live quota is attributed to that account or a switch is admitted. A mismatch is not an implicit account transition.

Transaction mechanisms and ownership are defined in [persistence-and-concurrency.md](persistence-and-concurrency.md). Threat boundaries are defined in [security-and-trust-model.md](security-and-trust-model.md).

## Routing

CURRENT IMPLEMENTATION candidate policy is evidenced by CandidateSelector:

1. Do not select the current account as its own replacement.
2. Reject expired or failed accounts.
3. Reject candidates below the configured minimum quota for the explicit requested model; aggregate quota or another healthy model cannot replace it.
4. Require a vaulted session.
5. Reject candidates in cooldown or with absent or expired quota observations.
6. Prefer non-reserve accounts, then higher remaining quota, then lower numeric priority, then account ID for deterministic tie-breaking.

The production automatic path is:

1. Read configured workload intent and verify the active identity against metadata.
2. Obtain live active quota for that exact model and evaluate low-quota pressure.
3. Read durable candidate observations and apply model, freshness, vault, cooldown, minimum-quota, and ranking rules.
4. Revalidate the selected observation and obtain recovery, quarantine, activity, identity, and process admission through the real switch coordinator.
5. Apply the target credential, restart the verified process generation, and verify target identity.
6. Obtain fresh live target quota, require the configured model and every relevant row to be valid, known, non-exhausted, and at least MinimumCandidateQuotaPercent **before metadata finalization**.
7. Commit guarded metadata on success; on failed target verification (including identity verification timeout, identity mismatch, or live quota rejection) after genuine target activation has begun (credential mutation / process launch), invalidate cached candidate quota observations account-wide and attempt conditional rollback with source identity verification. The switch journal durably persists target-activation provenance: NOT_ATTEMPTED until the target credential phase begins, MAY_HAVE_BEEN_ATTEMPTED from the write that precedes the credential writer invocation, preserved through ROLLING_BACK and QUARANTINED, and never regressing. Pre-activation failures (such as source process stop failures or snapshotting errors) preserve target observations intact, both within the running process and across restart via the persisted provenance. Other post-mutation failures also require rollback; unproved rollback remains recovery-blocking. Interrupted journal transitions preserve continuous durable recovery evidence — the canonical journal, its transition marker, or both — and recovery classifies the two files together with transition-aware, fail-closed semantics (owned by persistence-and-concurrency.md).

Manual switching uses the same coordinator and safety gates without requiring workload-model configuration or automatic target-quota thresholds.

INTENDED INVARIANT:

- Missing candidate telemetry cannot make an account eligible by pretending it has full quota.
- Automatic switching requires both a routing decision and a successful safety assessment.
- BUSY, active trajectories, unavailable telemetry, unsafe process provenance, or ambiguous identity block mutation.
- Target activation failures (identity timeout, identity mismatch, or quota verification rejection) invalidate cached candidate observations account-wide so the router cannot repeatedly loop attempting switches using stale positive quota. Invalidation is triggered strictly after target credential mutation/activation has begun; pre-activation failures leave candidate evidence untouched. The distinction is durably persisted as journal target-activation provenance, so source-only failures remain distinguishable from target-attempted failures across crash and restart. Ambiguous identity timeouts or transport failures do not prove session revocation and must not falsely mark account metadata as EXPIRED or FAILED unless an explicit auth-specific signal exists. When recovery resolution resolves an uncommitted or rolled-back switch journal, journals whose provenance is MAY_HAVE_BEEN_ATTEMPTED or UNKNOWN (legacy/ambiguous) retry target quota observation invalidation across the target account, and it must succeed before the journal can be deleted; NOT_ATTEMPTED journals resolve without touching target observations.
- RECORDED means the target credential boundary was never crossed — it does NOT mean the source runtime was never interrupted, because the journal is written before the source process stop. A RECORDED journal (any provenance value, including legacy UNKNOWN) may be cleaned up only after source coherence is established: the source account still exists in metadata, and live telemetry proves a connected source runtime bound to the source identity. Startup reconciliation probes that runtime within a bounded deadline; if coherence cannot be established, the journal is retained with ACTION_REQUIRED (never deleted as clean, never target-manipulated). The repository never relaunches the source language server itself: a safe relaunch would require reconstructing session-bound launch flags or persisting secret launch material, both of which the process lifecycle and journal invariants forbid.
- Manual recovery state disables automatic switching until an operator explicitly clears it after recovery.
- A transition marker binds one stable source/target pair and transition-valid activation provenance; contradictory entries fail closed before target-evidence invalidation. Resolution never publishes recovery `NONE` over surviving sidecar evidence or an unattributed disappearance during conditional cleanup. Rotation ownership and the durable predecessor bridge are defined in [persistence-and-concurrency.md](persistence-and-concurrency.md#continuous-durable-transition-evidence-transition-marker).
- Any non-`NONE` journal recovery state blocks backend manual and automatic switch admission, even without a quarantine marker; independent quarantine blocks admission as well.
- Threshold equality and rounding behavior must be defined by tests, not UI formatting.

## State machines

RoutingSafetyGate owns router progression such as IDLE, low-quota detection, pending, waiting for idle, switching, verification, cooldown, and manual recovery.

NativeAccountSwitchCoordinator owns transaction states such as preflight, snapshotting, credential application, process stop/restart, verification, finalization, rollback, and failure.

A definitively completed success or verified rollback can return the same coordinator instance to idle only after ownership cleanup and proof that no unresolved journal, recovery state, or quarantine remains. Terminal results remain available as last-result history. Recovery-requiring states remain blocking; see the persistence owner for lifecycle details.

The two state machines must not be collapsed in prose or UI: the router decides whether and when to request a switch; the coordinator owns one switch transaction and its recovery result.

Primary evidence: RoutingModels.cs, SwitchModels.cs, RoutingSafetyGate, CandidateSelector, NativeAutoRouter, NativeAccountSwitchCoordinator, and their focused tests.

## Candidate pool status and transport boundary

When automatic routing cannot select a candidate, the router assesses and reports an explicit `CandidatePoolStatusDto` distinguishing root causes:
- `NO_ENROLLED_ALTERNATIVES`: Zero alternative accounts exist in the store.
- `VALIDATION_OR_SESSION_FAILED`: All candidate accounts fail validation or lack a vaulted session.
- `ALL_IN_COOLDOWN`: All eligible candidates are in stabilization cooldown.
- `RESERVE_ONLY`: All alternatives are flagged reserve-only.
- `EVIDENCE_STALE_OR_UNKNOWN`: Quota observations are expired, unconfigured, or unknown.
- `ALL_EXHAUSTED`: All candidate accounts have observed quota <= 0% or `is_exhausted == true`.
- `ALL_BELOW_MINIMUM`: All candidate accounts with fresh observations are below `MinimumCandidateQuotaPercent`.
- `QUOTA_DEPLETED`: Candidate accounts are a mixture of exhausted and below-minimum quota.

Transport boundary invariant:
- Local loopback Connect-RPC connects to the local Antigravity LanguageServer daemon. Upstream HTTP 503/529 cloud overload conditions are unobservable by the local RPC client.
- Transport errors, missing telemetry, or daemon unresponsiveness must NEVER mark an account's quota as 0% or exhausted; they are treated fail-closed as unavailable telemetry (`EVIDENCE_STALE_OR_UNKNOWN`). Quota exhaustion is driven solely by affirmative positive evidence (`remaining_fraction <= 0` or `is_exhausted == true`).
- Earliest reset times reflect only affirmative, non-expired future timestamps parsed from observed telemetry; timestamps are never fabricated.

Pool-status publication invariant:
- A candidate pool status is authoritative only when produced by a real routing evaluation from full selector inputs. Status derived from an empty candidate-evaluation collection is not a pool assessment and must never be published or served.
- The router pairs cached pool status with its routing-context generation and durable observation revision. Identity, evidence-cache, manual-result, and configuration changes clear it. Every public pool-status accessor validates evidence freshness: synchronous `GetStatus()` checks the shared store's known committed revision; async candidate-evidence reads also reread disk. Completed coordinator/recovery mutations through the shared production store therefore reject old cache immediately. Independent store/process changes require a durable async read. See [the revision and concurrency contract](persistence-and-concurrency.md#durable-quota-observations) for UTC-instant revision identity and exact freshness boundaries.
- The dashboard never manufactures a pool assessment. When no current evaluated status exists for the present generation and revision, PoolStatus is null/unavailable while the candidate rows remain current.
- A status computation cannot republish old READY over a completed shared-store invalidation: publication checks the known committed revision again under the router lock. Pool status is observational; revision checks do not lock out independent process writes after the final read, and switching always revalidates durable candidate evidence independently.

## Documentation boundary

This document does not define JSON property names, HTTP status codes, persistence layout, lock implementation, or security authorization. Those belong to the linked subject documents. Every invariant suitable for automation should ultimately have a focused test or schema check; prose alone is not proof.
