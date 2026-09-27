# AG2 Router TODO

Canonical engineering backlog for unfinished, planned, research, and accepted work.
Source and tests remain the technical truth. Each item names its evidence; durable
rules stay in their owning documents under `docs/`, and current implementation gaps
stay in `docs/known-limitations.md`.

## Current state

- Generated: 2026-09-26
- Repository state: `main` at `2f4c8f17cc4e2263aeecef9782433d973150b071` (tree `688c7e4faebf85f91859b45fc2c1c3afa27e1c48`), clean tracked tree + this untracked TODO.md.
- Last shipped release: v0.4.0. Release activity remains on hold pending explicit user/controller authorization.
- Status legend: OPEN DEFECT · PLANNED · RESEARCH · QUALITY IMPROVEMENT · ACCEPTED LIMITATION · DEFERRED · CLOSED

## Now — verified defects

No verified defects currently open.

## Next — planned engineering

### TODO-003 · PLANNED · Durable switch transaction journal and startup reconciliation

- Priority: highest non-defect priority (follows verified defects TODO-001 and TODO-002) — critical data-integrity gap during crash window.
- Problem: switch transaction state lives only in `NativeAccountSwitchCoordinator` memory. Applying the WinCred target and finalizing account metadata are separate durable operations, and abrupt termination between them can leave live credential state and metadata out of sync with no startup reconciliation (documented in `docs/known-limitations.md`, "Switch transaction continuity").
- Why it matters: this crash window is the strongest remaining data-integrity gap; recovery today is manual.
- Evidence: `docs/persistence-and-concurrency.md` ("Interrupted switch consistency"), `dotnet/src/AG2Router.AG2/Switching/NativeAccountSwitchCoordinator.cs`, `dotnet/src/AG2Router.AG2/Vault/SessionVault.cs`.
- Completion criteria: a design decision covering journal location, format, ownership, and startup reconciliation semantics is recorded as an ADR; implementation detects and reconciles a WinCred/metadata divergence at startup; fault-injection tests prove reconciliation preserves newer external state and does not weaken `RecoveryQuarantine` semantics; the known-limitations entry is removed when implementation and tests close it.
- Dependencies: design phase first; coordination with the existing switch-lease and quarantine model.

## Research / architecture decisions

### TODO-004 · RESEARCH · Antigravity authentication/enrollment lifecycle mapping

- Priority: high research priority (second highest non-defect priority, follows TODO-003) — resolves core session durability and candidate observability questions.
- Objective: map the current Add Account/enrollment behavior against the actual Antigravity authentication lifecycle and separate what AG2 Router proves from what it assumes. Investigation only; no redesign is authorized by this item.
- Questions to answer from source/runtime evidence: how Google authentication relates to Antigravity account readiness; what enrollment currently proves (live telemetry identity plus the WinCred blob at capture time) versus assumes (session durability across restart or power cycle); expected behavior after a normal Windows restart; whether persisted enrolled accounts unnecessarily require browser login; candidate observability across restarts (observed candidate quotas are process-memory-only; after restart, automatic switching may be unable to select a candidate until that account has been observed live in the current process lifetime — treat this as a research question / operational consequence to verify, not as a proven defect); and which states the architecture can actually distinguish — for example READY, REAUTH_REQUIRED, ineligible/invalid session, and uncertain identity.
- Invariants to preserve: a switch is successful only after the restarted, verified process reports the target identity (`docs/domain-rules.md`, "Account identity"), preserving fail-closed candidate selection and live-identity-proof requirements.
- Evidence: `dotnet/src/AG2Router.AG2/Accounts/AccountEnrollmentService.cs`, `dotnet/src/AG2Router.AG2/Adapter` and `Discovery`, `dotnet/src/AG2Router.Windows/Security/WindowsWinCredReader.cs`, `docs/domain-rules.md`.
- Completion criteria: a written lifecycle map with explicit proof/assumption boundaries and a proposed state model, each claim tied to source or to an explicitly authorized live observation; an ADR if a state-machine change is proposed.
- Dependencies: any live observation requires explicit user authorization under the protected-resource gates in `docs/testing.md`.

### TODO-005 · RESEARCH · Isolated Antigravity profile feasibility (MultiGravity-style switching backend)

