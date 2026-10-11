# Known limitations

This document records concise, source-evidenced CURRENT implementation gaps. It is not a roadmap. Intended behavior is defined by the owning subject document, and an item should be removed when implementation plus tests close it.

## Quota and routing

- WorkloadModelKey is explicit user-configured intent, not automatic detection of the IDE model dropdown. Users must keep it aligned with the workload they want routing to protect. Unset intent fails closed; this is a supported configuration boundary rather than a missing requested-model implementation.
- There is no implemented read-only quota query for an inactive enrolled account. The Connect-RPC client addresses the live language server and has no account/session selector. Durable observations survive restart, but an account without a prior coherent live observation remains ineligible. Their two-hour freshness policy is an application bound, not an upstream guarantee. Switching merely to probe quota would disturb the workload and is not read-only observation.
- Current routing uses conservative durable evidence and verifies live target quota before commit. Cached evidence cannot guarantee future capacity or session validity; unknown, stale, or disproven evidence blocks eligibility. These boundaries do not imply the implemented automatic path is unreachable, and they do not establish installed/live release verification.

Evidence and intended-invariant boundaries: [domain-rules.md](domain-rules.md).

## Frontend and loopback API

- There is no generated/shared schema or end-to-end contract gate proving frontend declarations match serialized .NET responses.

Evidence and wire-contract detail: [api-contracts.md](api-contracts.md).

## Configuration and polling

- No verified configuration/polling gap currently open. Router configuration bounds (integer polling interval 1..2147483647 ms with 10000 ms default, low-quota threshold 5–50%, candidate threshold 10–90%) are authoritatively enforced by RouterConfigValidator across API ingestion, runtime mutations, and persisted config loading; configuration persists atomically via IDurableFileWriter; PollingIntervalMs dynamically reconfigures the active telemetry timer; and the frontend config form is protected against polling clobbering by dirty-state tracking.
- The dashboard edits polling in seconds with millisecond precision and preserves sub-second persisted values; the backend integer range remains authoritative. Aggressive polling produces near-continuous serialized polling, increasing local RPC, CPU, and lock-file activity; external-service tolerance and rate-limit behavior under aggressive polling are unknown.

Evidence and lifecycle detail: [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Switch transaction continuity

- The durable switch journal and startup reconciliation detect interrupted transactions and block uncertain state. WinCred mutation and metadata finalization remain separate durable operations; reconciliation does not blindly repair all cross-store divergence. Unresolved journals require proof-based operator resolution or manual remediation, and resolved quarantine can require restart. This is a conservative recovery boundary, not an absent journal implementation.

Transaction boundaries and careful wording: [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Loopback authorization

- Mutation Origin validation accepts a missing Origin header to accommodate same-user local native processes, while rejecting foreign origins and cross-site fetch contexts across all API routes.
- Explicit switching and journal resolution require the additional per-process switch-intent token; other mutations do not generally authenticate same-user native callers.
- Remote-network exposure is blocked, but same-user non-browser local processes are not generally authenticated. Cross-Windows-user/session loopback reachability is unverified by repository evidence; the implementation does not promise OS-user isolation.

Loopback binding reduces exposure but is not user authentication. See [security-and-trust-model.md](security-and-trust-model.md).

## Diagnostics persistence

- The structured diagnostic logging foundation (P0) and subsystem wiring across bootstrap, WebView2 recovery, RPC discovery/probing, and telemetry/usage accounting (P1a) are implemented and verified with valid JSONL records (header records on line 1, no `#` comment lines), pre-admission in-memory buffering, strict queue bounds (512 items, 4 MiB memory bound), segment rotation (4 MiB / 8 segments / 32 MiB total / 14 days), truthful retention accounting (`RetentionFailureCount`), and hard pre-ingestion privacy sanitization with regex pre-bounding and safe symbol enforcement.
- Persistence employs a single active writer lease (`.active_writer.lock`). Early startup and secondary instances run in memory-only mode via `MemoryDiagnosticSink`. Primary promotion occurs strictly after `SingleInstanceGuard` acquisition. If multiple AG2 Router instances execute simultaneously under the same user profile, only the primary instance acquires disk persistence rights; secondary instances degrade to memory-only ring-buffer diagnostics and do not create lock files or append to disk logs.
- Diagnostic viewing in the dashboard UI (P2) and installer-level log collection/export (P3) remain planned for subsequent phases; diagnostic events currently persist to JSONL files in `%LOCALAPPDATA%\AG2-Router\diagnostics\` and can be queried via `IDiagnosticSink.GetRecentEvents()`.
- Legacy `js_runtime_errors.json` writes remain active alongside the structured sink; retained in-memory and displayed JS errors in the legacy viewer remain bounded to 100 items / 64 KiB with O(1) coalescing of pending writes under delayed persistence.

Durable state and lifecycle sequencing: [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Generated UI freshness

- frontend/ is authored source and Vite writes committed assets to src/ui.
- The .NET project packages src/ui as wwwroot.
- Current CI builds these assets but has no explicit clean-rebuild plus repository-diff gate, so stale committed generated output is possible.
- scripts/package-release.ps1 regenerates frontend output before publishing, including standalone invocations. A separate clean-rebuild repository-diff gate remains absent in CI.

Evidence and artifact flow: [build-and-release.md](build-and-release.md).

## Native and live validation

- Default tests rely heavily on fakes, synthetic processes, temporary directories, and source/package inspection.
- Frontend automation includes static/type/build checks, helper/API tests, and server-rendered authored quota and routing components. There is no committed mounted-browser/E2E CI suite covering forms, recovery confirmation, refresh races, or the full loopback workflow; server rendering does not prove interaction behavior.
- AG2 live telemetry validation is opt-in through AG2_LIVE_TEST and therefore is not ordinary CI evidence.
- Canonical live WinCred reading in the TypeScript suite is separately opt-in.
- Upgrade preservation tests deliberately avoid real user data, live WinCred, user registry state, installed application state, and production vaults.
- WebView2 coordination tests exercise the coordinator abstraction but cannot cover every installed runtime/version interaction.
- Release workflow artifacts are not proven Authenticode-signed or published by the workflow.

These are fidelity/isolation boundaries, not permission to run live tests. See [testing.md](testing.md).
