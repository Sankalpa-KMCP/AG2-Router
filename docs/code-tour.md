# AG2 Router Code Tour & Developer Onboarding Guide

Welcome to the AG2 Router codebase. This document is a comprehensive engineering tour of the architecture, security models, concurrency controls, runtime flows, and subsystem implementations across both the native .NET 10 desktop application and the Node/TypeScript reference suite.

For authoritative specifications on individual domains, refer to the owning documents:
- Runtime boundaries: [docs/runtime-architecture.md](runtime-architecture.md)
- Domain routing & quotas: [docs/domain-rules.md](domain-rules.md)
- Wire contracts & HTTP APIs: [docs/api-contracts.md](api-contracts.md)
- Security & trust model: [docs/security-and-trust-model.md](security-and-trust-model.md)
- Persistence, concurrency & recovery: [docs/persistence-and-concurrency.md](persistence-and-concurrency.md)
- Testing architecture: [docs/testing.md](testing.md)
- Packaging & release: [docs/build-and-release.md](build-and-release.md)

---

## 1. Repository Layout & Project Boundaries

The repository is structured into distinct, decoupled projects designed to separate platform-neutral domain logic from host-specific presentation, Windows APIs, and build tooling:

```
AG2_Router/
├── dotnet/                               # Shipped native Windows desktop application (.NET 10)
│   ├── src/
│   │   ├── AG2Router.Core/               # Platform-neutral models, DTOs, and interface contracts
│   │   ├── AG2Router.AG2/                # Antigravity discovery, RPC, normalization, routing & switching
│   │   ├── AG2Router.Windows/            # Windows WinCred, DPAPI, named pipe, process & registry interop
│   │   └── AG2Router.App/                # Shipped WPF host, tray lifecycle, loopback server, WebView2
│   └── tests/
│       └── AG2Router.Tests/              # Native unit, integration, packaging, and concurrency tests
├── frontend/                             # Authored Svelte 5 + TypeScript dashboard source
├── src/                                  # Node/TypeScript reference implementation
│   ├── accounts/                         # Reference enrollment and metadata management
│   ├── ag2/                              # Reference discovery, normalizer, and WinCred interop
│   ├── server/                           # Reference loopback HTTP server
│   ├── ui/                               # Generated compiled UI output (Vite build target; DO NOT EDIT)
│   └── vault/                            # Reference session vault
├── test/                                 # Node/TypeScript reference test suite (vitest)
├── scripts/                              # Build, copy, install, and uninstall automation
└── installer/                            # Inno Setup packaging definitions
```

### Architectural Separation
- `AG2Router.Core`: Contains zero references to Win32, WPF, System.Net.HttpListener, or concrete storage. Owns core DTOs (`SystemStatusDto`, `QuotaSnapshotDto`, `AccountMetadata`, `SwitchResult`) and contracts (`INativeAutoRouter`, `ISwitchJournalStore`).
- `AG2Router.AG2`: Encapsulates all domain behavior regarding Antigravity. Owns `AG2TelemetryNormalizer`, `NativeAutoRouter`, `NativeAccountSwitchCoordinator`, `SwitchJournalStore`, and `AG2ProcessLifecycle`.
- `AG2Router.Windows`: Bridges to Win32 APIs (`Advapi32.dll` for Windows Credential Manager, `Crypt32.dll` for DPAPI, and named pipes).
- `AG2Router.App`: The composition root and presentation host. Handles the Windows system tray (`TrayIconManager`), WebView2 dashboard host (`DashboardLifecycleManager`), local HTTP server (`LoopbackServer`), and periodic polling (`TelemetryPollingCoordinator`).

---

## 2. Native Startup & Composition Root (`App.xaml.cs`)

The primary composition root of the shipped desktop application resides in `dotnet/src/AG2Router.App/App.xaml.cs`.