- Priority: medium research priority (follows TODO-004) — exploratory feasibility study of alternative backend.
- Objective: evaluate whether profile isolation (separate user-data directories, possibly virtualized `USERPROFILE`/environment) can become a future switching backend, before any implementation decision.
- Questions to answer from actual source/runtime evidence only: whether Antigravity reliably isolates accounts using separate user-data directories; whether a virtual `USERPROFILE` or related environment isolation is required; whether SSH/remote-mode behavior changes credential storage; the exact credential/token location and format; whether tokens persist across reboot/restart; token security compared with the current WinCred + DPAPI handling; filesystem ACL and security implications; language-server/process environment propagation; process/port/profile isolation; same-profile double-launch behavior; logout/reauthentication behavior; compatibility with live-identity verification as implemented by `AG2ProcessDetector` and `ProcessProvenanceValidator`; resilience to Antigravity updates; and whether such a backend should remain experimental, coexist with the current switch coordinator, or replace it.
- Constraint: nothing about the external approach is proven until its source or runtime behavior has actually been inspected; all comparisons must reference the guarantees the current implementation documents in `docs/security-and-trust-model.md` and `docs/persistence-and-concurrency.md`.
- Completion criteria: an evidence-backed feasibility report answering each question, with a recommendation (experimental / coexist / replace / reject) and an ADR if a direction is chosen.
- Dependencies: live Antigravity observation requires explicit user authorization; must not weaken process-provenance or identity-verification invariants.

## Quality / validation backlog

Items in this section remain lower priority than TODO-003 through TODO-005 unless current repository evidence contradicts that ordering.

### TODO-006 · QUALITY IMPROVEMENT · Generated or shared frontend/backend API schema

