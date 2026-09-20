# AG2 Router &bull; Architecture Specification

## 1. Overview & Core Philosophy

**AG2 Router** is a minimal, lightweight local Windows application and service designed to monitor quota across multiple Antigravity 2 accounts, present a clear local dashboard, and enable safe, automated account switching when quota drops below configured thresholds.

### Guiding Principles

1. **Lightweight First:** Zero runtime external dependencies. Built entirely on modern Node.js (20+) standard library primitives (`node:http`, `node:fs`, `node:crypto`, `node:test`). No Electron, no Chromium background instance, no database engine.
2. **Strict Isolation:** Unofficial and version-sensitive Antigravity 2 integration details remain strictly encapsulated behind an AG2 adapter boundary. Core routing, account management, and UI layers never touch Windows processes or Connect-RPC internals directly.
3. **Safety & Zero Work Disruption:** Under no circumstance will the router switch accounts while an active agent cascade run / trajectory is in progress. The safety gate strictly enforces confirmation of IDLE status before any transition.
4. **Truthful Telemetry:** The system represents state honestly. No fake accounts, mock quotas, or misleading combined numbers are displayed.

---

## 2. Module Responsibilities & System Boundaries

The application is structured into clearly separated architectural domains:

```
src/
├── ag2/         # Antigravity 2 integration layer (Adapter boundary)
├── accounts/    # Account metadata domain models & storage abstractions
├── router/      # Quota assessment, candidate selection, & idle safety gate
├── server/      # Lightweight loopback HTTP server & API surface
├── ui/          # Minimal vanilla HTML/CSS/JS dashboard
├── config/      # Runtime configuration & environment overrides
└── index.ts     # Process entrypoint, bootstrap, & graceful shutdown
```

### 2.1 AG2 Integration Boundary (`src/ag2/`)

All Antigravity 2 daemon communication is isolated behind the `IAG2Adapter` interface:

* `discover()`: Detects running `language_server.exe` processes and resolves active dynamic ports.
* `getCurrentAccount()`: Retrieves currently signed-in account identity via Connect-RPC `GetUserStatus`.
* `getQuota()`: Retrieves per-model quota allocations and prompt/flow credit pools.
* `getActivityState()`: Inspects cascade trajectory runs to determine whether AG2 is `IDLE` or `BUSY`.
* `switchAccount()`: Initiates session activation (staged for subsequent security/switch layer).
* `verifyAccount()`: Confirms that the target session is actively recognized by the daemon.

In this foundation stage, `AG2AdapterFoundation` enforces the interface contract while explicitly marking mutation methods as `NotImplementedError`, ensuring unsupported operations fail explicitly rather than feigning success.

### 2.2 Accounts Layer (`src/accounts/`)

The accounts module manages registered account records:

* **Separation of Concerns:** Contains strictly **non-secret metadata** (email, priority, standard vs. reserve flag, validation status).
* **Zero Secrets in Storage:** OAuth access tokens, refresh tokens, and passwords are never stored in metadata files. Secret storage is deferred to a future bounded DPAPI/WinCred security stage.
* **Storage Abstraction:** `IAccountStore` supports both in-memory and local filesystem persistence (`LocalMetadataAccountStore`) using atomic file write patterns (`.tmp` write followed by `fs.rename`).

### 2.3 Router & Quota Engine (`src/router/`)

The routing engine evaluates account capacity and orchestrates safe transitions:

* **Deterministic Candidate Selection (`selector.ts`):**
  * Evaluates current account quota against `lowQuotaThresholdPercent` (default: 15%).
  * Filters candidate accounts against `minimumCandidateQuotaPercent` (default: 30%).
  * Respects reserve account rules: standard accounts are strictly preferred; reserve accounts are only considered when all standard accounts are exhausted.
  * Deterministic tie-breaking: highest remaining quota fraction $\to$ lowest integer priority number $\to$ alphabetical account ID.
* **Idle Safety Gate (`safety-gate.ts`):**
  * State lifecycle: `LOW QUOTA → SWITCH PENDING → WAIT FOR IDLE → SWITCH → VERIFY`.
  * If Antigravity 2 has $\ge 1$ running trajectory or reports `BUSY`, switching is prohibited and the system transitions to `WAITING_FOR_IDLE`.

### 2.4 Server & API Surface (`src/server/`)

A minimal HTTP server running on native `node:http`:

* Binds strictly to `127.0.0.1` (loopback only).
* Rejects non-loopback connections.
* Enforces strict path traversal prevention on static asset requests.
* Provides REST endpoints:
  * `GET /api/status`: Overall system health, AG2 discovery status, router state, and telemetry.
  * `GET /api/accounts`: List of registered accounts with active session indicator.
  * `POST /api/accounts`: Register new non-secret account metadata.
  * `DELETE /api/accounts/:id`: Remove registered account metadata.
  * `GET /api/config`: Retrieve router configuration.
  * `POST /api/config`: Update threshold and auto-switch settings.

### 2.5 Dashboard (`src/ui/`)

A vanilla HTML5/CSS3/JavaScript client application:

* Adheres to professional UI standards: restrained neutral typography, generous spacing, subtle borders, no gratuitous animations or card soup.
* Automatically adapts to system light and dark color schemes.
* Truthfully displays empty states (`--%`, `Not connected`, `Waiting for Antigravity 2`).

---

## 3. Quota Accounting Rules

1. **Credit Segregation:** Prompt credits and Flow credits originate from distinct resource pools. They are tracked separately and **must never be aggregated into a single combined balance**.
2. **Model Pool Precision:** Quotas are tracked per model (e.g. `Gemini 3.8 Flash (High)`, `Claude Sonnet 4.6 (Thinking)`). Model percentages are never averaged across different accounts.

---

## 4. Implementation Phasing

| Phase | Description | Status |
| :--- | :--- | :--- |
| **Phase 1: Foundation & Setup** | Repository initialization, TypeScript architecture, contracts, selector, safety gate, local loopback server, UI shell, test suite | **COMPLETE** |
| **Phase 2: Discovery & Telemetry** | Native Windows process detection (`language_server.exe`), Connect-RPC client, live telemetry ingestion | Staged |
| **Phase 3: Secure Session Vault** | Windows DPAPI encryption, WinCred `gemini:antigravity` target management, session capture | Staged |
| **Phase 4: Cold Switch Execution** | Process termination, WinCred swap, daemon respawn, post-switch verification | Staged |
| **Phase 5: Automated Routing** | End-to-end autonomous switching on low quota with safety gate enforcement | Staged |