### Startup Pipeline
When `AG2Router.App.exe` starts (`OnStartup`):
1. **Single-Instance Enforcement:** Invokes `SingleInstanceGuard.TryAcquirePrimary(...)`. If an existing instance holds the mutex, the secondary process writes command-line activation parameters into a Windows Named Pipe (`ag2-router-{user-hash}`) and immediately exits with exit code 0.
2. **Directory & Storage Initialization:** Resolves the user's data directory in `%LOCALAPPDATA%\AG2-Router`. Instantiates:
   - `WindowsDpapiProvider`: Secures vault sessions using DPAPI with `CurrentUser` scope.
   - `SessionVault`: Encrypts/decrypts serialized session payloads.
   - `LocalMetadataAccountStore`: Manages account records in `%LOCALAPPDATA%\AG2-Router\accounts.json`.
   - `SwitchJournalStore`: Manages crash-consistent switch transaction logs in `%LOCALAPPDATA%\AG2-Router\switch-journal.json`.
   - `DurableQuotaObservationStore`: Retains cross-account historical quota observations.
3. **Journal Quarantine & Reconcile:** Before starting any background polling, HTTP servers, or auto-routing, the application executes `NativeAccountSwitchCoordinator.ReconcileStartupJournalAsync()`. If an unfinalized switch journal exists from a prior application crash or system shutdown, the system enters quarantine mode to protect account state.
4. **Service Graph Composition:**
   - Connects `NativeAutoRouter` with `NativeAccountSwitchCoordinator`.
   - Wires `TelemetryPollingCoordinator` with `AG2LiveAdapter` and `NativeAutoRouter`.
   - Binds `LoopbackServer` to an ephemeral port (`127.0.0.1:0`).
   - Hooks configuration updates: changes to polling intervals pass through `UpdateConfigWithGeneration` to reject stale updates.
5. **UI & Tray Initialization:**
   - Instantiates `TrayIconManager` to display status in the Windows Notification Area.
   - Initializes `DashboardLifecycleManager` to handle lazy creation of `WebView2` windows on user request.

---

## 3. Node/TypeScript Reference Startup (`src/server/server.ts`)

In addition to the shipped .NET desktop application, the repository contains a complete Node/TypeScript reference implementation used for architectural prototyping, contract parity verification, and cross-platform CI verification.

- **Entry Point:** `src/index.ts` / `src/server/server.ts`.
- **Composition:** Assembles `AccountStore`, `SessionVault`, `AccountEnrollmentService`, and `AG2ProcessDetector`.
- **HTTP Server:** Uses Node's built-in `node:http` module to serve loopback requests and static dashboard assets from `dist/src/ui/`.
- **Parity Invariant:** The Node server and .NET server adhere to the exact same JSON wire shapes, endpoint paths, status codes, and CSRF protection headers (documented in [docs/api-contracts.md](api-contracts.md)).

---

## 4. Frontend Build & Source Relationship

The dashboard UI is authored with modern Svelte 5 (runes) and TypeScript.

### File Locations & Responsibilities
- `frontend/`: Authored source files (`frontend/src/App.svelte`, `frontend/src/lib/components/`, `frontend/src/lib/api/`).
- `src/ui/`: Generated static bundle assets (`index.html`, `app.js`, `styles.css`). **Never edit files in `src/ui/` directly.**

### Build & Link Chain
1. **Compilation:** Running `npm run build:ui` (or `npm run build`) invokes Vite with `frontend/vite.config.ts`.
2. **Output:** Vite bundles and minifies the Svelte application directly into `src/ui/`.
3. **Node Distribution:** Running `npm run build:server` invokes `scripts/copy-ui.mjs`, which copies `src/ui/` into `dist/src/ui/` for the Node reference server.
4. **.NET Native Linking:** In `dotnet/src/AG2Router.App/AG2Router.App.csproj`, an MSBuild item group links files from `..\..\..\src\ui\**` into the build output directory under `wwwroot/`:
   ```xml
   <Content Include="..\..\..\src\ui\**">
     <Link>wwwroot\%(RecursiveDir)%(Filename)%(Extension)</Link>
     <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
   </Content>
   ```
5. **Runtime Serving:** At runtime, `LoopbackServer.cs` serves files directly from the `wwwroot` directory on its loopback port.

---

## 5. Antigravity Telemetry Flow

The telemetry pipeline extracts live model capacity and session status from the local Antigravity application:

```
[Antigravity Process]
        │ (Connect-RPC over loopback / process discovery)
        ▼
[AG2LiveAdapter / ProcessDetector]
        │ (Raw Upstream Telemetry Payload)
        ▼
[AG2TelemetryNormalizer]
        │ (CanonicalModelDto, QuotaSnapshotDto, SystemStatusDto)
        ▼
[TelemetryPollingCoordinator]
        │
        ├──► [NativeAutoRouter] (Evaluates capacity thresholds & triggers auto-switches)
        ├──► [TrayIconManager] (Updates icon color & quick status tooltips)
        └──► [LoopbackServer] (Publishes to GET /api/status and SSE streams)
```

