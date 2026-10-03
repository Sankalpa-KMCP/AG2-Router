# Persistence and concurrency

This document owns durable stores, mutation ownership, synchronization, transaction compensation, and lifecycle sequencing. Domain outcomes are in [domain-rules.md](domain-rules.md); trust assumptions are in [security-and-trust-model.md](security-and-trust-model.md).

## State inventory

| State | Current owner and location | Durability |
| --- | --- | --- |
| Account metadata and active account ID | LocalMetadataAccountStore; DATA_DIR/accounts.json when overridden, otherwise per-user LocalAppData/AG2-Router/data/accounts.json | Durable JSON |
| Vaulted sessions | SessionVault; per-user LocalAppData/AG2-Router/vault/sessions.dat by default | Durable versioned JSON envelope containing DPAPI ciphertext |
| Live Antigravity credential | Windows Credential Manager canonical target | External OS-managed state |
| Router configuration | NativeAutoRouter; config.json beside accounts.json | Durable; loaded before polling starts |
| Candidate quota observations | DurableQuotaObservationStore; quota-observations.json beside accounts.json | Durable, versioned account/model evidence |
| Router cooldowns and legacy quota fallback | NativeAutoRouter | Process memory only; fallback is not production candidate authority |
| Poll/status snapshot | TelemetryPollingCoordinator | Process memory only |
| Switch status and last result | NativeAccountSwitchCoordinator | Process memory only |
| Switch transaction journal | SwitchJournalStore; switch-journal.json beside accounts.json | Durable, zero-secret recovery record |
| WebView2 browser profile | WebView2EnvironmentCoordinator under per-user LocalAppData/AG2-Router/webview2 | WebView2-managed persistent state |
| Autostart preference | WindowsRegistryAutostartService | Per-user registry state |

Paths above describe application contracts, not permission to inspect live user data. Tests must redirect storage to task-owned temporary locations.

## File durability

DurableFileWriter implements the shared write primitive for account metadata, vault state, routing configuration, quota observations, and switch journal:

1. Resolve the full destination and create its directory.
2. Write a unique temporary sibling using asynchronous write-through.
3. Flush writer and stream to disk.
4. Replace an existing destination or move the temporary file into place.
5. Best-effort remove a temporary file when replacement did not complete.

Readers treat malformed or unsupported persisted data as an error. SessionVault in particular fails closed on empty, truncated, wrong-magic, wrong-version, non-object root envelopes, invalid or non-object `records` containers (such as arrays, primitives, or null), malformed record entry shapes (such as array/primitive values, missing required fields, non-string fields, mismatched account IDs, or prototype-pollution keys), or identity-mismatched content rather than overwriting it with an empty vault. Corrupted vault files are never overwritten or silently normalized; existing file bytes are strictly preserved for forensic recovery, and any mutation attempt throws `VaultCorruptionError`.

Furthermore, SessionVault requires authoritative durable readback verification before reporting ordinary save success: after atomic replacement, the written envelope is re-read from disk and validated, proving that the record intended for the account was durably serialized and matches the committed record byte-for-byte in all required fields. If write throws, readback fails, the file is unreadable, the record is missing, or the record differs, the save never reports success and fails closed with `VaultCorruptionError` or `VaultMutationUncertainError`.

Atomic replacement protects the destination snapshot; it does not by itself serialize multiple read-modify-write actors. Locks and rebasing are separate requirements.

### Router configuration validation

RouterConfig is validated immediately when loaded by NativeAutoRouter during initialization. Malformed JSON or out-of-contract persisted configuration (such as invalid polling intervals or threshold percentages) is rejected before polling or routing startup begins. In such cases, application startup fails visibly with an actionable error dialog rather than silently falling back to defaults or running with invalid runtime constraints.

Partial or older configuration files whose missing fields deserialize to current valid defaults remain supported where current serializer and model semantics provide that behavior. However, previously API-written out-of-range values or corrupted configuration files may require correction before startup can succeed. This is an intentional fail-closed contract designed to prevent routing loops and invalid timers, not an accidental crash.

