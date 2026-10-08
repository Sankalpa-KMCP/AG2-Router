# AG2 Router architecture

This is the orientation-level overview of the current system. It explains what the components are and how they fit together; it is navigation, not authority. Each subject has one owning document (see the table in [AGENTS.md](../AGENTS.md)), and source and tests outrank all prose. Where this summary and a subject document disagree, the subject document wins; where any document and current source/tests disagree, source and tests win.

## Purpose

AG2 Router is a per-user Windows desktop application for a local Antigravity ("AG2") installation. It:

- polls the active Antigravity session for per-model quota telemetry;
- keeps DPAPI-encrypted sessions for multiple enrolled Antigravity accounts;
- switches the active account automatically when the explicitly configured workload model runs low — only while Antigravity is verifiably idle — and safely under manual control;
- records a privacy-minimal, durable ledger of conversation-model token usage;
- exposes everything through a tray-first UX and a loopback WebView2 dashboard.

Unknown, stale, ambiguous, or unsafe evidence fails closed: the application refuses mutation rather than guessing.

## High-level system

Everything shipped runs in one Windows process per user session. Antigravity is external; the dashboard browser engine is embedded.

```
Windows session (single AG2Router.exe instance)
|
AG2Router.App (WPF host, composition root: App.xaml.cs)
  |-- TelemetryPollingCoordinator -- Connect-RPC over loopback --> Antigravity language server(s)
  |     \-- AG2ProcessDetector / AG2LiveAdapter / AG2TelemetryNormalizer
  |-- NativeAutoRouter + NativeAccountSwitchCoordinator   (routing decisions + switch transactions)
  |-- UsageCollectionService / UsageAggregationService    (usage ledger ingest + read models)
  |-- LoopbackServer (Kestrel, 127.0.0.1, ephemeral port)
  |     \-- serves wwwroot = generated Svelte dashboard (src/ui) + /api/* JSON
  |-- DashboardLifecycleManager -> MainWindow (WebView2, lazy) -> loads the loopback dashboard URL
  |-- TrayIconManager + QuickStatusWindow                 (tray-first interaction)
  \-- Durable state under %LOCALAPPDATA%\AG2-Router  +  WinCred `gemini:antigravity`  +  registry autostart

External: Antigravity processes (discovered locally, provenance-verified). There is no cloud service and no second backend process.
```

