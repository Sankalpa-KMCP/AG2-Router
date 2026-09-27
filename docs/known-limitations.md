# Known limitations

This document records concise, source-evidenced CURRENT implementation gaps. It is not a roadmap. Intended behavior is defined by the owning subject document, and an item should be removed when implementation plus tests close it.

## Quota and routing

- No verified quota/routing gap currently open. Quota normalization, reset-window separation, candidate eligibility, and weakest-link exhaustion are enforced by source and tests.

Evidence and intended-invariant boundaries: [domain-rules.md](domain-rules.md).

## Frontend and loopback API

- There is no generated/shared schema or end-to-end contract gate proving frontend declarations match serialized .NET responses.

Evidence and wire-contract detail: [api-contracts.md](api-contracts.md).

## Configuration and polling

- No verified configuration/polling gap currently open. Router configuration bounds (integer polling interval 1..2147483647 ms with 10000 ms default, low-quota threshold 5–50%, candidate threshold 10–90%) are authoritatively enforced by RouterConfigValidator across API ingestion, runtime mutations, and persisted config loading; configuration persists atomically via IDurableFileWriter; PollingIntervalMs dynamically reconfigures the active telemetry timer; and the frontend config form is protected against polling clobbering by dirty-state tracking.
- Intentional layered difference: the backend/persisted validity range is integer 1..2147483647 ms, whereas the dashboard UI exposes a 2–60 second range (whole-second granularity) as a conservative UI/UX guard. Sub-2-second API and persisted values (1–1999 ms) remain contract-valid without automatic clamping or migration. Aggressive polling produces near-continuous serialized polling, increasing local RPC, CPU, and lock-file activity; external-service tolerance and rate-limit behavior under aggressive polling are unknown.

Evidence and lifecycle detail: [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Switch transaction continuity

- Switch status is in memory, and WinCred mutation plus metadata finalization are separate durable operations. Abrupt termination between them can leave cross-store state out of sync; startup has no durable transaction journal/reconciliation step.

Transaction boundaries and careful wording: [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Loopback authorization

- Mutation Origin validation accepts a missing Origin header to accommodate same-user local native processes, while rejecting foreign origins and cross-site fetch contexts across all API routes.
- Only explicit switching currently requires the additional per-process switch-intent token.
- Remote-network exposure is blocked, but same-user non-browser local processes are not generally authenticated. Cross-Windows-user/session loopback reachability is unverified by repository evidence; the implementation does not promise OS-user isolation.

Loopback binding reduces exposure but is not user authentication. See [security-and-trust-model.md](security-and-trust-model.md).

## Diagnostics persistence

- If diagnostic file persistence is blocked for an extended period while JavaScript errors continue arriving, queued complete snapshots can accumulate in memory and cause redundant writes once unblocked; retained in-memory and displayed JS errors remain strictly bounded to 100.

Durable state and lifecycle sequencing: [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Documentation drift

- docs/architecture.md describes an earlier Node foundation and staged native work rather than the shipped topology.
- docs/dotnet-migration.md is migration-era history and includes obsolete staging language.
- docs/security-model.md says WinCred access is strictly read-only, while the native switch coordinator performs controlled writes and conditional rollback through WindowsWinCredWriter.
- docs/security.md and docs/security-model.md overlap. This foundation leaves both unchanged to preserve history; security-and-trust-model.md is the current subject owner.

## Generated UI freshness

- frontend/ is authored source and Vite writes committed assets to src/ui.
- The .NET project packages src/ui as wwwroot.
- Current CI builds these assets but has no explicit clean-rebuild plus repository-diff gate, so stale committed generated output is possible.
- scripts/package-release.ps1 regenerates frontend output before publishing, including standalone invocations. A separate clean-rebuild repository-diff gate remains absent in CI.

Evidence and artifact flow: [build-and-release.md](build-and-release.md).

## Native and live validation

- Default tests rely heavily on fakes, synthetic processes, temporary directories, and source/package inspection.
- Frontend automation has static/type/build and helper/reference tests, but no authored Svelte mounting/render interaction tests and no browser/E2E suite.
- AG2 live telemetry validation is opt-in through AG2_LIVE_TEST and therefore is not ordinary CI evidence.
- Canonical live WinCred reading in the TypeScript suite is separately opt-in.
- Upgrade preservation tests deliberately avoid real user data, live WinCred, user registry state, installed application state, and production vaults.
- WebView2 coordination tests exercise the coordinator abstraction but cannot cover every installed runtime/version interaction.
- Release workflow artifacts are not proven Authenticode-signed or published by the workflow.

These are fidelity/isolation boundaries, not permission to run live tests. See [testing.md](testing.md).
