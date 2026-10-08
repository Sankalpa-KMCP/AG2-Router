# ADR-006: RECORDED journal cleanup requires source-runtime coherence

Status: ACCEPTED (implemented)

Date: 2026-10-07

## Context

The switch journal is written with state `RECORDED` (and target-activation provenance `NOT_ATTEMPTED`) strictly before the source Antigravity process stop. A crash after that write and the stop therefore leaves a journal that startup reconciliation used to delete as a proven-clean pre-mutation record — while the source language server remained stopped. No credential loss occurs, but no repository-owned path restored or proved the source runtime, and external Antigravity supervision cannot be assumed. The same blind cleanup existed in the operator-triggered resolution path.

## Decision

`RECORDED` journals (any provenance value, including legacy `UNKNOWN`) may be cleaned up — at startup reconciliation or during resolution — only after source coherence is established:

1. Durable check (no live dependency): the journal's source account still exists in metadata with an email. RECORDED transactions mutated no credentials and no metadata, so validation status, vault contents, and the active selection are not decision-relevant for cleanup.
2. Runtime proof (bounded): live telemetry must report a connected server whose identity matches the source account email. The entire proof, including each RPC probe, is bounded by the verification deadline so recovery can never retain startup ownership past it.
3. Proven → conditional journal cleanup. Metadata-incoherent (source account missing) → retained with vault quarantine. Runtime-unproven (absent, mismatched, or unavailable) → retained with `ACTION_REQUIRED` and no vault quarantine.

Target quota evidence is never manipulated for `RECORDED` journals, and `NOT_ATTEMPTED` is never converted to `MAY_HAVE_BEEN_ATTEMPTED` by recovery.

Startup reconciliation for all other journal states, and boots with no journal, continue to make zero live RPC calls; the bounded runtime probe exists only on the RECORDED crash-recovery path, whose cleanup decision genuinely requires live evidence.

## Rationale

This is the smallest safe design that makes "clean" mean "proven coherent" instead of "journal absent". The repository deliberately does not relaunch the source language server: the only safe launch path (`WindowsAG2ProcessLifecycle.LaunchAsync`) requires a previously verified live-process snapshot — executable identity plus session-bound launch arguments — and reconstructing those flags or persisting secret launch material would violate the lifecycle's stated invariant and the journal's zero-secret invariant. Antigravity's own client supervision (or the operator) is the only safe launcher; the bounded proof consumes such a restoration when it happens and keeps recovery blocked when it does not. No new source-runtime provenance field is needed: runtime state is established by direct proof at recovery time, and no decision ever resumes the interrupted transaction.

## Alternatives considered

- Relaunching the source runtime from the repository: rejected — requires reconstructing session-bound launch flags (forbidden lifecycle invariant) or persisting executable/secret launch material in the journal (forbidden zero-secret invariant).
- A persisted source-interruption provenance field (`UNKNOWN`/`NOT_INTERRUPTED`/`MAY_HAVE_BEEN_INTERRUPTED`): rejected — recovery never resumes the transaction, so the only decision (clean vs retain) is fully determined by the runtime coherence proof; a field would record what the proof establishes anyway.
- Keeping unconditional `RECORDED` cleanup at startup and only fixing resolution: rejected — startup is exactly where the stale-clean deletion happens.
- Waiting indefinitely at startup for the runtime: rejected — recovery ownership must be bounded.

## Consequences

Positive: a RECORDED crash can no longer be cleaned while the source runtime is unproven; recovery is deterministic, bounded, and idempotent; ordinary boots stay offline-safe.

Negative: a RECORDED journal whose source runtime stays down requires operator resolution (the app reports the retained journal and the recovery action needed); the RECORDED crash-recovery path performs bounded live probes that other startup paths do not.

## Compatibility/migration impact

No persisted-format change. The existing startup-reconciliation test invariant is re-scoped: ordinary reconciliation (no journal, or any non-`RECORDED` state) still makes zero live calls; only RECORDED recovery probes telemetry.

### Amendment (2026-10-07, PROMPT #025)

Cleanup ownership is bound to the proved entry. The bounded proof authorizes removing only the exact journal entry it proved, so startup cleanup — for `RECORDED` and for the committed-safe `TARGET_IDENTITY_VERIFIED_PRECOMMIT` state alike — uses the store's exact-entry conditional deletion (the same primitive operator resolution uses) instead of unconditional deletion. If the persisted journal changed while the proof ran, the replacement survives for its own reconciliation, the result is degraded with `ACTION_REQUIRED` (never clean, never admission-opening), and the repository does not recursively reconcile the replacement inside the same cleanup. If the proved entry disappears under recovery ownership, the removal's owner cannot be established and the outcome fails closed the same way. A persistence failure of the conditional delete also fails closed and retains the journal.