### Telemetry Normalization Pipeline
1. `AG2ProcessDetector` scans running processes on Windows for Antigravity, extracting its active listening port and process ID.
2. `AG2LiveAdapter` queries the Antigravity Connect-RPC service over loopback.
3. `AG2TelemetryNormalizer` (`dotnet/src/AG2Router.AG2/Normalization/AG2TelemetryNormalizer.cs` & `src/ag2/normalizer.ts`):
   - Maps upstream models into standardized canonical models (`gemini-flash`, `gemini-pro`, `claude-sonnet`, etc.).
   - Segregates models into model pools (Fast / Pro / Thinking).
   - Preserves explicit epistemic status: null quota fractions indicate `UNKNOWN` telemetry and are never coerced to 0% exhaustion.
4. `TelemetryPollingCoordinator` (`dotnet/src/AG2Router.App/Services/TelemetryPollingCoordinator.cs`):
   - Polling runs on a periodic background loop.
   - Applies monotonic sequence numbers; stale out-of-order poll responses are discarded.
   - Publishes thread-safe snapshots to consumers.

---

## 6. Quota Normalization & Model Pool Segregation

Quota representation requires strict fidelity to prevent premature or erroneous account switching:

### Canonical Models & Dual Wire Contracts
To maintain seamless backward compatibility with older UI and API clients while supporting new multi-modal family groupings, the normalizer produces a dual-property contract:
- `canonicalModels`: Modern array of `CanonicalModelDto` objects containing `canonicalKey`, `displayLabel`, `remainingFraction`, `resetTime`, `isExhausted`, and `modes`.
- `models`: Legacy array of `ModelQuotaDto` preserving `modelOrTier`, `label`, `remainingFraction`, and `resetTime`.

### Visual Health & Epistemic Distinctions
- **`EXHAUSTED` (0%):** Upstream explicitly reports exhaustion or 0 remaining quota. Rendered in danger/red.
- **`LOW` (≤ Threshold %):** Remaining fraction is positive but falls below the configured low threshold (default 15%). Rendered in warning/yellow.
- **`HEALTHY` (> Threshold %):** Remaining fraction is comfortably above threshold. Rendered in success/green.
- **`UNKNOWN` (null):** Telemetry is missing, unobserved, or unsupported for this model. Rendered in neutral muted styling. It is strictly forbidden to treat `UNKNOWN` as `EXHAUSTED`.

---

## 7. Account Enrollment & Identity Verification

Account enrollment captures the currently active Antigravity session and registers it as a reusable managed account in the router.

### Enrollment Invariants & Concurrency Fencing (`src/accounts/enrollment.ts`)
1. **Dual-Tier Concurrency Locks:**
   - *In-Memory Lock:* Per-email async mutex prevents overlapping enrollment attempts for the same account identity within the process.
   - *Cross-Process Lease File:* Acquires an exclusive file lease (`.enrollment.lock`) in `%LOCALAPPDATA%\AG2-Router` to prevent concurrent writes across multiple application instances.
2. **Pre-Lock & Post-Lock Revalidation:** Validates live account identity before acquiring the lock, and re-validates against disk after lock acquisition to prevent double-enrollment races.
3. **Multi-Phase Coherence Verification:**
   - Reads active credentials from Windows Credential Manager (`gemini:antigravity`).
   - Obtains active session identity from the Antigravity RPC client.
   - Uses timing-safe string comparison (`timingSafeEqual`) to verify that the active credential token belongs to the enrolled account identity, preventing credential splicing or side-channel leakage.
4. **Conditional Rollback (`restoreIfCurrent`):** If any step fails during session encryption or account metadata writing, the service rolls back only if the disk state still matches the pre-enrollment snapshot, preventing destructive rollbacks of concurrent user actions.

---

## 8. Automatic Routing Policy & Interruption Admission

The `NativeAutoRouter` (`dotnet/src/AG2Router.AG2/Routing/NativeAutoRouter.cs`) monitors quota consumption and automatically switches accounts when capacity is depleted.

### The Interruption Admission Hierarchy
To prevent deadlocks and race conditions between automatic switching, manual user switching, and dynamic configuration updates, the system enforces a strict three-tier locking hierarchy:

