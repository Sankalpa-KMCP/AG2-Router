# Persistence and concurrency

This document owns durable stores, mutation ownership, synchronization, transaction compensation, and lifecycle sequencing. Domain outcomes are in [domain-rules.md](domain-rules.md); trust assumptions are in [security-and-trust-model.md](security-and-trust-model.md).

## State inventory

| State | Current owner and location | Durability |
| --- | --- | --- |
| Account metadata and active account ID | LocalMetadataAccountStore; DATA_DIR/accounts.json when overridden, otherwise per-user LocalAppData/AG2-Router/data/accounts.json | Durable JSON |
| Vaulted sessions | SessionVault; per-user LocalAppData/AG2-Router/vault/sessions.dat by default | Durable versioned JSON envelope containing DPAPI ciphertext |
| Live Antigravity credential | Windows Credential Manager canonical target | External OS-managed state |
| Router configuration | NativeAutoRouter config JSON under per-user app data | Durable; loaded before polling starts |
| Observed account quotas and cooldowns | NativeAutoRouter | Process memory only |
| Poll/status snapshot | TelemetryPollingCoordinator | Process memory only |
| Switch status and last result | NativeAccountSwitchCoordinator | Process memory only |
| WebView2 browser profile | WebView2EnvironmentCoordinator under per-user LocalAppData/AG2-Router/webview2 | WebView2-managed persistent state |
| Autostart preference | WindowsRegistryAutostartService | Per-user registry state |

Paths above describe application contracts, not permission to inspect live user data. Tests must redirect storage to task-owned temporary locations.

## File durability

DurableFileWriter implements the shared write primitive for account metadata and vault state:

1. Resolve the full destination and create its directory.
2. Write a unique temporary sibling using asynchronous write-through.
3. Flush writer and stream to disk.
4. Replace an existing destination or move the temporary file into place.
5. Best-effort remove a temporary file when replacement did not complete.

Readers treat malformed or unsupported persisted data as an error. SessionVault in particular fails closed on empty, truncated, wrong-magic, wrong-version, or identity-mismatched content rather than overwriting it with an empty vault.

Atomic replacement protects the destination snapshot; it does not by itself serialize multiple read-modify-write actors. Locks and rebasing are separate requirements.

## Locking model

- PathLockRegistry returns one in-process SemaphoreSlim per canonical full path.
- CrossProcessFileLease acquires an exclusive sibling .lock file for cross-process mutation. On Windows it uses delete-on-close behavior.
- LocalMetadataAccountStore and SessionVault take the path lock, acquire the cross-process lease for mutations, then re-read the latest durable snapshot before applying changes.
- AccountEnrollmentService adds a normalized-email semaphore; enrollment, deletion, switching, and router quota attribution share the vault-derived switch path lock and cross-process lease. Enrollment takes that lease once for its transaction rather than nesting it with a switch operation.
- AccountRemovalService rechecks active identity under that boundary, removes the vault record first, and restores it if guarded metadata removal fails. An unreadable metadata readback is an explicit manual-attention outcome, not proof of removal.
- NativeAutoRouter binds quota and source identity from one user-status response, compares it with persisted active metadata and a fresh live identity, then rechecks under switch ownership before attributing it. NativeAccountSwitchCoordinator rejects a mismatched source identity before process or credential mutation.
- NativeAccountSwitchCoordinator uses a process-wide SwitchGate and a cross-process switch lease derived from the vault path.
- A timed-out, cancellation-ignoring mutation remains inside its original lock and lease until it actually settles. The caller receives an explicit recovery-uncertain result, while a process-lifetime quarantine rejects later switch, account-lifecycle, and vault admission for that resource. Restart and manual reconciliation are required before reuse; a timeout is not evidence that an underlying write stopped.
- NativeAutoRouter uses an evaluation gate to prevent overlapping cycles.

Maintain lock acquisition order. Introducing a new caller that acquires these resources in another order requires deadlock analysis and focused contention tests.

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