WorkloadModelKey persists in config.json. Older files without it remain valid but leave automatic workload intent unconfigured; they do not infer a model. User-facing configuration and validation names are defined in [api-contracts.md](api-contracts.md).

### Durable quota observations

QuotaObservationsDocument writes schema version 2 and reads structurally valid version 1 documents. A nonexistent quota-observations.json means an empty store. An existing document must contain an explicit non-null observations array; an empty array is valid. Missing/null observations, empty/truncated/malformed JSON, invalid records, and unsupported versions fail closed through the store's InvalidDataException contract, rather than becoming a valid empty store. Corrupt storage is not overwritten by an update attempt.

Records contain account ID, canonical model key, nullable remaining fraction, reset evidence, local observation time, and provenance; they contain no credential or session material. DurableQuotaObservationStore rereads under a path lock and cross-process lease before atomic writes. Newer unknown evidence replaces older known evidence; equal-time conflicts are conservative. Domain eligibility and duplicate-pool semantics are owned by [domain-rules.md](domain-rules.md#workload-model-and-candidate-evidence).

Production NativeAutoRouter and NativeAccountSwitchCoordinator share this store. Plans bind and reread the selected record through execution admission, so still-fresh persisted evidence works after restart without in-memory injection. InvalidateIfUnchangedAsync conditionally replaces disproven evidence with unknown without erasing a newer observation. Successfully verified target observations can be recorded after commit; a derived-evidence write failure does not undo committed metadata.

Evidence: QuotaObservationModels.cs, DurableQuotaObservationStore.cs, DurableQuotaObservationStoreTests, and DurableRoutingEvidenceTests.


## Locking model

- PathLockRegistry returns one in-process SemaphoreSlim per canonical full path.
- CrossProcessFileLease acquires an exclusive sibling .lock file for cross-process mutation. On Windows it uses delete-on-close behavior.
- LocalMetadataAccountStore and SessionVault take the path lock, acquire the cross-process lease for mutations, then re-read the latest durable snapshot before applying changes.
- AccountEnrollmentService adds a normalized-email semaphore; enrollment, deletion, switching, and router quota attribution share the vault-derived switch path lock and cross-process lease. Enrollment takes that lease once for its transaction rather than nesting it with a switch operation.
- AccountRemovalService rechecks active identity under that boundary, removes the vault record first, and restores it if guarded metadata removal fails. An unreadable metadata readback is an explicit manual-attention outcome, not proof of removal.
- NativeAutoRouter binds quota and source identity from one user-status response, compares it with persisted active metadata and a fresh live identity, then rechecks under switch ownership before attributing it. NativeAccountSwitchCoordinator rejects a mismatched source identity before process or credential mutation.
- NativeAccountSwitchCoordinator uses a process-wide SwitchGate and a cross-process switch lease derived from the vault path.
- A timed-out, cancellation-ignoring mutation remains inside its original lock and lease until it actually settles. The caller receives an explicit recovery-uncertain result, while a process-lifetime quarantine rejects later switch, account-lifecycle, and vault admission for that resource. Restart and manual reconciliation are required before reuse; a timeout is not evidence that an underlying write stopped.
- Vault operations recheck quarantine after acquiring the cross-process lease, before reading or mutating the envelope, and before a compensating write that follows a failed removal. An explicitly unproved vault removal/restoration or post-vault enrollment/removal relationship quarantines vault and account-lifecycle admission.
- NativeAutoRouter uses an evaluation gate to prevent overlapping cycles.

NativeAutoRouter evaluation admission and disposal share its state lock. Concurrent evaluation calls skip rather than queue. DisposeAsync closes admission and requests cancellation without waiting for evaluation completion or dependency cancellation callbacks. An admitted evaluation retains its gate until its finally block releases and disposes the gate if shutdown was requested. Its linked cancellation source is disposed after both evaluation cleanup and cancellation callbacks complete; callback faults are observed and reported through redacted diagnostics. Cancellation-ignoring dependencies therefore cannot make bounded application shutdown dispose a gate beneath its owner. EvaluateCycleAsync remains caller-owned: cancellation and unexpected failures propagate on that task; no detached evaluation task is introduced. NativeAutoRouterTests cover late completion, cooperative cancellation, concurrent admission, and polling shutdown ordering.

Maintain lock acquisition order. Introducing a new caller that acquires these resources in another order requires deadlock analysis and focused contention tests.

Startup journal reconciliation, quarantine resolution, and automatic switch-result publication release their acquired PathLock exactly once even if file-lease disposal fails. Cleanup failure blocks reuse through quarantine or router manual-recovery state. Recovery methods retain an existing domain failure classification and append cleanup guidance; otherwise they report a recovery/persistence failure. If resolution throws and cleanup also fails, both exceptions are retained in an AggregateException with the primary error first. Cancellation keeps its original exception and token, with secondary cleanup evidence in Exception.Data under LeaseCleanupFailure. Automatic publication retains primary ownership-check and cleanup details in its redacted manual-recovery reason. Internal lease-factory seams support synthetic disposal-failure tests without changing runtime configuration or lock order.

## Metadata compare-and-set operations

IAccountStore exposes guarded operations used by recovery paths:

- RemoveAccountIfUnchangedAsync removes only the expected metadata version.
- RestoreAccountIfUnchangedAsync restores the pre-enrollment metadata snapshot only when the written version still matches.
- CompareExchangeActiveAccountIdAsync changes the active selection only when the expected ID still owns it.
- TryFinalizeSwitchAsync updates the target and active selection only when the expected source remains active.

These operations prevent a compensation path from erasing a newer concurrent decision. A plain Add, Update, Remove, or SetActive call is not an equivalent substitute.

## Enrollment transaction

CURRENT IMPLEMENTATION in AccountEnrollmentService:

1. Verify connected telemetry and obtain the current account identity.
2. Read and validate the current WinCred entry.
3. Serialize enrollment for the normalized identity across in-process and cross-process callers.
4. Capture prior metadata, vault, and active-account state.
5. Create or update metadata, save the protected session, and conditionally select the account.
6. On failure, use RestoreIfCurrent, RemoveAccountIfUnchanged or RestoreAccountIfUnchanged, and active-account compare/exchange so compensation does not overwrite newer state.

The final live credential read must match the captured material byte-for-byte using a constant-time comparison and the captured username using ordinal comparison. The username is structurally validated (nonblank, at most 513 characters, and no embedded NUL), but is not compared with the authenticated email. If the credential rotated or its username changed during capture, enrollment refuses a successful current-session result and conditionally restores both the vault and the prior metadata snapshot (or removes a newly created record). Vault save re-reads the authoritative record after a writer exception: a proven committed record returns a receipt, a proven unchanged record fails normally, and an unreadable or conflicting outcome requires manual recovery. Post-vault metadata completion, readback, and conditional vault/metadata compensation have wall-clock caller deadlines; unreadable authority, a changed metadata snapshot, or unproved restoration is reported as manual recovery rather than an ordinary write failure. Account removal likewise bounds the caller's wait for metadata readback and vault restoration after vault deletion. A dependency that ignores cancellation retains the original lifecycle ownership until it settles, while quarantine prevents newer operations from racing a late write. Vault mutations have the same bounded-observer and quarantine rule around a non-cooperative durable writer. If active-selection compare/exchange throws after an enrollment commit, bounded authoritative readback distinguishes committed selection, preserved prior selection, and unknown outcome.

Cancellation after mutation begins does not justify abandoning compensation. Recovery uses bounded, explicit operations and must preserve external/concurrent changes.

Evidence: AccountEnrollmentService.cs, AccountEnrollmentServiceTests.cs, SessionVaultTests.cs, and AccountStoreTests.cs.

The Node/reference AccountEnrollmentService selects an initial identity, then acquires normalized-email ownership and the vault-derived `.enrollment` path lock/cross-process lease before authoritative capture. A changed post-ownership identity aborts. It copies and structurally validates the credential, brackets fresh credential reads with identity observations, and compares credential target, type, username, persistence, and exact bytes. Vault readback must also match the captured bytes. SessionVault.withCurrentReceipt holds the proved record unchanged through metadata finalization; acquisition order is normalized-email ownership, enrollment path lease, vault ownership, then metadata ownership. The finalization callback must not reacquire the held vault. LocalMetadataAccountStore.finalizeEnrollment conditionally commits the expected account and active selection together under metadata storage ownership, then repeats coherence proof after persistence before releasing ownership or reporting success. On failed proof it restores only a matching written snapshot; the service restores the vault by receipt and removes only an unchanged new placeholder. A newer active choice is preserved, and a conflicting or unreadable compensation outcome reports manual recovery rather than success. InMemoryAccountStore publishes synchronously after proof and metadata CAS. Unsupported stores fail closed. A vault writer exception after replacement requires readback to establish a receipt or an uncertain outcome. These are repeated-observation guarantees: the external account and credential providers do not expose an atomic joint snapshot or honor repository locks. No persistence follows the successful durable final proof. Evidence: src/accounts/enrollment.ts, src/accounts/account-store.ts, src/vault/session-vault.ts, test/account-enrollment.test.ts, and test/account-enrollment-coherence.test.ts.

## Switch transaction

CURRENT IMPLEMENTATION in NativeAccountSwitchCoordinator:

1. Acquire non-overlap gates and validate the target, vaulted session, current identity, activity, and process provenance.
2. Capture rollback state and durably record the transaction before process transition; refresh the rollback credential around source-process quiescence.
3. Record mutation milestones, apply the target credential, restart the verified process generation, and verify target identity.
4. For an automatic request, obtain fresh live target quota and verify the configured model and minimum quota before metadata finalization. Manual requests do not require this automatic-routing configuration.
5. Record TARGET_IDENTITY_VERIFIED_PRECOMMIT and finalize metadata with a guarded active-account transition; clean up the completed journal.
6. On failed live quota verification, conditionally invalidate unchanged cached target evidence. On post-mutation failure, quiesce the owned replacement generation, restore the credential only if current state still matches the applied credential, restart, and verify the source identity.
7. Return explicit verified-rollback or manual-recovery results. Unproved rollback, evidence invalidation, or journal cleanup cannot be hidden as safe completion.

Credential capture accepts structurally valid legacy usernames without treating them as account identity. A successful forward write uses the canonical WinCred username `antigravity`. Compensation restores the exact captured original credential, including a legacy username, after the existing conditional-restore check; there is no startup or background migration.

Manual and automatic requests share switch ownership. Automatic plans revalidate both router epoch and persisted active identity under that ownership before mutation; manual completion reacquires that ownership and publishes success only if its target is still the authoritative active identity. Router quota observations are attributed only after rechecking epoch, active identity, and live identity inside that ownership boundary. Cooldown-probe and gate transitions revalidate their originating epoch under router state ownership, so an older evaluation cannot clear a newer cooldown or manual-recovery decision. Successful automatic publication likewise reacquires switch ownership before updating router state under its state lock. A delayed automatic rollback failure remains a terminal manual-recovery outcome even after a newer manual success; the operator must explicitly clear it. Newer manual success and manual-recovery terminal states cannot be overwritten by an earlier automatic success.

The coordinator admits a manual or automatic switch only when its journal recovery state is `NONE` and neither switch-resource quarantine nor vault quarantine is active. It checks this before starting the switch task and again while holding switch ownership. In particular, a failed cleanup of a proven-clean `RECORDED` or committed `PRECOMMIT` journal can leave `ACTION_REQUIRED` without quarantine; that state still blocks backend switch admission. The router also reads the coordinator recovery state before evaluation and enters manual-recovery mode for any non-`NONE` state or independent quarantine, avoiding repeated automatic attempts. `RESTART_REQUIRED` remains terminal for the process lifetime.

CanAdmitSwitch remains authoritative. Successful completion, verified rollback, or a safe pre-mutation failure permits reuse of the same coordinator only after active ownership is released and bounded journal readback proves absence, recovery is NONE, and quarantine is absent. The live transaction state then returns to IDLE while LastResult retains the terminal outcome. Active transactions and cleanup still exclude concurrent switches. A retained journal yields ACTION_REQUIRED, corrupt/unsupported storage yields NOT_RESOLVABLE, and uncertain readback yields UNKNOWN; no terminal-state reset bypasses those gates. Evidence: SwitchCoordinatorLifecycleTests and TargetQuotaVerificationSwitchTests.

The coordinator rechecks the live source and active metadata after target-vault loading and again after process revalidation, immediately before stopping the verified source generation. That final boundary also re-observes activity: BUSY, UNKNOWN, or failed activity proof prevents the stop and credential mutation. Rollback quiescence is permitted only once the verified process stop has actually been attempted; a pre-stop proof failure must not stop the still-running original. A cleanup fault cannot hide an established rollback-failure result; an unclassified coordinator failure is treated as recovery-uncertain by automatic and explicit switch callers. An outer transaction deadline bounds the caller without detaching the inner transaction from its ownership; unresolved mutations quarantine subsequent admission.

NativeAutoRouter supplies owned automatic interruption admission to NativeAccountSwitchCoordinator. The dedicated semaphore serializes UpdateConfig (including persistence and runtime publication) against the final automatic currentness proof through the actual source kill request. A save completed before admission invalidates a changed immutable configuration snapshot; a save arriving after admission cannot persist, publish, or complete before interruption is issued. Every RouterConfigDto field remains part of snapshot equality; identical saves remain valid but share the same admission ordering. Acquisition order is switch gate, switch-resource path lock/cross-process lease, interruption admission, then short router state access. Config saves acquire interruption admission before router state and never acquire switch ownership. Quota evidence readback stays outside the router state lock. WindowsAG2ProcessLifecycle invokes onStopIssued only after Kill returns, releasing admission before the asynchronous process-exit wait; stop/proof failures release it in finally. Cancellation while waiting for automatic admission remains pre-mutation cancellation. Manual switches do not acquire this configuration guard. Evidence: RoutingConfigurationAdmissionTests and RoutingInterruptionAdmissionTests.

The order is security- and integrity-sensitive. See NativeAccountSwitchCoordinatorTests and WindowsAG2ProcessLifecycleTests before changing it.

## Polling and stale state

TelemetryPollingCoordinator starts from the persisted router polling interval, prevents overlapping PollAsync calls, assigns monotonic sequence numbers, and refuses to replace newer status with an older result. When Antigravity is connected, it derives account identity and quota telemetry from a single unified observation (`IAG2Adapter.GetAccountQuotaObservationAsync`) to ensure published dashboard snapshots never mix telemetry across accounts. A successful config write dynamically replaces its timer. Each poll invokes NativeAutoRouter.EvaluateCycleAsync before building the published snapshot. A failed durable config write does not report success or change runtime config.

To prevent concurrent configuration saves from leaving TelemetryPollingCoordinator running an older polling interval than the final committed configuration, NativeAutoRouter maintains a monotonic `ConfigGeneration` incremented under interruption admission and state lock on each successful configuration write. LoopbackServer's `POST /api/config` endpoint invokes `UpdateConfigWithGeneration` and passes the resulting generation to `onPollingIntervalChangedWithGeneration` or `onPollingIntervalChangedAsync`. TelemetryPollingCoordinator tracks `LastAppliedConfigGeneration` under its state lock; `UpdateInterval(TimeSpan newInterval, long generation = 0)` accepts the update only if `generation >= _lastAppliedConfigGeneration` (or when `generation == 0` for direct unsequenced calls), rejecting stale updates from older configuration writes that settle out of order. Evidence: `TelemetryPollingCoordinatorTests`, `TelemetryPollingCoordinatorGenerationTests`, and `LoopbackConfigConcurrencyTests`.

### Dashboard configuration form

App.svelte refreshes status, accounts, configuration, switching status, and candidate evidence approximately every six seconds. Each resource has independent request sequencing, and reads that cross a mutation are discarded. Failed ordinary reads retain their last good snapshot; failed switching-status reads revoke safety authority and fail closed, while failed candidate-evidence reads show unavailable evidence instead of retaining a healthy claim. RoutingConfigSection.svelte protects dirty edits from incoming config refreshes until save or reset, including edits made while a save is in flight. Its complete save payload preserves WorkloadModelKey across unrelated edits.

This is client-side UI concurrency/UX behavior. It does not demonstrate persistent-file corruption or a failed server-side atomic write.

## Interrupted switch consistency

Active transaction status and LastResult remain process-local, but SwitchJournalStore persists a versioned transaction record without credential material. App.xaml.cs calls ReconcileStartupJournalAsync before routing, polling, and dashboard service startup. This detects interrupted work; WinCred and metadata still are not one atomic cross-store transaction.

- No journal permits clean startup. A proven pre-mutation RECORDED journal or TARGET_IDENTITY_VERIFIED_PRECOMMIT whose target metadata is already active can be conditionally cleaned up.
- Unresolved CREDENTIAL_APPLYING, ROLLING_BACK, QUARANTINED, or uncommitted precommit records require ACTION_REQUIRED and quarantine. Failed cleanup of a proven-clean record also requires ACTION_REQUIRED.
- Corrupt or unsupported journals yield NOT_RESOLVABLE and quarantine. I/O, ownership, or other uncertain reads yield UNKNOWN. Startup does not blindly roll back credentials or finalize uncertain metadata.
- Every non-NONE recovery state (ACTION_REQUIRED, NOT_RESOLVABLE, UNKNOWN, RESTART_REQUIRED) blocks manual and automatic switching. Independent quarantine and active transactions also block admission.

The dashboard exposes recovery status and an explicitly confirmed, token-protected resolve action. Clean journal cleanup is conditional. Resolution of unresolved state requires repeated current coherence proof across metadata, vault, live identity, and credential, then conditional journal deletion that preserves concurrent changes. Successful proof-based resolution returns ResolvedRestartRequired; it does not authorize switching in the same process. Failed proof, unsupported data, uncertainty, or changed authority remains blocking. NoJournal alone does not prove quarantine cleared, and the UI must obtain authoritative current status before permitting actions. Wire behavior is owned by [api-contracts.md](api-contracts.md).

Evidence: SwitchJournalStoreTests, NativeAccountSwitchCoordinatorJournalTests, NativeAccountSwitchCoordinatorResolutionTests, SwitchCoordinatorLifecycleTests, LoopbackSwitchApiTests, and test/frontend-quarantine.test.ts. The original design proposal is retained as historical rationale in [ADR-001](decisions/ADR-001-switch-transaction-journal.md).

## Switch request cancellation

CURRENT IMPLEMENTATION:

- Cancellation before process transition begins returns the CANCELLED result, which the loopback server maps to 408.
- Once process transition begins, caller cancellation is detached. Forward completion and rollback have internally owned bounded cancellation lifetimes.
- Shutdown closes switch admission before waiting for the active transaction. It signals the internal forward path, waits within a caller-specified bound, and reports timeout rather than silently exiting while recovery remains active.

This is transaction/control-flow behavior, not evidence of file corruption. HTTP mappings are summarized in [api-contracts.md](api-contracts.md).

## WebView2 lifecycle

DashboardLifecycleManager owns lazy window creation and reuse. WebView2EnvironmentCoordinator serializes environment creation, keeps closing environments from being handed to new windows, waits for browser-process exit when possible, and retries known lock contention with backoff.

MainWindow funnels navigation, initialization, and process failures through a shared bounded recovery budget. Environment creation and control initialization each have wall-clock deadlines even when a callee ignores cancellation. Event handlers are detached when a WebView is replaced, and generation checks prevent callbacks or delayed recovery work from an old control from changing the current recovery state. Automatic reload/recreation stops after exhaustion and exposes a stable manual retry overlay; successful current-generation navigation resets the budget.

Tests cover concurrent creation, reopen during exit, timeout, lock contention, and late exit events. They do not prove behavior for every installed WebView2 runtime version.

## Change checklist

For any persistent or concurrent mutation, identify:

- the authoritative store and schema;
- in-process and cross-process ownership;
- the re-read/rebase point;
- the commit point;
- cancellation behavior before and after mutation;
- compensation preconditions;
- how newer external state is preserved;
- fault-injection and contention tests.