### Amendment (2026-10-07, PROMPT #027)

The ownership model extends to the whole switch/recovery lifecycle:

1. Journal cleanup is exact-entry-owned: a proof authorizes removing only the exact entry it proved (PROMPT #025 amendment).
2. A switch transaction deletes only the exact journal entry it successfully persisted. The transaction tracks its owned entry explicitly: ownership starts null, advances only after a write successfully commits, and a failed or ambiguous write advances nothing. A transaction that has not written a journal performs no journal cleanup at all, so a journal written by another actor during preflight or plan work always survives.
3. Deletion of the prior entry is not sufficient proof of path clearance: the conditional deletion's comparison/disposition window admits a replacement written at the canonical path (the opened handle deletes the original file object wherever its link now points). Every `Deleted` outcome is therefore followed by a bounded post-cleanup read of the canonical path; only a genuinely absent journal publishes Clean, and a surviving replacement or corrupt bytes fail closed with recovery blocked.
4. Switch admission never trusts a stale in-memory `NONE`: before performing any mutation, the transaction re-reads the canonical journal path, and any journal there fails closed into blocking recovery.
5. Cooperative locks do not replace persisted ownership validation: non-cooperating writers (external tools, tests, manual edits) are exactly the case the persisted entry and the post-cleanup path proof arbitrate. External mutation must produce fail-closed recovery behavior, never deletion or false Clean.

Transaction cleanup outcomes preserve the distinction between business outcome and cleanup uncertainty: a successful commit or verified rollback keeps its result code while a surviving or mismatched journal publishes `ACTION_REQUIRED` recovery. Uncertain post-cleanup readbacks defer to the transaction-exit proof, which re-reads the same disk state and applies its established classification. Unexpected disappearance of an owned entry fails closed immediately. A later fresh reconciliation of a genuinely absent journal still clears state per the standing no-journal startup policy, so no outcome is permanently unrecoverable.

### Amendment (2026-10-07, PROMPT #029)

Write-side ownership completes the model: journal creation and every state transition are ownership-conditional, not only cleanup.

1. The first journal write is an atomic create-if-absent: the serialized entry is written to a durable same-directory temporary file (write-through, flushed to disk) and published with a non-replacing rename that the filesystem fails atomically when the destination name exists. A foreign journal or corrupt bytes appearing after admission can therefore never be overwritten by the transaction's first write.
2. Every transaction state transition replaces only the exact entry the transaction currently owns, composed from the two audited ownership-checked primitives: the exact-entry conditional delete (comparison and delete disposition share one handle whose share mode is `FILE_SHARE_READ | FILE_SHARE_DELETE` — it does NOT exclude external rename or delete; deletion is authorized by the exact-entry content comparison, and external interference survives as durable evidence that post-cleanup and admission rereads observe fail-closed — see the PROMPT #032 amendment below for the corrected safety argument) followed by the atomic create-if-absent publish. If an actor seizes the freed name in the inter-primitive gap, the publish fails and the occupying bytes survive untouched; no interleaving can overwrite a foreign or replacement journal.
3. Journal ownership is acquired only by successful conditional persistence and is never inferred or adopted: a mismatched journal is never read and continued as the transaction's own state, and a failed or ambiguous write advances nothing. Ambiguous publish outcomes are classified by reading the canonical path back (next entry proven durable → ownership recovered; expected entry intact → transition simply failed; foreign or corrupt bytes → ownership lost; unprovable → fail closed).
4. When a transaction loses journal ownership mid-flight, all further journal writes are suppressed, required compensation (target-evidence invalidation, source credential/process restoration) still runs to completion, and the surviving foreign journal remains canonical recovery evidence with recovery blocked. The rollback-state persistence failure rule is unchanged: with the prior owned journal still on disk, compensation is not performed under an unrecorded rollback.
5. Cleanup remains exact-entry conditional with the post-cleanup canonical-path proof, and the durable admission reread remains defense-in-depth; neither is made obsolete by the atomic create.

Trade-off: the conditional transition deletes the owned entry before publishing the next state. A process crash inside that two-syscall window (the publish is a single rename of an already-fsynced temporary file) leaves no journal. For pre-credential-mutation transitions that equals the reconciled outcome of the old journal; for post-credential-mutation transitions it forfeits the explicit quarantine signal, where the live-identity gate still fails routing and further switching closed. This was weighed against the only alternative bounded design (in-place writes through the pinned handle), which trades the same window for corrupt-journal manual remediation on every crash during a write.

### Amendment (2026-10-07, PROMPT #032)

The trade-off above is superseded by a durable transition sidecar: a zero-secret transition marker (`switch-journal.json.transition`) now exists beside the canonical journal throughout every owned state transition, closing the crash window instead of accepting it.

1. **Corrected safety argument for the conditional delete.** The PROMPT #029 amendment claimed the delete handle's share mode "kernel-enforced" protection against rename/delete by a non-cooperating actor. That was inaccurate: the handle is opened with `GENERIC_READ | DELETE` under share mode `FILE_SHARE_READ | FILE_SHARE_DELETE`, which explicitly PERMITS other actors to open the file for delete and rename it or delete it while the handle is open (only writers are excluded). The actual safety argument is layered: (a) the exact-entry comparison limits what the transaction intentionally deletes; (b) external rename/delete remains possible and is never assumed away; (c) the create-if-absent publish fails atomically on an occupied name, so whatever occupies the canonical path after any interference survives untouched; (d) the transition marker preserves crash evidence across the delete-to-publish window; and (e) post-cleanup path proofs, admission rereads, and startup reconciliation reread both paths and fail closed on surviving evidence.
2. **Continuous durable recovery evidence.** From the first durable journal state until terminal cleanup is proven complete, every crash boundary leaves the canonical journal, the marker, or both — never both absent. The marker nests the exact owned entry and the exact intended successor, is created if-absent before the predecessor can be removed, and is removed only by exact-entry conditional deletion after the successor is proven durable. The successor's flushed temporary bytes are prepared before the predecessor is removed. A transaction may replace only a marker provably its own (same transaction ID, expected entry identical to the entry still owned); foreign, materially different, or corrupt markers fail the transition closed with the occupant preserved.
3. **Transition-aware startup and resolution semantics.** Startup reconciliation, admission, and operator resolution classify the canonical journal and the marker together. The `RECORDED` → `CREDENTIAL_APPLYING` gap is the only one that can publish clean, and only through the RECORDED source-runtime proof, because the credential writer is invoked strictly after the `CREDENTIAL_APPLYING` journal lands — an unpublished successor with a durable marker proves the target boundary was never crossed. Every other gap (target-attempted, rollback-intent, quarantine) retains quarantine-level conservatism. Corrupt, unsupported, or unreadable markers fail closed (`NOT_RESOLVABLE`) and are never deleted, even when the canonical journal looks valid. A durable successor that exactly matches its marker proves the marker stale; it is removed exactly and the successor reconciled per its own state.
4. **Compatibility boundary.** There is no global data-format version gate: an older binary ignores the marker file and would report clean on the D1 crash shape. Downgrade to builds that predate the marker is unsupported for marker-bearing state; the canonical schema-v1 journal without a marker remains readable unchanged (no destructive migration).

Alternatives considered for closing the crash window: in-place journal rewrite through a pinned handle (rejected in the #029 trade-off — corrupt-journal remediation on every crash-during-write); an immutable-generation journal subsystem or embedded database (rejected — out of authorized scope, disproportionate to the gap); accepting the window and relying on the live-identity gate (rejected — it forfeits the explicit quarantine signal exactly where target mutation may have occurred). The sidecar was chosen because it closes the gap with one additive zero-secret file, exact-entry ownership primitives symmetric to the journal's, and no change to the canonical journal schema.

### Amendment (2026-10-07): exact marker rotation and joint recovery clearance

The earlier same-transaction/expected-entry eligibility wording is superseded: active marker ownership comes only from a successful persistence result and covers the whole marker, including `createdAt` and `next`. A marker read from disk cannot be adopted under a partial match. Store exceptions after marker acquisition must return that acquired ownership to the transaction so rollback can distinguish its own retained evidence from interference.

A successor publication failure can leave only the owned transition marker. Rotating that marker now uses the existing canonical journal as a durable bridge: retain the old marker, restore its exact previously owned predecessor with create-if-absent if needed, prove it, and hold a revalidating read handle that excludes write/delete sharing across exact old-marker deletion and replacement-marker publication. The guard ends before the ordinary canonical transition. A foreign/corrupt canonical occupant or failed/uncertain restoration preserves the old marker. This replaces delete-then-create rotation justified merely by remembered predecessor ownership. Restoring the predecessor changes only recovery evidence, not runtime or credential state.

This keeps the accepted one-sidecar architecture. Deleting the marker faster or trusting an in-memory predecessor was rejected because neither guarantees evidence survives a crash. A second sidecar, immutable generations, or a database was unnecessary: the exact previously durable canonical entry provides the bridge without a new persistence format. The trade-off is an additional canonical publication/readback after a covered gap and a short-lived read guard; restoration failures retain manual recovery rather than removing uncertainty.

Resolution clearance is a bounded proof of both paths. Unexpected conditional-delete absence remains blocking concurrent mutation, even when a later read finds both paths absent; a subsequent explicit resolution can establish a legitimate empty pair. Corrupt/unsupported markers yield `NOT_RESOLVABLE`; I/O/read uncertainty yields `UNKNOWN`, correcting the earlier classification wording. No cleanup success or `NoJournal` result ignores surviving sidecar evidence.

Marker validation also requires stable source/target identities and the actual coordinator's provenance transitions. Source-only RECORDED rollback preserves `NOT_ATTEMPTED`; activation entry raises it to `MAY_HAVE_BEEN_ATTEMPTED`, which subsequent target-attempted transitions preserve; quarantine preserves the rollback provenance exactly. New markers cannot carry `UNKNOWN` or reduce activation evidence. Legacy canonical journals retain conservative UNKNOWN parsing without migration. This prevents contradictory successor fields from steering marker-only resolution or redirecting target-quota invalidation.

Evidence: `TransitionMarkerHardeningTests`, `SwitchTransitionMarkerTests`, and the existing crash-recovery/ownership suites. Current operational detail remains in [persistence-and-concurrency.md](../persistence-and-concurrency.md).

### Amendment (2026-10-07): exact canonical companion guard for nonterminal transition-marker removal (R1)

The R1 durability failure demonstrated that normal successor-marker cleanup could erase the last durable recovery evidence: if a non-cooperating actor removed the canonical successor during the marker-deletion window, the marker was still deleted under conditional deletion, leaving both artifacts absent while the coordinator continued with forward credential mutation. A subsequent crash recreated the D1 failure class.

To close R1 without redesigning the persistence model, every nonterminal transition-marker removal — including normal successor cleanup (`TryAdvanceOwnedJournalAsync`), startup stale-marker cleanup (`ReconcileStartupJournalAsync`), and operator resolution stale-marker cleanup (`ResolveQuarantinedJournalAsync`) — is made contingent on a continuously protected exact canonical companion:

1. `AcquireExactJournalGuardAsync` verifies that the canonical journal currently exists, parses validly, and exactly matches the expected entry (`EntriesMatch`). On Windows, it opens the file under `FileAccess.Read, FileShare.Read`, actively excluding delete, rename, and write sharing to external actors while the guard handle is held.
2. `DeleteTransitionMarkerWhileCanonicalGuardedAsync` holds this canonical companion guard throughout the entire marker deletion critical section.
3. If the canonical companion is missing, mismatched, corrupt, or cannot be guarded, the transition marker is never deleted; the operation fails closed with the marker preserved on disk.
4. In normal transitions, canonical companion failure causes `TryAdvanceOwnedJournalAsync` to return a persistence failure without an owned entry (`applyingEntry == null`), immediately aborting forward credential mutation (`_credentials.WriteCount == 0`) and triggering blocking recovery.
5. In startup reconciliation and operator resolution, canonical companion failure preserves the transition marker and fails closed, requiring manual resolution.
6. Non-Windows platforms fail closed with `UnsupportedPlatform`.

Evidence: `ExactJournalGuardTests.cs`, `TransitionMarkerCrashRecoveryTests.cs`, `SwitchJournalStore.cs`, `NativeAccountSwitchCoordinator.cs`.

## Related

[ADR-001](ADR-001-switch-transaction-journal.md), [ADR-005](ADR-005-target-activation-provenance.md) (amended), [persistence-and-concurrency.md](../persistence-and-concurrency.md), [domain-rules.md](../domain-rules.md). Evidence: `NativeAccountSwitchCoordinator.cs` (`VerifyDurableSourceCoherenceAsync`, `ProveSourceRuntimeCoherenceAsync`, `TryAdvanceOwnedJournalAsync`, `ReconcileRecordedMarkerGapAsync`), `SwitchTransitionMarker.cs`, `RecordedSourceRuntimeRecoveryTests.cs`, `TransitionMarkerCrashRecoveryTests.cs`, `ExactJournalGuardTests.cs`.