The final live credential read must match the captured material as well as the username. If the same identity's credential rotated during capture, enrollment refuses a successful current-session result and conditionally restores both the vault and the prior metadata snapshot (or removes a newly created record). Vault save re-reads the authoritative record after a writer exception: a proven committed record returns a receipt, a proven unchanged record fails normally, and an unreadable or conflicting outcome requires manual recovery. Post-vault metadata completion, readback, and conditional vault/metadata compensation have wall-clock caller deadlines; unreadable authority, a changed metadata snapshot, or unproved restoration is reported as manual recovery rather than an ordinary write failure. Account removal likewise bounds the caller's wait for metadata readback and vault restoration after vault deletion. A dependency that ignores cancellation retains the original lifecycle ownership until it settles, while quarantine prevents newer operations from racing a late write. Vault mutations have the same bounded-observer and quarantine rule around a non-cooperative durable writer. If active-selection compare/exchange throws after an enrollment commit, bounded authoritative readback distinguishes committed selection, preserved prior selection, and unknown outcome.

Cancellation after mutation begins does not justify abandoning compensation. Recovery uses bounded, explicit operations and must preserve external/concurrent changes.

Evidence: AccountEnrollmentService.cs, AccountEnrollmentServiceTests.cs, SessionVaultTests.cs, and AccountStoreTests.cs.

## Switch transaction

CURRENT IMPLEMENTATION in NativeAccountSwitchCoordinator:

1. Acquire non-overlap gates and validate the target, vaulted session, current identity, activity, and process provenance.
2. Capture and then refresh the rollback credential around source-process quiescence.
3. Apply the target credential, restart the verified process generation, and verify target identity.
4. Finalize metadata with a guarded active-account transition.
5. On post-mutation failure, quiesce the owned replacement generation, restore the credential only if current state still matches the applied credential, restart, and verify the source identity.
6. Return explicit rollback or manual-recovery results when restoration cannot be proven.

Manual and automatic requests share switch ownership. Automatic plans revalidate both router epoch and persisted active identity under that ownership before mutation; manual completion reacquires that ownership and publishes success only if its target is still the authoritative active identity. Router quota observations are attributed only after rechecking epoch, active identity, and live identity inside that ownership boundary. Cooldown-probe and gate transitions revalidate their originating epoch under router state ownership, so an older evaluation cannot clear a newer cooldown or manual-recovery decision. Successful automatic publication likewise reacquires switch ownership before updating router state under its state lock. A delayed automatic rollback failure remains a terminal manual-recovery outcome even after a newer manual success; the operator must explicitly clear it. Newer manual success and manual-recovery terminal states cannot be overwritten by an earlier automatic success.

The coordinator rechecks the live source and active metadata after target-vault loading and again after process revalidation, immediately before stopping the verified source generation. That final boundary also re-observes activity: BUSY, UNKNOWN, or failed activity proof prevents the stop and credential mutation. A cleanup fault cannot hide an established rollback-failure result; an unclassified coordinator failure is treated as recovery-uncertain by automatic and explicit switch callers. An outer transaction deadline bounds the caller without detaching the inner transaction from its ownership; unresolved mutations quarantine subsequent admission.

The order is security- and integrity-sensitive. See NativeAccountSwitchCoordinatorTests and WindowsAG2ProcessLifecycleTests before changing it.

## Polling and stale state

TelemetryPollingCoordinator starts from the persisted router polling interval, prevents overlapping PollAsync calls, assigns monotonic sequence numbers, and refuses to replace newer status with an older result. A successful config write dynamically replaces its timer. Each poll invokes NativeAutoRouter.EvaluateCycleAsync before building the published snapshot. A failed durable config write does not report success or change runtime config.

### Dashboard configuration form

App.svelte refreshes status, accounts, and configuration approximately every six seconds. Each resource has independent request sequencing, and reads that cross a mutation are discarded; failed reads retain the last good snapshot. RoutingConfigSection.svelte protects dirty edits from incoming config refreshes until save or reset, including edits made while a save is in flight.

This is client-side UI concurrency/UX behavior. It does not demonstrate persistent-file corruption or a failed server-side atomic write.

## Interrupted switch consistency

Switch transaction state, including the active transaction and last result, exists only in NativeAccountSwitchCoordinator memory. Applying the WinCred target and finalizing account metadata are separate durable operations. Abrupt process termination between those operations can leave live credential state and account metadata out of sync.

Startup composition currently has no durable switch transaction journal or reconciliation step. Atomic account-metadata replacement protects that file's snapshot; it does not make WinCred plus metadata one atomic cross-store transaction. Repository evidence does not establish automatic or manual recovery behavior for this abrupt-termination case.

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
