# Known limitations

This document records concise, source-evidenced CURRENT implementation gaps. It is not a roadmap. Intended behavior is defined by the owning subject document, and an item should be removed when implementation plus tests close it.

## Quota and routing

- Missing per-model quota is normalized to 1.0 by AG2TelemetryNormalizer. This conflicts with the intended invariant that unknown quota is not known 100%.
- NativeAutoRouter seeds accounts without an observed quota as 1.0 before candidate selection. An unobserved account can therefore appear fully available.
- CanonicalizeModelQuotas groups all entries with the same ModelOrTier without including reset time, then selects one reset value. This can collapse incompatible reset windows without explicit shared-pool evidence.
- Tests cover label fallback separation when reset times differ, but do not establish the required same-ModelOrTier incompatible-window behavior.
- NativeAutoRouter excludes exhausted models before calculating the account-level minimum, so a healthy model can mask an exhausted model. The intended mixed-model policy is not specified by authoritative repository requirements.

Evidence and intended-invariant boundaries: [domain-rules.md](domain-rules.md).

## Frontend and loopback API

- Backend canonical quota fields are key, label, modelOrTier, remainingFraction, resetTime, isExhausted, and modes. The frontend declares canonicalKey, displayLabel, timeUntilReset, and isRollingWindow.
- Backend RouterConfigDto includes autoSwitchEnabled; the frontend RouterConfigDto omits it although saveConfig sends it.
- Backend native switch result codes are strings; frontend switch status/execution declarations model code as numeric.
- Backend account metadata exposes lastActiveAt while the frontend declares lastUsedAt. The account UI currently consumes neither field, so no rendered failure is established.
- POST /api/accounts/{id}/switch-plan always returns 501, while the frontend expects a plan and uses it before confirmation.
- There is no generated/shared schema or end-to-end contract gate proving frontend declarations match serialized .NET responses.

Evidence and wire-contract detail: [api-contracts.md](api-contracts.md).

## Configuration and polling

- Router configuration is held in NativeAutoRouter process memory and returns to defaults after restart.
- TelemetryPollingCoordinator fixes its interval at construction and is the loop started by App.xaml.cs.
- Updating RouterConfigDto.PollingIntervalMs does not change that active telemetry timer. NativeAutoRouter has a separate configurable periodic loop, but the shipped composition does not call NativeAutoRouter.Start.
- The dashboard refreshes configuration about every six seconds, and the config form mirrors refreshed props without a dirty-state guard; unsaved edits can be overwritten. This is UI concurrency/UX behavior, not persistence corruption.

Evidence and lifecycle detail: [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Switch transaction continuity

- Switch status is in memory, and WinCred mutation plus metadata finalization are separate durable operations. Abrupt termination between them can leave cross-store state out of sync; startup has no durable transaction journal/reconciliation step.
- Cancellation before process transition maps to CANCELLED/408. After transition begins it enters rollback, including a late abort after target verification, which can return the rolled-back failure path instead of 408.

Transaction boundaries and careful wording: [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Loopback authorization

- Mutation Origin validation accepts a missing Origin header.
- Sec-Fetch-Site rejection is present for selected account mutations, not uniformly for every mutation.
- Only explicit switching currently requires the additional per-process switch-intent token.
- Remote-network exposure is blocked, but same-user non-browser local processes are not generally authenticated. Cross-Windows-user/session loopback reachability is unverified by repository evidence.

Loopback binding reduces exposure but is not user authentication. See [security-and-trust-model.md](security-and-trust-model.md).

## Documentation drift

- docs/architecture.md describes an earlier Node foundation and staged native work rather than the shipped topology.
- docs/dotnet-migration.md is migration-era history and includes obsolete staging language.
- docs/security-model.md says WinCred access is strictly read-only, while the native switch coordinator performs controlled writes and conditional rollback through WindowsWinCredWriter.
- docs/security.md and docs/security-model.md overlap. This foundation leaves both unchanged to preserve history; security-and-trust-model.md is the current subject owner.

## Generated UI freshness

- frontend/ is authored source and Vite writes committed assets to src/ui.
- The .NET project packages src/ui as wwwroot.
- Current CI builds these assets but has no explicit clean-rebuild plus repository-diff gate, so stale committed generated output is possible.
- scripts/package-release.ps1 does not build the frontend, so standalone packaging requires fresh src/ui first. The GitHub release workflow does build it before packaging; this gap does not prove released v0.2.0 artifacts were stale.

Evidence and artifact flow: [build-and-release.md](build-and-release.md).

## Native and live validation

- Default tests rely heavily on fakes, synthetic processes, temporary directories, and source/package inspection.
- The normal .NET suite is not fully isolated: DashboardLifecycleTests.JsRuntimeDiagnostics_RecordsAndClearsErrors can write the production default js_runtime_errors.json path.
- Frontend automation has static/type/build and helper/reference tests, but no authored Svelte mounting/render interaction tests and no browser/E2E suite.
- AG2 live telemetry validation is opt-in through AG2_LIVE_TEST and therefore is not ordinary CI evidence.
- Canonical live WinCred reading in the TypeScript suite is separately opt-in.
- Upgrade preservation tests deliberately avoid real user data, live WinCred, user registry state, installed application state, and production vaults.
- WebView2 coordination tests exercise the coordinator abstraction but cannot cover every installed runtime/version interaction.
- Release workflow artifacts are not proven Authenticode-signed or published by the workflow.

These are fidelity/isolation boundaries, not permission to run live tests or the known unsafe diagnostics test. See [testing.md](testing.md).
