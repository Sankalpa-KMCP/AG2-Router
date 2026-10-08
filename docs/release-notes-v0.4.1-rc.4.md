# AG2 Router v0.4.1-rc.4

**Version:** 0.4.1-rc.4
**Release channel:** Prerelease candidate for supervised testing
**Intended package target:** Windows x64

This release candidate contains the accumulated engineering remediation, durability, and safety hardening across the AG2 Router subsystems for supervised evaluation. It is an unreleased testing candidate; no public release, signing, or distribution has been authorized.

## Highlights & Accumulated Hardening

- **Switching Planner & Readiness (`SwitchPlanner`):** Read-only readiness assessment checks target existence and validation, vaulted-session presence, whether the target is already active, coordinator admission/recovery state, router safety/cooldown, and live connectivity/activity. Configured-model quota evidence is advisory for manual switching. The dashboard displays the result before confirmation; execution still requires coordinator admission checks.
- **Routing Resilience & Admission (`NativeAutoRouter`, `CandidateSelector`):** Resilient candidate evaluation under load, async observation revision freshness gating, target activation verification, and fail-closed admission refusal when switch prerequisites are not proven.
- **Journal & Recovery Durability (`SwitchJournal`, `SwitchTransitionMarker`):** Canonical journal state transitions with atomic companion transition markers, preventing unobserved crash states and ensuring safe runtime coherence and rollback.
- **Target Activation Provenance & Quota Invalidation:** The switch coordinator records target-activation provenance in the durable journal before target credential application. The coordinator requests account-wide quota-observation invalidation after failed target activation, and the quota store persists that invalidation. Recovery distinguishes proven unattempted activation from attempted or uncertain activation when deciding whether target evidence must be invalidated before clearance.
- **RPC Transport Confinement (`AG2RpcClient`):** Production daemon transport confines `x-codeium-csrf-token` to the selected loopback daemon endpoint; HTTP proxies are explicitly bypassed (`UseProxy = false`), redirects are rejected immediately from response headers (`AllowAutoRedirect = false`, `ResponseHeadersRead`), and one deadline (5 seconds by default) spans dispatch, headers, and permitted response-body reads.
- **PoolStatus Propagation Freshness (`TelemetryPollingCoordinator`, `LoopbackServer`):** Authoritative router snapshot ownership with access-time freshness refreshes; TelemetryPollingCoordinator and desktop status dispatchers prevent stale or race-delayed pool snapshots from overwriting newer states across `/api/status`, tray, and Quick Status presentation.
- **Desktop & WebView2 Navigation Safety:** `WebViewNavigationPolicy` constrains top-level navigation to the dashboard's configured scheme, host, and port, rejecting foreign origins and userinfo. `MainWindow` cancels rejected navigations and handles new-window requests without opening them. Separately, the loopback server sends a Content Security Policy restricting scripts to the same origin. Actual WebView2 runtime verification remains pending.
- **Usage Checkpoint Byte Preservation (`UsageCollectorStateStore`):** Corrupted usage collection checkpoints are preserved fail-closed under path locks and cross-process leases without silent overwriting or data loss.
- **Diagnostic Secret Redaction (`AG2Security`):** Sanitization of recognized tokens across multi-line JSON separator whitespace, multi-parameter HTTP Digest challenges/responses containing internal brackets/braces/punctuation, and strict line-bounded `Authorization` header consumption.

## Validation Status

- **Native Test Suite (.NET):** 1,758 tests passed, 0 failed, 1 intentional live-gate skip (`AG2LiveParityTests`).
- **Node Reference Test Suite:** 419 tests passed, 0 failed, 1 intentional live-gate skip (`AG2WinCredReader`).
- **TypeScript / Svelte Diagnostics:** 0 errors, 0 warnings.
- **Cross-Process Transport Security:** Child process proxy bypass and redirect rejection validated across isolated environments.

## Unresolved Qualifications & Operational Limits

- **Live Parity Gate:** Live Antigravity daemon integration test (`AG2LiveParityTests`) was intentionally skipped (`AG2_LIVE_TEST=1` not configured).
- **Desktop Interaction Verification Pending:** Manual interactive verification of WebView2 rendering, confirmation modal interactions, keyboard focus, and tray/Quick Status transitions on a physical Windows desktop session remains pending.
- **Packaging and Installer Verification Pending:** Release installer generation (`Inno Setup`) and code signing remain unexecuted and pending.
- **No Public Release Authorized:** This document describes candidate preparation only.

## Installation and Testing Notes

After packaging and distribution are separately authorized, the expected filenames are `AG2Router-Setup-v0.4.1-rc.4-win-x64.exe` for the per-user installer and `AG2Router-v0.4.1-rc.4-win-x64.zip` for the archive installation path. Verify any distributed files against `SHA256SUMS.txt`.

- Signing remains pending; unsigned binaries may produce a Windows Defender SmartScreen unknown-publisher notice.
- The dashboard requires the Microsoft Edge WebView2 Runtime.
- Application data resides under `%LOCALAPPDATA%\AG2-Router`. Back up critical session and account data before testing.

Historical release notes for v0.4.1-rc.3 and v0.4.0 remain unchanged.