- Priority: quality backlog (lower priority than TODO-003 through TODO-005).
- Problem: `frontend/src/lib/api/types.ts` and the .NET DTOs are maintained in parallel by hand; compiling both halves does not prove wire agreement (`docs/known-limitations.md`, "Frontend and loopback API").
- Completion criteria: a machine-checked contract (generated schema or contract tests derived from the .NET serialization surface) fails CI on drift and supersedes the partial coverage in `test/frontend-contract-drift.test.ts` as the primary gate.
- Dependencies: choose the generation source (C# records or a shared schema definition).

### TODO-007 · QUALITY IMPROVEMENT · Mounted Svelte/browser end-to-end coverage

- Priority: quality backlog (lower priority than TODO-003 through TODO-005).
- Problem: authored Svelte components are exercised only through static checks, helper tests, and server-rendered markup; nothing mounts components or drives form, modal, or periodic-refresh interaction against a loopback server (`docs/testing.md`, "Frontend interaction fidelity").
- Completion criteria: a committed browser-level suite covers at least the configuration form (including the TODO-001 invalid-input path), the account modal flows, and the dashboard refresh/mutation fence; runs on CI without touching live resources.
- Dependencies: none; coordinate with TODO-006 so assertions target the generated contract if it lands first.

### TODO-008 · QUALITY IMPROVEMENT · CI clean-rebuild diff gate for committed `src/ui`

- Priority: quality backlog (lower priority than TODO-003 through TODO-005).
- Problem: CI builds the UI but never compares the rebuild against the committed `src/ui`, so stale generated output is possible (`docs/build-and-release.md`). A clean rebuild at the HEAD shown above was verified byte-identical to the committed blobs on 2026-09-26, so the current committed output is fresh; the standing gate is still missing.
- Completion criteria: a CI step builds the frontend into a disposable location (without regenerating tracked assets in place) and fails when committed `src/ui` differs; the known-limitations entry is removed when closed.
- Dependencies: none.

### TODO-009 · QUALITY IMPROVEMENT · Installed old-version to current-version upgrade smoke

- Priority: quality backlog (lower priority than TODO-003 through TODO-005).
- Problem: preservation tests use synthetic staged layouts; no proven live installed-old-version to current upgrade path exists (`docs/build-and-release.md`, "Release provenance limits").
- Completion criteria: a repeatable, authorization-gated smoke procedure installs the previous release over a disposable profile and verifies upgrade preservation and uninstall boundaries; documented in `docs/testing.md`.
- Dependencies: explicit user authorization for any run that touches an installed application.

### TODO-010 · QUALITY IMPROVEMENT · Stronger opt-in live-integration validation

- Priority: quality backlog (lower priority than TODO-003 through TODO-005).
- Problem: live AG2 telemetry and WinCred validation exists but is opt-in and narrow (`docs/testing.md`, "Live opt-ins"; `docs/known-limitations.md`).
- Completion criteria: the opt-in suites' compatibility assertions are broadened (telemetry contract, process provenance, WinCred framing) while default CI stays isolated and real credentials remain untouched; no live gate may ever be enabled automatically.
- Dependencies: explicit user authorization for any live run.

### TODO-015 · QUALITY IMPROVEMENT · Explicit scoping of root .env.example to reference implementation

- Priority: low (quality backlog; lower priority than TODO-003 through TODO-005) — configuration clarity.
- Problem: the root `.env.example` documents variables such as `PORT`, `AUTO_SWITCH`, `LOW_QUOTA_THRESHOLD`, `MIN_CANDIDATE_QUOTA`, and `POLL_INTERVAL_MS`. Current repository evidence indicates these belong to the TypeScript reference implementation (`src/server/server.ts`) rather than the shipped .NET runtime (`AG2Router.App`), which uses its own runtime configuration path and ephemeral loopback binding. This file should eventually be explicitly scoped/annotated so users do not mistake reference-server configuration for shipped-product configuration.
- Completion criteria: `.env.example` is explicitly scoped to the TypeScript/reference implementation or otherwise clearly annotated; related README/configuration claims are cross-checked in the same future change.
- Dependencies: documentation/annotation update only; do not edit `.env.example` until authorized.

## Accepted limitations / deferred

Owned by `docs/known-limitations.md` and the subject documents; recorded here only as backlog pointers (not active implementation priorities).

- TODO-011 · ACCEPTED LIMITATION · Authenticode/code signing — deferred pending signing infrastructure; the SHA256 checksum manifest is currently the only artifact integrity proof.
- TODO-012 · ACCEPTED LIMITATION · Managed-memory plaintext lifetime — best-effort zeroing cannot erase every transient copy; revisit only if a sharper threat model requires it.
- TODO-013 · ACCEPTED LIMITATION · Same-user/session trust surface — loopback HTTP mutations verify intent (loopback binding, Host, Origin, `Sec-Fetch-Site`, switch token) but not the OS user, and the same-user/session trust surface also includes the session-scoped named-pipe IPC implemented by SingleInstanceGuard (`dotnet/src/AG2Router.Windows/Lifecycle/SingleInstanceGuard.cs`); cross-Windows-user/session reachability remains unverified; revisit if the threat model changes.
- TODO-014 · DEFERRED · Historical documentation cleanup (`docs/architecture.md`, `docs/security-model.md`, `docs/dotnet-migration.md`) — currently labeled historical; rewrite only when a documentation task justifies it.
- TODO-016 · ACCEPTED LIMITATION · Diagnostics persistence accumulation — diagnostics snapshots can accumulate in memory while persistence is blocked, while displayed/retained errors remain bounded (to 100); this is currently accepted, not a verified defect; completion criterion: either the limitation remains explicitly documented/pointer-linked, or future implementation bounds the queue and authoritative docs/TODO are updated together.

## Completed / closed

- AUD-001 release tag/version guard — fixed: the release workflow derives the canonical version from `dotnet/Directory.Build.props` and fails on mismatch.
- AUD-101 through AUD-108 — closed; re-verified fixed at the HEAD shown above: frontend helper ownership with import-boundary test (AUD-101), unified account/quota observation (AUD-102), frontend API type alignment with drift test (AUD-103), dead-seam removal in `NativeAutoRouter` (AUD-104), case-sensitive release tag guard (AUD-105), workflow permissions/concurrency/timeouts (AUD-106), action SHA pinning (AUD-107), truthful README routing wording (AUD-108). Reopen only on regression evidence.
- TODO-001 · CLOSED · RouterConfig has no server-side validation boundary (AUD-109) — closed via PR #25 (`fix: validate router configuration`): authoritative RouterConfig validation rejects invalid API and runtime configuration before persistence or application; invalid persisted configuration fails closed before polling or routing startup; routing threshold bounds are enforced across .NET and the TypeScript reference server; startup configuration errors retain actionable validation detail without exposing the full config filesystem path in user-facing dialogs; full diagnostic exception context preserved in application logs; covered by focused negative and regression tests.
- TODO-002 · CLOSED · Misleading enrollment contention wording (AUD-110) — closed via PR #28 (`fix: clarify enrollment contention errors`, commit `d33b59e382015081adc5b1194b0cee14d0fa229c`, merged as `57a076137eee412b04b6f156f1d0407ab798caa6`): retryable per-identity enrollment and lifecycle ownership contention timeouts report retry guidance instead of claiming manual recovery; ordinary contention paths remain non-quarantining; genuinely uncertain and quarantined enrollment paths retain manual-recovery semantics; covered by deterministic regression tests for both contention branches; post-merge CI verified successful.
