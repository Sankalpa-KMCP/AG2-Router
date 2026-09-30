# ADR-001: Durable Switch Transaction Journal and Startup Reconciliation

Status: Proposed
Date: 2026-09-27

Historical design proposal: the context and proposed API names below describe the pre-journal implementation. The journal and startup reconciliation are now implemented; current behavior is owned by [persistence-and-concurrency.md](../persistence-and-concurrency.md#interrupted-switch-consistency) and [runtime-architecture.md](../runtime-architecture.md). This record preserves original rationale and proposal metadata, not a current operational gap or release-verification claim.

## Context

In AG2 Router, switching active accounts is a multi-stage transaction coordinating Windows Credential Manager (`gemini:antigravity`), running Antigravity process generations, live identity verification, and persistent account metadata (`accounts.json`).

Currently, the in-flight transaction lifecycle and recovery quarantine state exist only in process memory within `NativeAccountSwitchCoordinator` (`dotnet/src/AG2Router.AG2/Switching/NativeAccountSwitchCoordinator.cs`). Applying the target credential to WinCred and committing active-account metadata in `LocalMetadataAccountStore` are separate durable operations.

As documented in `docs/persistence-and-concurrency.md` ("Interrupted switch consistency") and `docs/known-limitations.md` ("Switch transaction continuity"), an abrupt process termination (crash, power loss, or forced kill) between WinCred write and metadata finalization leaves durable cross-store state out of sync. Furthermore, upon router restart, there is currently no durable record of the interrupted transaction, and any previously active `RecoveryQuarantine` enforcement is lost because quarantine state is memory-only.

This record establishes the authoritative architecture for a dedicated, durable switch transaction journal and deterministic startup reconciliation.

---

## Current continuity failure mode

A switch transaction follows this sequential progression:
1. Preflight validation & snapshot capture (process state and original rollback credential).
2. Quiescence of verified source process.
3. Target credential write to WinCred.
4. Launch and verification of replacement process generation.
5. Live target identity proof via adapter RPC (`VerifyIdentityAsync`).
6. Metadata commit (`LocalMetadataAccountStore.TryFinalizeSwitchAsync`).

### The Crash Window
Between Step 3 (WinCred write) and Step 6 (Metadata commit):
- WinCred holds the target account's credential payload.
- `accounts.json` still records the source account as `activeAccountId`.
- If `AG2Router.App` abruptly terminates in this window (or during rollback):
  - In-memory coordinator transaction state is destroyed.
  - On the next launch of AG2 Router, the system observes `accounts.json` pointing to the source account, but the underlying Windows credential or running process reflects the target account.
  - In-memory `RecoveryQuarantine` is lost upon restart, allowing new switch transactions or automatic routing evaluations to proceed against inconsistent state.
  - Recovery today requires manual operator inspection and remediation.

---

## Existing invariants that constrain the design

Any journal and reconciliation mechanism must adhere to the core domain and persistence rules defined across the repository:
1. **Source and tests are executable truth**: Documented rules must align with runtime primitives (`docs/domain-rules.md`, `docs/persistence-and-concurrency.md`).
2. **Account identity proof is live-only**: An account switch is successful only when a verified running Antigravity process reports the target identity via live telemetry RPC; metadata and persisted credentials alone are never proof of live identity (`docs/domain-rules.md`, "Account identity").
3. **No cross-store atomic transactions**: Windows Credential Manager and filesystem metadata cannot be updated in a single hardware transaction. Atomicity across stores must be achieved via ordered intent journaling, verification milestones, and fail-closed reconciliation.
4. **Single-writer serialization**: Live switch operations remain strictly serialized under `SwitchGate`, `PathLockRegistry`, and cross-process file leases (`.switch`).
5. **Fail-closed quarantine**: When the outcome of an external state mutation cannot be proven, the system must quarantine lifecycle operations to prevent conflicting mutations until authoritative reconciliation succeeds.

---

## Decision

Introduce a durable, single-owner transaction journal managed exclusively by `NativeAccountSwitchCoordinator`. The journal records transaction intent prior to external state mutations and milestone verification prior to metadata commitment.

On application startup, before any automatic routing, telemetry polling, loopback server, or tray surfaces begin, `NativeAccountSwitchCoordinator.ReconcileJournalAsync()` inspects any retained journal, classifies the state fail-closed, and enforces lifecycle quarantine if an unresolved mutation is detected.

---

## Journal ownership and storage

### Single Owner
`NativeAccountSwitchCoordinator` is the sole owner of:
- Journal creation;
- Journal state transitions;
- Terminal journal deletion or retention;
- Startup reconciliation classification.

The application composition root (`dotnet/src/AG2Router.App/App.xaml.cs`) invokes reconciliation during startup initialization but does not interpret or parse journal states directly. `NativeAutoRouter`, `AccountEnrollmentService`, and `AccountRemovalService` do not read or write the journal.

### Storage Location and Path Resolution
The journal is stored as a dedicated file in the application's local user data directory:
- **Concrete Path Resolution**: `LocalMetadataAccountStore` (`dotnet/src/AG2Router.AG2/Accounts/LocalMetadataAccountStore.cs`) owns and resolves the authoritative accounts metadata file path via its concrete `GetFilePath()` method and `ResolveDefaultFilePath(string? dataDirectory, string? currentDirectory, string? localApplicationData)` static resolver.
- **Environment Policy**: Path resolution explicitly honors the `DATA_DIR` environment variable when configured. `%LOCALAPPDATA%\AG2-Router\data` is only the default fallback root when `DATA_DIR` is unset, not a universal hardcoded root.
- **Sibling Relationship**: The switch journal path is derived as the sibling `switch-journal.json` under the parent directory of the concrete resolved metadata store path:
  ```csharp
  string journalPath = Path.Combine(Path.GetDirectoryName(metadataStore.GetFilePath())!, "switch-journal.json");
  ```
- **Composition Wiring**: The application composition root (`App.xaml.cs`) resolves this sibling path from the instantiated `LocalMetadataAccountStore` and provides the resulting path or journal store to `NativeAccountSwitchCoordinator`. The coordinator must not independently duplicate environment variable or data-root resolution policy.
- **Prohibited Locations**: The journal must never be stored in Windows Credential Manager, embedded inside `SessionVault`, placed in the Windows Registry, or emitted to production logs.

---

## Schema v1

The journal file contains a single JSON object. It adheres to strict minimality and zero-secret invariants.

```json
{
  "magic": "AG2SWITCHJRNL",
  "schemaVersion": 1,
  "transactionId": "b1a2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d",
  "state": "CREDENTIAL_APPLYING",
  "updatedAt": "2026-09-27T12:00:00.0000000Z",
  "sourceAccountId": "acc-source-uuid",
  "targetAccountId": "acc-target-uuid",
  "quarantineReasonCode": null
}
```

### Field Definitions
- `magic` (`string`): Fixed string `"AG2SWITCHJRNL"` to guarantee file identity and detect corruption.
- `schemaVersion` (`int`): Format version, fixed to `1`. Unknown versions fail closed to quarantine.
- `transactionId` (`string`): Unique UUID identifying the switch operation.
- `state` (`string`): Single authoritative state enum value.
- `updatedAt` (`string`): ISO-8601 UTC timestamp of the last durable state update.
- `sourceAccountId` (`string`): Account ID expected to be active prior to the switch. Acts as the canonical `expectedActiveAccountId` for CAS evaluation.
- `targetAccountId` (`string`): Account ID intended to become active.
- `quarantineReasonCode` (`string?`): Optional machine-readable reason code populated when transitioned to or classified as `QUARANTINED`.

### Account ID vs. Email
The journal records `sourceAccountId` and `targetAccountId` exclusively. It does not record account emails. Under `LocalMetadataAccountStore`, atomic finalization (`TryFinalizeSwitchAsync`) operates on stable account IDs. Account IDs uniquely and immutably identify metadata records; storing emails in the journal is redundant and would create synchronization drift if aliases or display names change.

---

## Authoritative durable states and transition table

The journal avoids multi-variable flags or split phase/terminal states. It uses exactly one authoritative state enum:

| State Enum | Meaning | Mutation Boundary |
| :--- | :--- | :--- |
| `RECORDED` | Preflight passed; switch transaction intent captured. | Written before source process stop or any credential change. |
| `CREDENTIAL_APPLYING` | WinCred target mutation is imminent or in progress. | Written durably before calling `WinCredWriter.WriteCredentialAsync`. |
| `TARGET_IDENTITY_VERIFIED_PRECOMMIT` | Target credential applied, process restarted, and live target identity verified via RPC. | Written durably only after `VerifyIdentityAsync(target)` succeeds; before calling `TryFinalizeSwitchAsync`. |
| `ROLLING_BACK` | Forward mutation failed; credential compensation/rollback is imminent or in progress. | Written durably before calling `QuiesceForRollbackAsync` or restoring credentials. |
| `QUARANTINED` | Terminal unresolved state requiring authoritative operator reconciliation. | Retained on disk; re-marks lifecycle `.switch` `RecoveryQuarantine` and `SessionVault` quarantine on startup. |

### Terminal Success Representation
A successfully completed switch transaction **deletes** the journal file. Clean state is defined by the absence of `switch-journal.json`. No `SUCCESS` record is retained on disk.

---

## Pre/post write ordering

Journal updates must strictly precede the external actions they protect:

```
[Preflight Passed]
       │
       ▼
1. Write Journal: RECORDED
       │
       ▼
[Stop Source Process]
       │
       ▼
2. Write Journal: CREDENTIAL_APPLYING
       │
       ▼
[Write Target Credential to WinCred]
       │
       ▼
[Launch & Verify Replacement Process]
       │
       ▼
[Verify Live Target Identity via RPC]
       │
       ▼
3. Write Journal: TARGET_IDENTITY_VERIFIED_PRECOMMIT
       │
       ▼
[Commit Metadata via TryFinalizeSwitchAsync]
       │
       ▼
4. Delete Journal File (Clean Terminal State)
```

If a failure occurs after Step 2 (`CREDENTIAL_APPLYING`):
```
[Failure Detected]
       │
       ▼
1. Write Journal: ROLLING_BACK (must complete durably before rollback actions)
       │
       ▼
[Quiesce Replacement Process via QuiesceForRollbackAsync]
       │
       ▼
[Execute Credential Restoration & Verification]
       │
       ├─► [Rollback Succeeded & Verified] ──► Delete Journal File
       │
       └─► [Rollback Failed / Uncertain]    ──► Transition/Retain Journal: QUARANTINED
```

- **Intent ordering**: An intent state (`CREDENTIAL_APPLYING`, `ROLLING_BACK`) must be fully flushed to disk before the corresponding external mutation is invoked.
- **Rollback Intent Order**: The `ROLLING_BACK` journal transition must complete durably before `_processLifecycle.QuiesceForRollbackAsync` or any other rollback external action begins. If persisting `ROLLING_BACK` fails:
  - Rollback external actions must not proceed under that new state transition;
  - Because forward mutation may already have occurred, the operation remains fail-closed and quarantined;
  - The earlier conservative on-disk state (`CREDENTIAL_APPLYING`) remains preserved on disk, ensuring the next startup safely quarantines.
- **Proof ordering**: A milestone state (`TARGET_IDENTITY_VERIFIED_PRECOMMIT`) is written only after the milestone is proven by live telemetry.

---

## Startup reconciliation matrix

When `NativeAccountSwitchCoordinator.ReconcileJournalAsync()` executes during application startup, it evaluates the journal against current persistent metadata (`accounts.json`):

| Journal State Found | Current Metadata State | Startup Reconciliation Action | Rationale |
| :--- | :--- | :--- | :--- |
| **No journal file** | Any | None. Clean startup. | Normal state; no interrupted transaction. |
| `RECORDED` | Any | **Delete journal**. Clean startup. | No durable credential or metadata mutation began before crash. |
| `CREDENTIAL_APPLYING` | Any | **Mark quarantine**. Retain journal. | WinCred write may have completed, partially completed, or failed. State is uncertain. |
| `TARGET_IDENTITY_VERIFIED_PRECOMMIT` | `activeAccountId == targetAccountId` | **Delete journal**. Clean startup. | Metadata commit succeeded before crash; journal deletion was delayed. Transaction is complete. |
| `TARGET_IDENTITY_VERIFIED_PRECOMMIT` | `activeAccountId == sourceAccountId` | **Mark quarantine**. Retain journal. | **DO NOT call TryFinalizeSwitchAsync**. Crash occurred before metadata write. Stale pre-crash identity verification is not live identity proof. |
| `TARGET_IDENTITY_VERIFIED_PRECOMMIT` | `activeAccountId != targetAccountId` (other) | **Mark quarantine**. Retain journal. | Newer external metadata modification detected; preserve newer state fail-closed. |
| `ROLLING_BACK` | Any | **Mark quarantine**. Retain journal. | Rollback completion is unprovable across crash; WinCred/process state is uncertain. |
| `QUARANTINED` | Any | **Mark quarantine**. Retain journal. | Re-establish persistent quarantine enforcement. |
| **Malformed / Corrupt / Unknown Version** | Any | **Mark quarantine**. Retain raw journal. | Fail-closed on parsing or schema failure. Preserve original file bytes byte-for-byte for diagnostics; do not overwrite. |

---

## Live-identity rule

Under no circumstances may startup reconciliation treat a journal entry or persistent metadata as proof of current live identity:
- `TARGET_IDENTITY_VERIFIED_PRECOMMIT` records that the target identity was verified in a process generation that existed *before the crash*.
- That process generation is dead or unverified across restart.
- Persisted metadata in `accounts.json` reflects stored intent, not verified execution.
- Therefore, startup reconciliation **must never execute an automatic `TryFinalizeSwitchAsync`** to advance metadata to the target based on pre-crash journal records.
- Any future recovery operation requiring identity proof must verify fresh live telemetry from a currently running process.

---

## Quarantine integration and lifecycle enforcement

The journal does not replace `RecoveryQuarantine`. It provides the durable backing store that makes `RecoveryQuarantine` persistent across process lifetimes:

### Parseable vs. Corrupt Journal Handling
1. **Parseable Unresolved Journal**:
   - For parseable records in unresolved states (`CREDENTIAL_APPLYING`, uncommitted `TARGET_IDENTITY_VERIFIED_PRECOMMIT`, `ROLLING_BACK`, or `QUARANTINED`), the journal state is transitioned to or retained as `QUARANTINED` with an appropriate `quarantineReasonCode`.
2. **Malformed, Truncated, or Unknown-Schema Journal**:
   - The original file artifact is preserved byte-for-byte on disk.
   - The coordinator must **NOT** overwrite corrupt or unknown bytes to encode a synthetic `QUARANTINED` record.
   - The file is retained intact as diagnostic/forensic evidence.
   - In-memory fail-closed quarantine is marked independently.

### Quarantine Keying Without Payload Parsing
When the journal file is malformed, truncated, or has an unknown schema, quarantine cannot depend on parsing `sourceAccountId` or `targetAccountId` from the corrupted payload. Instead, quarantine is keyed using the known lifecycle resource path derivable directly from composition:
- `sessionVault.GetVaultPath() + ".switch"` (and the known journal path), which deterministically governs the switch lifecycle resource.

### Concrete Enforcement Across All Lifecycle Surfaces
Startup reconciliation must activate existing enforcement mechanisms to block all three account lifecycle surfaces:
1. **Switch Operations**: Blocked by marking lifecycle `.switch` `RecoveryQuarantine` (`RecoveryQuarantineRegistry.Get(sessionVault.GetVaultPath() + ".switch").Mark()`), which causes `NativeAccountSwitchCoordinator` to reject subsequent switch requests fail-closed.
2. **Enrollment Operations**: Blocked by marking `SessionVault` quarantine (`sessionVault.QuarantineUnresolvedMutation()`), which sets `_sessionVault.IsQuarantined` and marks the `.switch` resource, causing `AccountEnrollmentService` to reject enrollment requests fail-closed.
3. **Removal Operations**: Blocked by `AccountRemovalService`, which explicitly checks both the lifecycle `.switch` `RecoveryQuarantine` and `sessionVault.IsQuarantined`.

By invoking `sessionVault.QuarantineUnresolvedMutation()` (which marks both `_vaultFilePath` and `_vaultFilePath + ".switch"`), startup reconciliation activates both existing quarantine mechanisms without inventing a third.

---

## Operator resolution contract

### Current Repository Reality
- **No Existing In-Product Recovery Tooling**: No supported automated or in-product switch-journal recovery mechanism exists in the current repository.
- **Restart Is Insufficient**: Restart alone **MUST NOT** clear an unresolved retained journal. Startup reconciliation re-marks quarantine on every launch as long as an unresolved journal remains.
- **Manual Deletion Prohibited**: Deleting `switch-journal.json` by itself is **NOT** evidence that the account state is coherent and is not a normal or supported recovery procedure. Deleting the journal merely destroys crash evidence while leaving WinCred and metadata potentially split.
- **Fail-Closed Persistence**: Unresolved states remain strictly fail-closed until authoritative reconciliation succeeds.

### Frozen Minimum Safe Resolution Contract
A future supported, operator-triggered resolution path must satisfy the following safety contract under the established lifecycle ownership and locking discipline (`SwitchGate -> PathLockRegistry lock -> CrossProcessFileLease`):
1. **Identification**: The retained journal is identified as unresolved or quarantined.
2. **Authoritative Metadata**: Current persistent metadata (`accounts.json`) is read authoritatively.
3. **Fresh Live Identity**: Current live Antigravity identity is freshly obtained through the normal live-identity verification mechanism (`IAG2Adapter.GetCurrentAccountAsync` / `VerifyIdentityAsync`).
4. **Coherence Verification**: Persisted metadata and fresh live identity are proven to agree on the same enrolled account.
5. **Credential State Verification**: Where current repository primitives permit it, current WinCred/session state is verified against the enrolled/vaulted state without persisting secrets in the journal.
   *(Note: Current repository primitives support verifying vaulted session presence via `ISessionVault.HasSessionAsync` and reading WinCred target presence; full secret-payload comparison against DPAPI-encrypted sessions without exposing credentials must respect zero-secret boundaries).*
6. **No Overwriting Newer State**: No newer external metadata state is overwritten.
7. **Fail-Closed on Ambiguity**: If any required evidence is unavailable, ambiguous, mismatched, or errors, reconciliation fails closed and the journal remains retained on disk.
8. **Coherence-Only Deletion**: Only after coherence is positively proven across live identity, persistent metadata, and credential state may the coordinator delete the retained journal.
9. **Process Restart Requirement**: Because existing `RecoveryQuarantine` is process-lifetime (`int _marked`, with no unmark or reset API in `dotnet/src/AG2Router.AG2/Persistence/RecoveryQuarantine.cs`) and has no authoritative in-process clear contract, successful journal clearance must require or recommend an AG2 Router application restart before normal lifecycle work resumes, unless a later implementation introduces and verifies an explicit safe clear mechanism.

### Scope and Backlog Gating
- A supported operator-triggered invocation surface implementing this contract is required before `TODO-003` may be declared fully closed.
- The concrete UI, API, or CLI shape of this invocation surface is deferred to a later bounded design decision.
- `TODO-003` implementation must not leave manual filesystem deletion as the only supported escape from persistent quarantine.

---

## Failure semantics

### 1. Journal I/O Failures During Live Switch
- **Failure before external mutation** (writing `RECORDED` or `CREDENTIAL_APPLYING` fails):
  - The switch transaction immediately aborts.
  - No WinCred write or process termination is attempted.
  - The in-memory transaction fails cleanly; no quarantine is required.
- **Failure after external mutation** (writing `TARGET_IDENTITY_VERIFIED_PRECOMMIT` or `ROLLING_BACK` fails):
  - The failure must never make the state appear cleaner than it is.
  - The earlier conservative state (`CREDENTIAL_APPLYING`) remains on disk.
  - The coordinator triggers rollback or marks in-memory quarantine.
  - Next startup will read the conservative state and fail closed to quarantine.
- **Failure to delete journal after successful commit**:
  - The journal remains on disk in `TARGET_IDENTITY_VERIFIED_PRECOMMIT`.
  - On next startup, reconciliation observes `activeAccountId == targetAccountId`, identifies the transaction as already completed, and retries deleting the journal without performing account-state repair.

### 2. Corrupt or Unreadable Journal File
- If `switch-journal.json` is zero bytes, truncated, contains invalid JSON, or has an unrecognized `magic` or `schemaVersion`:
  - It is treated as an active unresolvable hazard.
  - The original file bytes are preserved untouched on disk.
  - The coordinator marks in-memory `RecoveryQuarantine` and `SessionVault` quarantine.
  - The file is retained for diagnostic inspection (not overwritten with a synthetic record or discarded).
  - Diagnostic logs record the exact failure.

---

## Durability assumptions and residual limitation

### Durability Primitive
The journal implementation utilizes `DurableFileWriter` (`dotnet/src/AG2Router.AG2/Persistence/DurableFileWriter.cs`, namespace `AG2Router.AG2.Persistence`, `internal sealed class DurableFileWriter : IDurableFileWriter`), leveraging:
- Temporary sibling file creation (`$"{fullPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp"`);
- Asynchronous write-through (`FileOptions.Asynchronous | FileOptions.WriteThrough`);
- Explicit physical flush to disk (`FileStream.Flush(flushToDisk: true)`);
- Atomic replacement:
  - When destination exists: `File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true)`;
  - When destination does not exist: `File.Move(tempPath, fullPath)`.

### Boundary of Guarantees
- **Gap Closed**: Eliminates the loss of in-flight transaction awareness and recovery quarantine across router crashes, unhandled exceptions, and clean system restarts under standard operating system guarantees.
- **Residual Limitation**: Standard filesystem APIs do not provide transactional atomicity across distinct storage systems (Windows Credential Manager API vs. NTFS/filesystem). Furthermore, directory entry durability across sudden unbuffered physical power-loss events remains bounded by filesystem and disk controller hardware write-caching semantics.
- **Documentation Policy**: Implementing this ADR resolves the lack of a switch journal and startup reconciliation. However, the known-limitations documentation (`docs/known-limitations.md`) must continue to accurately document the underlying physical limitation that WinCred and filesystem storage cannot form a single hardware-atomic transaction.

---

## Security and secret analysis

- **Zero Secret Exposure**: The journal file stores metadata only (`transactionId`, account IDs, state, timestamps, reason codes).
- **Explicit Exclusions**: No access tokens, refresh tokens, WinCred blobs, DPAPI plaintext, or session tokens are ever written to the journal.
- **Filesystem ACLs**: The journal resides in the application data directory, inheriting standard Windows user profile ACLs (accessible only by the user and administrators).

---

## Upgrade and no-journal behavior

- **Upgrade Compatibility**: When upgrading AG2 Router from an older release lacking the journal, no migration script or conversion is required.
- **Absence of Journal**: An absent journal file indicates that no journaled transaction was in progress. Startup proceeds cleanly.
- **Retrospective Non-Guarantee**: The system cannot retroactively discover or reconcile a crash that occurred prior to installing the journal-enabled build.

---

## Concurrency, locking, and cross-process safety

### Cross-Process File Lease Authority
- `SingleInstanceGuard` (`dotnet/src/AG2Router.Windows/Lifecycle/SingleInstanceGuard.cs`) manages single-instance execution within a single interactive Windows user session via a session-scoped mutex (`Local\AG2Router_Session_Mutex`).
- However, multiple Windows sessions (e.g. fast user switching, disconnected RDP sessions, elevated vs. non-elevated instances, or service sessions) may still share the same per-user data root.
- Therefore, `SingleInstanceGuard` is defense-in-depth and user convenience within a single session, **not** a substitute for cross-process file locking.
- The cross-process file lease (`CrossProcessFileLease.AcquireAsync` on the `.switch` resource) is the authoritative cross-process mutation exclusion mechanism governing both live switching and startup reconciliation.
- Startup reconciliation acquires the cross-process file lease with bounded, fail-closed timeout before evaluating or mutating the journal.

### Established Lock Ordering
All switch and reconciliation operations adhere strictly to the established three-tier lock hierarchy:
```
SwitchGate (in-process SemaphoreSlim)
   └── PathLockRegistry lock (per-path in-process SemaphoreSlim)
         └── CrossProcessFileLease (filesystem-level atomic lock file with delete-on-close)
```

### Startup Execution Boundary
Startup reconciliation executes in `App.xaml.cs` before any background polling, auto-routing evaluation, loopback HTTP endpoints, or tray icon interactions are activated.

---

## Alternatives considered

1. **Storing journal state in Windows Credential Manager**:
   - *Rejected*: WinCred is designed for credential payloads, not transactional state machines. Mutating WinCred to record journal states introduces additional credential churn and complicates the very store being protected.
2. **Storing journal inside `SessionVault`**:
   - *Rejected*: `SessionVault` manages encrypted account session blobs. Conflating transaction journaling with credential vaulting violates single-responsibility boundaries and complicates crash recovery.
3. **Automatic startup WinCred rollback**:
   - *Rejected*: Reverting WinCred on startup without fresh live process provenance is unsafe. The running user process or external credential state may have advanced.
4. **Automatic metadata completion from pre-crash target verification**:
   - *Rejected*: Stale identity proof from a dead pre-crash process generation is not valid live identity proof upon restart. Automatically advancing metadata without live verification violates the live-identity invariant.
5. **Retaining historical journal records (Audit History)**:
   - *Rejected*: The journal is a concurrency and fault-recovery mechanism, not an audit log. Retaining old success records on disk increases file management complexity and risks stale state misinterpretation.
6. **Treating restart as sufficient to clear quarantine**:
   - *Rejected*: If a transaction was quarantined due to uncertain state, restarting the router does not magically resolve the uncertainty. Quarantine must persist until explicitly resolved.

---

## Consequences and tradeoffs

### Positive Consequences
- Crash resilience: An interrupted switch between WinCred and metadata is deterministically detected on next launch.
- Persistent quarantine: Unresolved state re-establishes `RecoveryQuarantine` and `SessionVault` quarantine on startup, preventing automated or manual clobbering across all lifecycle surfaces (switch, enrollment, removal).
- No phantom switches: Startup will not guess or apply unverified metadata transitions.
- Clean steady-state: Successful transactions leave zero disk debris.
- Forensic preservation: Corrupt journal artifacts are preserved intact for investigation.

### Negative Consequences / Tradeoffs
- Additional disk I/O: Every switch transaction incurs small atomic journal writes (`RECORDED`, `CREDENTIAL_APPLYING`, `TARGET_IDENTITY_VERIFIED_PRECOMMIT`, and deletion).
- Manual intervention on crash: In the rare event of a crash during the credential/metadata gap, operator intervention is required to clear quarantine and re-verify identity.

---

## Implementation sequencing

Implementation of this ADR is sequenced into discrete, verifiable phases:
- **Step 1: Storage & Schema**: Implement `SwitchJournalStore`, schema DTOs, file serialization, and corruption handling with pure persistence unit tests using synthetic fixtures.
- **Step 2: Coordinator Integration**: Integrate journal phase transitions into `NativeAccountSwitchCoordinator.SwitchCoreAsync`, enforcing rollback intent ordering (`ROLLING_BACK` persisted before `QuiesceForRollbackAsync`), and verify with failure-injection tests.
- **Step 3: Startup Reconciliation & Quarantine Enforcement**: Implement `ReconcileJournalAsync`, wire it into `App.xaml.cs` startup composition before any polling/routing/loopback/tray surfaces activate, verify persistent re-marking of lifecycle `.switch` `RecoveryQuarantine` and `SessionVault` quarantine, and test all matrix branches.
- **Step 4: Supported Operator Resolution Surface**: Implement a supported operator-triggered reconciliation invocation surface adhering to the frozen semantic safety contract, ensuring manual filesystem deletion is not the sole escape from persistent quarantine.
- **Step 5: Documentation & Backlog Reconciliation**: Update `docs/persistence-and-concurrency.md` and `docs/known-limitations.md` to reflect the implemented journal, and close `TODO-003` in `TODO.md`.

---

## Test requirements to freeze

Future implementation must verify the following scenarios using synthetic fixtures and fakes without touching protected live resources:
1. **Schema serialization**: Valid JSON round-trip for all states.
2. **Corruption resilience**: Malformed, truncated, or zero-byte journal files fail closed to quarantine without overwriting the corrupt artifact.
3. **Schema versioning**: Unknown `schemaVersion` or bad `magic` fails closed to quarantine without overwriting the file.
4. **Upgrade compatibility**: Absence of journal file allows normal, unquarantined startup.
5. **Reconciliation matrix coverage**:
   - `RECORDED` state → deletes journal, starts clean.
   - `CREDENTIAL_APPLYING` state → marks quarantine, retains journal.
   - `TARGET_IDENTITY_VERIFIED_PRECOMMIT` + metadata matching target → deletes journal, starts clean.
   - `TARGET_IDENTITY_VERIFIED_PRECOMMIT` + metadata matching source → marks quarantine, retains journal, **no CAS executed**.
   - `TARGET_IDENTITY_VERIFIED_PRECOMMIT` + external/newer metadata → marks quarantine, retains journal.
   - `ROLLING_BACK` state → marks quarantine, retains journal.
   - `QUARANTINED` state → marks quarantine, retains journal.
6. **Journal write failure AFTER external mutation**:
   - Writing `TARGET_IDENTITY_VERIFIED_PRECOMMIT` or `ROLLING_BACK` fails: stale conservative on-disk state (`CREDENTIAL_APPLYING`) survives; next startup safely quarantines.
7. **Journal write failure BEFORE external mutation**:
   - Writing `RECORDED` or `CREDENTIAL_APPLYING` fails: cleanly halts the switch without modifying external state.
8. **Journal deletion failure after metadata equals target**:
   - Next startup classifies `TARGET_IDENTITY_VERIFIED_PRECOMMIT` + metadata-target idempotently as complete, retries journal deletion, and performs no account-state repair.
9. **Startup ordering**:
   - Reconciliation occurs before telemetry polling, auto-router evaluation, loopback server, or tray surfaces become active.
10. **Repeated / idempotent reconciliation**:
    - Repeated clean classifications remain no-op and safe.
    - Retained unresolved classifications continue to quarantine without destructive mutation.
11. **Quarantine enforcement across all lifecycle surfaces**:
    - Retained journal activates both lifecycle `.switch` `RecoveryQuarantine` and `SessionVault` quarantine, blocking subsequent switch, enrollment, and removal requests.
12. **Rollback intent ordering**:
    - Verifies `ROLLING_BACK` transition completes durably before `QuiesceForRollbackAsync` or credential restoration begins.
13. **Secret scanning**:
    - Asserts journal file content contains no tokens, blobs, or plaintext passwords across all states.

---

## Evidence

The architectural decisions in this record are grounded in the following executable repository sources, contracts, and documentation:
- **Switch Coordinator & Rollback**: [`dotnet/src/AG2Router.AG2/Switching/NativeAccountSwitchCoordinator.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.AG2/Switching/NativeAccountSwitchCoordinator.cs) (`SwitchCoreAsync`, `QuiesceForRollbackAsync`, `SwitchGate`).
- **Account Metadata Storage & Path Resolution**: [`dotnet/src/AG2Router.AG2/Accounts/LocalMetadataAccountStore.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.AG2/Accounts/LocalMetadataAccountStore.cs) (`GetFilePath()`, `ResolveDefaultFilePath`, `TryFinalizeSwitchAsync`).
- **Core Account Store Contract**: [`dotnet/src/AG2Router.Core/Contracts/Contracts.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.Core/Contracts/Contracts.cs) (`IAccountStore`, `ISessionVault`).
- **Durable File Persistence & Locking**: [`dotnet/src/AG2Router.AG2/Persistence/DurableFileWriter.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.AG2/Persistence/DurableFileWriter.cs) (`DurableFileWriter`, `PathLockRegistry`, `CrossProcessFileLease`).
- **Recovery Quarantine Primitives**: [`dotnet/src/AG2Router.AG2/Persistence/RecoveryQuarantine.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.AG2/Persistence/RecoveryQuarantine.cs) (`RecoveryQuarantine`, `RecoveryQuarantineRegistry`).
- **Session Vault & Quarantine Propagation**: [`dotnet/src/AG2Router.AG2/Vault/SessionVault.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.AG2/Vault/SessionVault.cs) (`GetVaultPath`, `QuarantineUnresolvedMutation`, `IsQuarantined`).
- **Enrollment & Removal Lifecycle Gates**: [`dotnet/src/AG2Router.AG2/Accounts/AccountEnrollmentService.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.AG2/Accounts/AccountEnrollmentService.cs) (`AccountEnrollmentService`, `AccountRemovalService`, lifecycle `.switch` resource gating).
- **Session-Scoped Single-Instance Guard**: [`dotnet/src/AG2Router.Windows/Lifecycle/SingleInstanceGuard.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.Windows/Lifecycle/SingleInstanceGuard.cs) (`Local\AG2Router_Session_Mutex`).
- **Application Startup Composition**: [`dotnet/src/AG2Router.App/App.xaml.cs`](file:///d:/AG2-Router/dotnet/src/AG2Router.App/App.xaml.cs) (`OnStartup`, service initialization ordering).
- **Persistence & Concurrency Contract**: [`docs/persistence-and-concurrency.md`](file:///d:/AG2-Router/docs/persistence-and-concurrency.md) ("Interrupted switch consistency").
- **Domain Identity Invariants**: [`docs/domain-rules.md`](file:///d:/AG2-Router/docs/domain-rules.md) ("Account identity", live-only identity proof).
- **Known Limitations Baseline**: [`docs/known-limitations.md`](file:///d:/AG2-Router/docs/known-limitations.md) ("Switch transaction continuity").
- **ADR Standards & Template**: [`docs/decisions/README.md`](file:///d:/AG2-Router/docs/decisions/README.md).

*(Note: These references establish the repository architecture and constraints as inspected; they do not themselves substitute for dynamic test verification of new runtime capabilities).*

---

## Explicit non-goals

- Implementing automatic cross-process or multi-machine distributed transactions.
- Automatically repairing or rolling back Windows Credentials on startup without human intervention.
- Providing an exhaustive audit log of all historical switches.
- Guaranteeing physical hardware atomicity against unbuffered hardware power loss.
- Redesigning the enrollment, removal, or routing algorithms.