```
[Switch Ownership (SwitchGate)]
        │
        ▼
[_interruptionAdmission (SemaphoreSlim)]
        │
        ▼
[_stateLock (Router Internal State)]
```

### Monotonic Configuration Generation (`ConfigGeneration`)
1. When a user updates router settings via `POST /api/config`, the request carries a generation number.
2. `NativeAutoRouter.UpdateConfigWithGeneration` acquires `_stateLock`, verifies that the generation is strictly greater than `_lastAppliedConfigGeneration`, and updates the active configuration.
3. When an automatic switch is initiated, the router acquires `_interruptionAdmission`.
4. If a configuration update arrives while an auto-switch is in progress, it cleanly awaits admission before modifying thresholds or target pools.
5. The switch coordinator holds admission through process termination and releases it immediately upon issuing the stop command, ensuring that subsequent process wait cycles do not starve configuration updates.

---

## 9. Manual Switching & Switch Coordinator Transaction Phases

The `NativeAccountSwitchCoordinator` (`dotnet/src/AG2Router.AG2/Switching/NativeAccountSwitchCoordinator.cs`) manages account switching transactions with atomic rollback guarantees.

### Transaction Lifecycle Phases
1. **Phase 1: Preflight & Target Resolution:**
   - Verifies target account exists in metadata and is not disabled.
   - Resolves target session token from `SessionVault`.
   - Acquires `SwitchGate` (re-entrancy and concurrent switch prevention).
2. **Phase 2: Intent Journaling:**
   - Writes atomic switch journal entry (`SwitchJournalStore.CreateJournalEntry`) recording source account ID, target account ID, target token hash, and initial phase (`StoppingSource`).
3. **Phase 3: Controlled Process Termination:**
   - Re-verifies Antigravity process handle before sending termination signal.
   - Invokes `AG2ProcessLifecycle.StopVerifiedAsync`.
   - Triggers `onStopAttempted` and `onStopIssued` lifecycle callbacks.
4. **Phase 4: Credential Swap:**
   - Updates Windows Credential Manager target (`gemini:antigravity`) with the target account's session token.
   - Updates journal phase to `UpdatingCredentials`.
5. **Phase 5: Process Restart & Verification:**
   - Launches Antigravity executable with clean environment.
   - Awaits process readiness and polls Connect-RPC identity endpoint.
   - Confirms that Antigravity reports the target account identity.
6. **Phase 6: Live Target Quota Verification:**
   - Prior to committing account metadata, queries target account's live quota.
   - If target account is discovered to be exhausted, triggers verified rollback or records disproven cache evidence.
7. **Phase 7: Metadata Commit & Journal Deletion:**
   - Updates `accounts.json` marking target account as active.
   - Atomically deletes the switch journal file using TOCTOU-safe file deletion.

---

## 10. Credential & Session Persistence

Security of user tokens and persistent sessions is a top-priority architectural invariant:

### Security Boundaries & Asset Protection
- **Windows DPAPI (`WindowsDpapiProvider.cs`):** All session payloads stored in `%LOCALAPPDATA%\AG2-Router\vault\` are encrypted using the Windows Data Protection API (`CryptProtectData`) scoped to `DataProtectionScope.CurrentUser`. No plaintext session tokens are ever written to disk.
- **Windows Credential Manager (`gemini:antigravity`):** Read and written via Win32 `CredReadW` and `CredWriteW` APIs with `CRED_TYPE_GENERIC`.
- **Session Vault Hardening (`src/vault/session-vault.ts`):**
  - Defends against prototype pollution by filtering forbidden dictionary keys (`__proto__`, `constructor`, `prototype`).
  - Enforces strict plain-object dictionary structures (`isPlainObject`).
  - Performs authoritative disk readback verification (`saveSessionWithReceipt`) to prove ciphertext was durably committed before confirming save.
- **Zero Logging Policy:** Access tokens, refresh tokens, passwords, and authorization headers are strictly redacted from logs, exception messages, telemetry traces, and UI payloads.

---

## 11. Switch Journal Lifecycle, Quarantine & Recovery

To survive power loss, OS crashes, or unexpected process termination during an account switch, the system implements a crash-resilient journal (`SwitchJournalStore.cs` and `src/vault/`):

### The Journal State Machine
```
[No Journal] ──► CreateJournal() ──► [StoppingSource]
                                           │
                                           ▼
                                  [UpdatingCredentials]
                                           │
                                           ▼
                                  [RestartingTarget]
                                           │
                                           ▼
