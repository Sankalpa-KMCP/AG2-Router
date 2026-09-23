# Testing

This document owns test layers, fidelity boundaries, fixtures, live opt-ins, and protected-resource isolation. Expected domain behavior remains in [domain-rules.md](domain-rules.md), and build/release commands remain in [build-and-release.md](build-and-release.md).

## Test strata

| Layer | Location | Primary purpose | Does not prove |
| --- | --- | --- | --- |
| Node/TypeScript | test/ | Reference implementation behavior, cross-platform server/domain cases, selected Windows adapters | That the shipped .NET composition has identical behavior |
| .NET unit/integration | dotnet/tests/AG2Router.Tests | Native DTOs, normalization, routing, stores, vault, switching, loopback host, lifecycle, Windows abstractions | Full behavior against a user's installed/live environment |
| Frontend static checks | svelte-check, TypeScript, and Vite build | Svelte/type/build correctness against declared frontend types | Mounted component behavior, browser interaction, or runtime agreement with backend JSON |
| Build/package tests | ReleasePackagingTests and UpgradePreservationTests | Script/installer text contracts, staged layout, synthetic upgrade preservation | Execution of every installer path on a clean production-like Windows image |
| Opt-in live integration | AG2LiveParityTests and selected WinCred tests | Narrow compatibility checks against a deliberately supplied live environment | Safe default CI coverage or broad production certification |

CI definitions under .github/workflows/ show which layers are actually enforced. A test existing in the tree is not proof that every workflow runs it under every platform.

## Default isolation policy

INTENDED INVARIANT: default tests must not access or mutate:

- an installed AG2 Router instance;
- live Antigravity processes or RPC ports;
- the real canonical WinCred entry;
- production DPAPI/vault/account data;
- %LOCALAPPDATA%/AG2-Router;
- user registry state;
- transcripts, brain data, or production services.

Use task-owned temporary directories, InMemoryAccountStore, InMemoryWinCredStore, FakeDpapiProvider, fake registry accessors, fake process inspectors/lifecycles, and synthetic RPC handlers.

WindowsDpapiProvider round-trip tests may use synthetic bytes in an isolated temporary directory. That exercises the API under the current user but must not read production ciphertext or user vault paths.

UpgradePreservationTests explicitly redirect data and use synthetic/fake inputs. Preserve that isolation when adding lanes.

Normal repository .NET tests use synthetic in-memory sinks or task-owned temporary paths for JS runtime diagnostics, avoiding writes to %LOCALAPPDATA%/AG2-Router. When adding or expanding tests, ensure diagnostic logging continues to inject isolated destinations rather than resolving the production per-user path.

## Live opt-ins

- .NET AG2LiveParityTests are skipped unless AG2_LIVE_TEST=1.
- The TypeScript canonical WinCred read is skipped unless AG2_RUN_LIVE_WINCRED_TESTS=true on Windows.

These variables are explicit authorization gates. Do not set them automatically in default scripts, CI, documentation validation, or an agent run. Even when enabled, tests should remain read-only unless a separate test and user authorization explicitly permit mutation.

## Evidence by contract

- Normalization and canonical quota behavior: AG2TelemetryNormalizerTests and test/ag2-normalizer.test.ts.
- Candidate selection and safety: CandidateSelectorTests, RoutingSafetyGateTests, NativeAutoRouterTests, selector/safety TypeScript tests.
- Enrollment and persistence: AccountEnrollmentServiceTests, AccountRemovalServiceTests, AccountStoreTests, SessionVaultTests, and matching TypeScript tests.
- Switching and process integrity: NativeAccountSwitchCoordinatorTests, WindowsAG2ProcessLifecycleTests, ProcessProvenanceValidatorTests, and TypeScript switch tests.
- Loopback/API: LoopbackServerTests, LoopbackServerAccountApiTests, LoopbackSwitchApiTests, and LoopbackRouterApiTests.
- Polling/WebView lifecycle: TelemetryPollingCoordinatorTests, DashboardLifecycleTests, WebView2EnvironmentCoordinatorTests, and WebViewRecoveryPolicyTests.
- Packaging and upgrades: ReleasePackagingTests and UpgradePreservationTests, including disposable staged replacement/interruption checks. These do not execute the production installer.

Tests should reference the subject contract rather than duplicate prose explanations in their names or setup comments.

## Fidelity rules

- Prefer behavioral assertions over assertions that only search source text. Text inspection is useful for installer/package policy but cannot prove runtime execution.
- Use golden JSON only for intentional wire or persistence schemas; review golden changes as contract changes.
- Race tests must coordinate deterministically with barriers/task completions rather than timing sleeps where possible.
- Fault-injection tests should identify the mutation boundary being interrupted and verify the prior durable snapshot remains valid.
- A mock-based test proves orchestration against that mock contract, not real OS behavior.
- An opt-in live test proves only the observed environment and version. Record durable compatibility requirements in source/tests, not the machine state used for one run.

## Contract gaps

Frontend types are checked internally but not generated from backend serialization; compilation of both halves remains insufficient proof of a full browser-to-loopback contract. Negative quota reset-window and unknown-state tests cover the normalizers and dashboard helper, and request-order/mutation-fence tests cover the helper protocol, but authored Svelte rendering is not mounted in CI.

## Frontend interaction fidelity

CURRENT IMPLEMENTATION has Svelte/TypeScript static checks and a Vite build. test/frontend-dashboard.test.ts exercises reference/helper functions imported from src/dashboard/helpers.ts.

There are no authored tests that mount or render the Svelte components under frontend/src/, and there is no browser or end-to-end suite. The current checks therefore do not prove form editing, modal behavior, component reactivity, periodic refresh interaction, WebView2 rendering, or a full dashboard-to-loopback workflow.

## Typical validation

From the repository root:

    npm ci
    npm run typecheck
    npm run build
    npm test
    dotnet test dotnet/AG2Router.sln -c Release

Use focused projects/tests during iteration, then only the applicable safely isolated validation before handoff. Normal repository .NET tests may be run through the documented default command based on the current isolated test design; explicitly opt-in live tests continue to require separate authorization. Packaging is Windows-specific and is described in [build-and-release.md](build-and-release.md).