The Node/TypeScript implementation under `src/` is a reference/parity oracle, not a shipped component (see [Further detail](#further-detail)).

## Runtime and process boundaries

- **Single instance per session.** `SingleInstanceGuard` (dotnet/src/AG2Router.Windows/Lifecycle) holds the session mutex `Local\AG2Router_Session_Mutex`; secondary launches forward `ACTIVATE`/`CLOSE`/`EXIT` over a session-qualified named pipe with `PipeOptions.CurrentUserOnly`. Details: [runtime-architecture.md](runtime-architecture.md).
- **Upstream access is in-process.** `AG2ProcessDetector` discovers candidate language-server processes; `ProcessProvenanceValidator` proves executable identity/generation before use; `AG2RpcClient` speaks Connect-RPC over loopback ports; `AG2TelemetryNormalizer` maps raw payloads into Core DTOs. The app never talks to Antigravity through files or the UI.
- **Dashboard boundary.** WebView2 loads `http://127.0.0.1:<ephemeral>` served by the in-process `LoopbackServer`. All dashboard state flows through the HTTP API; there is no direct WebView2-to-native bridge beyond navigation.
- **Trust boundaries.** Loopback binding plus remote-address/Host checks block remote exposure, but loopback reachability is not authentication. Browser-mutation endpoints enforce `Origin`/`Sec-Fetch-Site` before body parsing; explicit switching additionally requires a per-process switch-intent token plus `confirm: true`. Credential storage relies on Windows user-scoped facilities (DPAPI `CurrentUser`, Credential Manager). Details and limits: [security-and-trust-model.md](security-and-trust-model.md).

## Major subsystems

| Subsystem | Responsibility | Primary source | Owning document |
| --- | --- | --- | --- |
| Antigravity boundary | Process discovery, provenance, Connect-RPC, telemetry normalization | dotnet/src/AG2Router.AG2 (Discovery, Rpc, Adapter, Normalization) | [runtime-architecture.md](runtime-architecture.md), [domain-rules.md](domain-rules.md) |
| Accounts & enrollment | Metadata store, active-account selection, verified enrollment/removal transactions | dotnet/src/AG2Router.AG2/Accounts | [persistence-and-concurrency.md](persistence-and-concurrency.md) |
| Vault & credentials | DPAPI session vault; WinCred read/write; redaction | dotnet/src/AG2Router.AG2/Vault, dotnet/src/AG2Router.Windows/Security | [security-and-trust-model.md](security-and-trust-model.md) |
| Routing | Workload-model pressure, durable candidate evidence, eligibility/ranking, safety gate | dotnet/src/AG2Router.AG2/Routing | [domain-rules.md](domain-rules.md) |
| Switch transactions | Verified stop → credential swap → restart → identity/target-quota verification → guarded commit/rollback; journal recovery | dotnet/src/AG2Router.AG2/Switching | [persistence-and-concurrency.md](persistence-and-concurrency.md) |
| Polling & app host | Composition root, single-instance, tray, polling loop, WebView2 lifecycle | dotnet/src/AG2Router.App | [runtime-architecture.md](runtime-architecture.md) |
| Loopback API | HTTP wire contract, static dashboard hosting, trust checks | dotnet/src/AG2Router.App/Server/LoopbackServer.cs | [api-contracts.md](api-contracts.md) |
| Usage accounting | Conversation-call ledger, multi-instance collection, aggregation, usage API | dotnet/src/AG2Router.AG2/Usage + Persistence | [usage-accounting.md](usage-accounting.md) |
| Persistence primitives | Atomic writes, path locks, cross-process leases | dotnet/src/AG2Router.AG2/Persistence (DurableFileWriter.cs and siblings) | [persistence-and-concurrency.md](persistence-and-concurrency.md) |
| Windows integration | WinCred, DPAPI, registry autostart, single-instance primitives, taskbar | dotnet/src/AG2Router.Windows | [security-and-trust-model.md](security-and-trust-model.md) |

Tests for every subsystem live in dotnet/tests/AG2Router.Tests and, for the reference implementation, test/. The evidence map at the end of [runtime-architecture.md](runtime-architecture.md) and [docs/testing.md](testing.md) map contracts to concrete test suites.

## Data and persistence

All durable application state lives outside the install directory, by default under `%LOCALAPPDATA%\AG2-Router`. An optional `DATA_DIR` environment variable can override file-based storage roots for development and test harnesses:
- In the Node reference server, `DATA_DIR` overrides the root storage directory for accounts and mock runtime state.
- In .NET, `LocalMetadataAccountStore`, `DurableQuotaObservationStore`, and `UsageCallLedger` honor `DATA_DIR` (or explicit constructor arguments) to redirect `accounts.json`, `quota-observations.json`, and `data/usage/` segments.
- Other .NET file paths derive from their configured stores (e.g. `switch-journal.json` and `config.json` resolve beside the accounts file), while the session vault defaults to `%LOCALAPPDATA%\AG2-Router\vault\sessions.dat`.
- `DATA_DIR` does **not** provide full Windows application-state isolation: external resources—including Windows Credential Manager (`gemini:antigravity`), DPAPI (`CurrentUser`), per-user registry (`HKCU`), IPC named pipes/mutexes, the WebView2 profile directory, and live Antigravity processes—remain bound to their respective OS, user, and session boundaries.

| State | Location | Nature |
| --- | --- | --- |
| Account metadata + active account | `data/accounts.json` | Durable JSON, atomic writes |
| Router configuration | `config.json` (beside accounts.json) | Durable, validated at load (fail-closed startup) |
| Candidate quota observations | `quota-observations.json` | Durable, versioned, per account/model |
| Switch recovery evidence | `switch-journal.json` + `switch-journal.json.transition` | Durable, zero-secret canonical record and transition sidecar; one recovery unit |
| Vaulted sessions | `vault/sessions.dat` | Versioned envelope of DPAPI ciphertext |
| Usage ledger | `data/usage/usage-calls-YYYY-MM.json` | Monthly segments, versioned schema |
| WebView2 profile | `webview2/` | WebView2-managed |
| Autostart preference | `HKCU\...\Run\AG2Router` | Per-user registry |
| Live Antigravity credential | Windows Credential Manager `gemini:antigravity` | External OS-managed |

Writes go through `DurableFileWriter` (temp file → flush → atomic replace) with path locks plus cross-process leases for read-modify-write mutation. Malformed or unsupported persisted data fails closed and is never silently overwritten. Ownership, locking order, and recovery semantics: [persistence-and-concurrency.md](persistence-and-concurrency.md). These paths are application contracts, not permission to inspect live user data.

## Frontend/native relationship

`frontend/` is the only authored dashboard source (Svelte 5 + TypeScript). The Vite build (frontend/vite.config.ts) emits the generated bundle to `src/ui/`, which must never be hand-edited. `AG2Router.App.csproj` links `src/ui/**` into `wwwroot` at build/publish time; `LoopbackServer` serves it; WebView2 navigates to the loopback URL. The Node reference server stages the same `src/ui` output into `dist/src/ui` via scripts/copy-ui.mjs. Frontend API types (frontend/src/lib/api/types.ts) and the .NET DTOs form one wire contract owned by [api-contracts.md](api-contracts.md). Build chain detail: [build-and-release.md](build-and-release.md).

## Usage subsystem

The ledger stores exactly one record per observed conversation-model call, identified by a salted SHA-256 of the length-prefixed `(cascadeId, responseId)` pair; raw cascade/response IDs, prompt content, and credential material never enter storage. `UsageCollectionService` discovers every validated language-server instance, applies change-gated fetching and per-endpoint attribution continuity, and ingests idempotent batches under a fixed lock order. `UsageAggregationService` provides read-only scopes (`all`, `account:{id}`, `unattributed`) over observation-time series plus explicit `HistoricalUnknown` totals. Attribution is immutable after first persistence. Full semantics: [usage-accounting.md](usage-accounting.md).

## Release and build architecture

```
frontend/ (authored Svelte)
   -> Vite build -> src/ui/ (generated, committed)
   -> AG2Router.App links it as wwwroot
   -> dotnet publish win-x64 (self-contained)
   -> scripts/package-release.ps1
   -> dist/ ZIP + SHA256SUMS.txt (+ optional Inno Setup installer from installer/AG2Router.iss)
```

dotnet/Directory.Build.props is the canonical release-version input; the release workflow derives the version from it and requires the git tag to match `v<Version>`. CI entry points: .github/workflows/ci.yml (Node matrix), dotnet-ci.yml (.NET build/test + installer compile gate), release.yml (packaging + artifact upload). Commands and packaging boundaries: [build-and-release.md](build-and-release.md).

## Architectural boundaries

What must not cross boundaries (full table in [runtime-architecture.md](runtime-architecture.md)):

- AG2Router.Core: no Win32, WPF, HTTP hosting, or concrete persistence — DTOs, records, interfaces, validation only.
- AG2Router.AG2: no WPF presentation or application composition — domain behavior only.
- AG2Router.Windows: Windows primitives only — no domain routing policy.
- AG2Router.App: composition, host UX, polling, loopback hosting only — no duplicated domain or storage implementations.
- frontend/: no server-side authority, no secret handling; authored code must not import from src/ (enforced by test/frontend-boundary.test.ts).
- src/ and test/: reference implementation and CI oracle only — a TypeScript behavior must not be assumed to exist in the shipped native application without corresponding .NET source, composition, and tests.

## Further detail

- [repo-map.md](repo-map.md) — where to find each file, generated-artifact map, common task routes.
- [invariants.md](invariants.md) — consolidated rules changes must preserve.
- [code-tour.md](code-tour.md) — deeper narrative tour of startup, transactions, and security mechanics.
- Subject documents listed in [AGENTS.md](../AGENTS.md) own their domains; release notes under docs/ and decision records under docs/decisions/ are historical by design.