[No Journal] ◄── DeleteJournal() ◄── [VerifyingIdentity]
```

### Quarantine Mode
If `App.xaml.cs` or `server.ts` detects an existing `switch-journal.json` during startup:
1. The application enters **Quarantine State**.
2. Normal auto-routing and manual switches are locked out.
3. The dashboard UI displays `SwitchJournalRecoveryModal.svelte`.
4. The system inspects live state (current WinCred token, running Antigravity process identity).
5. The user or administrator can trigger `POST /api/switch/resolve-journal` to either:
   - **Roll Back:** Restore source credentials and restart Antigravity under the original account.
   - **Roll Forward:** Complete target credential swap and finalize the switch.
6. Upon successful resolution, the journal is deleted and normal operations resume.

### TOCTOU File Deletion Hardening
When deleting the journal on Windows, `SwitchJournalStore.cs` uses Win32 `CreateFileW` with `FILE_FLAG_DELETE_ON_CLOSE` and shared delete/read permissions. This prevents Time-of-Check to Time-of-Use (TOCTOU) races where another process could replace or tamper with the journal file during deletion.

---

## 12. Loopback HTTP Server & Security Boundary

The loopback HTTP server provides the bridge between the Svelte dashboard UI, tray menu, and backend services.

### Defense-in-Depth Security Fences (`LoopbackServer.cs` & `src/server/server.ts`)
1. **Localhost Binding:** Binds strictly to `127.0.0.1` on an ephemeral port assigned by the OS. Never binds to `0.0.0.0` or external network interfaces.
2. **Allowed Mutation Origin:** All mutating requests (`POST`, `PUT`, `DELETE`) are inspected before reading the request body:
   - The `Origin` header must match the local loopback server origin (`http://127.0.0.1:{port}`).
   - Cross-origin browser requests are rejected with `403 Forbidden`.
3. **`Sec-Fetch-Site` Enforcement:** Mutating endpoints block requests with `Sec-Fetch-Site: cross-site`, defeating simple CSRF attacks launched from malicious web pages opened in external browsers.
4. **Switch Intent Tokens (`X-AG2-Switch-Token`):** Manual account switch requests require a valid single-use switch intent token issued by the backend, ensuring that user confirmation in the UI was explicitly granted.

---

## 13. Windows Single-Instance Lifecycle & IPC Named Pipe

To prevent resource contention, conflicting credential overwrites, and split-brain routing, only one instance of AG2 Router may run per Windows desktop session.

### Implementation Details (`SingleInstanceGuard.cs`)
1. **Named Mutex:** Creates a local session mutex `Local\AG2Router_Session_Mutex`. The kernel-scoped `Local\` prefix already isolates it per Windows session.
2. **Named Pipe IPC:** The named-pipe namespace is machine-global, so the primary instance listens on a session-qualified pipe `AG2Router_Session_IPC_Pipe_{sessionId}` derived from the terminal-services session id (`SingleInstanceIpcNamespace.ForSession`). Independent Windows sessions therefore derive independent pipe names and never contend for one global name. The server listener uses `PipeOptions.CurrentUserOnly` so only the owning user's processes may connect, and each connection is handled on its own task with a bounded command-read timeout, so one stalled client cannot block later commands.
3. **Command Forwarding:** When a secondary instance is launched (e.g. from the Windows Start menu or installer):
   - It fails to acquire the mutex.
   - Connects to the session's named pipe and transmits a command (`ACTIVATE`, `CLOSE`, or `EXIT`).
   - The primary instance receives the command and activates the dashboard (`ACTIVATE`), closes the dashboard (`CLOSE`), or begins graceful exit (`EXIT`).
   - The secondary instance terminates immediately with exit code 0.

---

## 14. Installer, Package Ownership & Stopped-State Verification

The PowerShell installer and uninstaller (`scripts/install.ps1`, `scripts/uninstall.ps1`) handle package deployment, upgrades, and channel ownership.

### Critical Safety Invariants
1. **Dual Stopped-State Verification (`Assert-AG2RouterStopped`):**
   - Verifies that running processes are completely stopped using both process handle enumeration and Mutex probing.
   - Adheres to the fail-closed policy: `UNKNOWN != STOPPED`. If process or mutex state cannot be conclusively proven stopped, the script halts immediately rather than proceeding with file swaps.
2. **Channel Ownership Disambiguation (`absent != foreign`):**
   - Detects whether an existing installation was placed by Inno Setup, synthetic staging, or a foreign installer.
   - Prevents an uninstaller or updater from clobbering an installation owned by a different channel.
3. **Atomic Swap & Rollback:**
   - Staged files are unpacked into a temporary staging folder.
   - The existing installation is backed up.
   - Directories are swapped atomically. If an error occurs, the backup is restored.
4. **Permanent Preservation Guarantee:**
   - `scripts/uninstall.ps1` removes application binaries, shortcuts, and registry uninstall keys.
   - It **strictly preserves** user data in `%LOCALAPPDATA%\AG2-Router` and Windows Credential Manager entries (`gemini:antigravity`).

---

## 15. Test Suites, Synthetic Isolation, and Live Gates

The repository features comprehensive test coverage across both .NET and TypeScript stacks, designed to run deterministically without requiring live external services.

### Test Matrix
- **Node/Vitest Suite (`test/`):** 366 unit and integration tests covering enrollment coherence, session vault encryption, telemetry normalization, loopback HTTP security, and frontend wire contracts.
- **.NET xUnit Suite (`dotnet/tests/AG2Router.Tests/`):** 1034 unit and integration tests covering auto-routing policies, switch coordinator transactions, process lifecycle, WinCred, telemetry sequence filtering, and packaging.

### Synthetic Isolation (`SyntheticInstallationTestEnvironment.cs` & `.ps1`)
- Packaging tests execute `install.ps1` and `uninstall.ps1` inside a locked-down, synthetic sandbox.
- **Reparse Point Containment:** Validates that sandbox paths do not escape via NTFS junctions or directory symlinks.
- **PowerShell AST Static Analysis:** Inspects the Abstract Syntax Tree (AST) of the scripts before execution to whitelist permitted commands, types, and methods, preventing unauthorized system mutations.

### Live Verification Gates
Tests default to synthetic fixtures and mocks. Real interaction with live Windows Credential Manager, Antigravity processes, or Google endpoints requires explicit opt-in environment variables:
- `$env:AG2_LIVE_TEST = '1'` (Default: '0')
- `$env:AG2_RUN_LIVE_WINCRED_TESTS = 'true'` (Default: 'false')

When these variables are unset or set to '0' / 'false', live tests are skipped, ensuring zero unintended side effects during local development or CI runs.

---

## 16. Build Artifact Chain, Installer Creation & Release Verification

The complete build, packaging, and release verification pipeline is automated through root scripts:

```
[frontend/ (Svelte)]
        │ (npm run build:ui)
        ▼
[src/ui/ (Bundled HTML/JS/CSS)]
        │
        ├──► (scripts/copy-ui.mjs) ──► [dist/src/ui/ (Node reference)]
        │
        └──► (MSBuild Link to wwwroot)
                     │
                     ▼
[dotnet publish (AG2Router.App)] ──► [Release Staging Directory]
                                              │
                                              ▼
[installer/installer.iss] ──────────► [Inno Setup Compiler (ISCC)]
                                              │
                                              ▼
                                     [AG2Router-Setup-vX.Y.Z.exe]
```

### Build Commands Reference
- **Frontend Typecheck & Diagnostics:**
  ```powershell
  npm run typecheck
  ```
- **Frontend & Server Build:**
  ```powershell
  npm run build
  ```
- **Node Test Suite:**
  ```powershell
  npm test
  ```
- **.NET Build (Release):**
  ```powershell
  dotnet build dotnet/AG2Router.sln -c Release
  ```
- **.NET Test Suite (Release):**
  ```powershell
  dotnet test dotnet/AG2Router.sln -c Release --no-build --verbosity normal
  ```
- **Packaging Verification:**
  ```powershell
  dotnet test dotnet/tests/AG2Router.Tests/AG2Router.Tests.csproj -c Release --filter "FullyQualifiedName~Packaging"
  ```

---

*This guide is maintained alongside the AG2 Router codebase. For new features or architectural changes, remember the core rule: update the source, update the tests, and update the authoritative documentation together.*
