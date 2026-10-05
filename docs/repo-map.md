# Repository map

Navigation aid: answer "where do I look for X?" fast. Orientation context is in [architecture.md](architecture.md); rules changes must preserve are consolidated in [invariants.md](invariants.md). Subject documents own their domains; this map points to them. Paths are repository-relative.

## Top-level

| Path | Purpose | Status |
| --- | --- | --- |
| AGENTS.md | Agent routing index and repository-wide safety policy | Authoritative entry point |
| README.md | Human-facing product overview, install/usage, project status | Current |
| SECURITY.md | Security reporting policy and scope (GitHub private vulnerability reporting, verified active) | Current |
| CONTRIBUTING.md | Contribution guidance; defers to AGENTS.md and the governance gates | Current |
| LICENSE | MIT license for the repository (ADR-004) | Authoritative grant |
| dotnet/ | Shipped .NET 10 Windows application and its tests | Authoritative (shipped behavior) |
| frontend/ | Authored Svelte 5 + TypeScript dashboard source | Authoritative (UI source) |
| src/ | Node/TypeScript reference implementation; contains generated `src/ui` | Reference + generated output |
| src/ui/ | Generated dashboard bundle consumed by .NET and the Node server | **Generated — never hand-edit** |
| test/ | Node/TypeScript reference test suite | Reference/CI oracle |
| scripts/ | Build, packaging, install/uninstall automation | Authoritative for its purpose |
| installer/AG2Router.iss | Inno Setup installer definition | Authoritative |
| .github/ | ci.yml, dotnet-ci.yml, release.yml workflows; issue templates (bug report, feature request); pull-request template | Enforced CI/release entry points and community templates |
| docs/ | Durable documentation (subject owners + this understanding layer) | See AGENTS.md table |
| dist/, node_modules/, publish/, graphify-out/ | Local build output / dependencies / derived graph | Ignored; never commit |
| dotnet/Directory.Build.props | Canonical version + shared compile settings | Authoritative version source |
| package.json / package-lock.json | Node toolchain, scripts, UI build; version mirrors releases | Toolchain authority |

Historical, not current: docs/release-notes-*.md (immutable per release) and docs/decisions/ (decision rationale).

## Native/.NET (dotnet/)

Solution: dotnet/AG2Router.sln (plus AG2Router.slnx). `TreatWarningsAsErrors` is on via Directory.Build.props.

| Project | Responsibility | Notable entry points | Tests |
| --- | --- | --- | --- |
| dotnet/src/AG2Router.Core | DTOs, domain records, interfaces, validation — no Win32/WPF/HTTP/persistence | Models/AppStatus.cs, AccountMetadata.cs, AccountModels.cs, RoutingModels.cs, SwitchModels.cs, UsageModels.cs, UsageAggregationModels.cs, VaultModels.cs; Contracts/; Validation/RouterConfigValidator.cs, AccountTextValidator.cs | CoreModelTests and friends |
| dotnet/src/AG2Router.AG2 | Antigravity domain: discovery, RPC, normalization, accounts, vault, routing, switching, usage | Discovery/AG2ProcessDetector.cs, ProcessProvenanceValidator.cs, WindowsProcessInspector.cs; Rpc/AG2RpcClient.cs; Normalization/AG2TelemetryNormalizer.cs; Adapter/AG2LiveAdapter.cs; Accounts/AccountEnrollmentService.cs, LocalMetadataAccountStore.cs; Vault/SessionVault.cs; Routing/NativeAutoRouter.cs, CandidateSelector.cs, RoutingSafetyGate.cs; Switching/NativeAccountSwitchCoordinator.cs, SwitchJournalStore.cs; Usage/UsageCollectionService.cs, UsageInstanceDiscovery.cs, UsageAggregationService.cs; Persistence/DurableFileWriter.cs (incl. PathLockRegistry + CrossProcessFileLease), DurableQuotaObservationStore.cs, UsageCallLedger.cs | Focused suites per area in dotnet/tests/AG2Router.Tests |
| dotnet/src/AG2Router.Windows | Win32 interop: WinCred, DPAPI, registry autostart, single-instance, taskbar | Security/WindowsWinCredReader.cs, WindowsWinCredWriter.cs, WindowsDpapiProvider.cs; Lifecycle/SingleInstanceGuard.cs, WindowsRegistryAutostartService.cs | WinCred/DPAPI/single-instance tests |
| dotnet/src/AG2Router.App | Shipped WPF host: composition root, tray, polling, loopback server, WebView2 | App.xaml.cs (composition root); Server/LoopbackServer.cs; Services/TelemetryPollingCoordinator.cs, WebView2EnvironmentCoordinator.cs; Lifecycle/DashboardLifecycleManager.cs; Tray/TrayIconManager.cs; Views/MainWindow, QuickStatusWindow; Diagnostics/JsRuntimeDiagnostics.cs | Loopback/polling/lifecycle tests |
| dotnet/tests/AG2Router.Tests | Native unit/integration/packaging tests (xUnit) | Fixtures/ incl. synthetic installation environment | whole project |

## Frontend

| Area | Path |
| --- | --- |
| App shell | frontend/src/App.svelte, frontend/src/main.ts |
| API contract client | frontend/src/lib/api/types.ts, client.ts (wire counterpart of Core DTOs) |
| Components | frontend/src/lib/components/*.svelte (accounts, quota, routing settings, recovery modal, usage, modals) |
| Helpers/utilities | frontend/src/lib/utils/*.ts (helpers, routing, switching, recovery, usage, usageRequests, account-mutations, providerQuota) |
| Styles | frontend/src/lib/styles/ |
| Build config | frontend/vite.config.ts → outputs to src/ui (clears it first) |

UI changes: edit frontend/, run `npm run build:ui`, never patch src/ui directly. Frontend behavior tests live in test/frontend-*.test.ts.

## Node/TypeScript reference

- src/: accounts/, ag2/, config/, persistence/, router/, server/ (server.ts), switching/, vault/ (session-vault.ts, dpapi.ts), dashboard/ (compat re-export of frontend helpers), index.ts (entry).
- test/: *.test.ts run by scripts/run-tests.mjs with Node's built-in test runner; requires `npm run build` first (tests execute from dist/test).
- src/ui is copied into dist/src/ui by scripts/copy-ui.mjs for the Node server.
- These suites remain CI inputs and parity evidence; they do not define shipped .NET behavior (see [runtime-architecture.md](runtime-architecture.md), "TypeScript status").

## Persistence

- Contracts and primitives: dotnet/src/AG2Router.AG2/Persistence/DurableFileWriter.cs (atomic writes, PathLockRegistry, CrossProcessFileLease); Core/Contracts/ISecurityStorage.cs, IQuotaObservationStore.cs.
- Stores: Accounts/LocalMetadataAccountStore.cs (accounts.json), Vault/SessionVault.cs (vault/sessions.dat), Routing + Switching share Persistence/DurableQuotaObservationStore.cs, Switching/SwitchJournalStore.cs, Persistence/UsageCallLedger.cs, Persistence/UsageCollectorStateStore.cs.
- Durable layout and lock order: [persistence-and-concurrency.md](persistence-and-concurrency.md).
- Tests: AccountStoreTests, SessionVaultTests, DurableQuotaObservationStoreTests, SwitchJournalStoreTests, and related suites.

## Usage

- Domain + collection: dotnet/src/AG2Router.AG2/Usage/ (UsageCollectionService.cs, UsageInstanceDiscovery.cs, UsageAggregationService.cs); Persistence/UsageCallLedger.cs, UsageCollectorStateStore.cs; Core/Contracts/IUsageCallLedger.cs, Models/UsageModels.cs, UsageAggregationModels.cs.
- API: LoopbackServer `/api/usage/*` routes; frontend/src/lib/utils/usage.ts, usageRequests.ts; frontend/src/lib/components/UsageSection.svelte.
- Semantics: [usage-accounting.md](usage-accounting.md). Tests: UsageCollectorTests, UsageAggregationServiceTests, LoopbackUsageApiTests, UsageInstanceDiscoveryTests, test/frontend-usage.test.ts, test/usage-node-unsupported.test.ts.

## Installer/release

| Concern | Location |
| --- | --- |
| Canonical version | dotnet/Directory.Build.props `Version` (release workflow + installer read it; package.json mirrors it) |
| Release packaging | scripts/package-release.ps1 (rebuilds UI, publishes win-x64, ZIP + SHA256SUMS, optional ISCC compile, payload inspection) |
| Inno installer | installer/AG2Router.iss; scripts/provision-inno.ps1 pins Inno Setup 6.4.0 in CI |
| Install/upgrade/uninstall | scripts/install.ps1, scripts/uninstall.ps1 (stopped-state fail-closed, ownership classification) |
| Workflows | .github/workflows/ci.yml (Node matrix), dotnet-ci.yml (.NET + installer compile gate), release.yml (tag-triggered packaging) |
| Packaging tests | ReleasePackagingTests, UpgradePreservationTests |

## Generated files

| Generated path | Authoritative source | Regeneration mechanism | Hand-edit? |
| --- | --- | --- | --- |
| src/ui/ | frontend/ (frontend/vite.config.ts) | `npm run build:ui` (also run by `npm run build` and package-release.ps1) | No |
| wwwroot (build/publish output) | src/ui via AG2Router.App.csproj content link | `dotnet build` / `dotnet publish` | No |
| dist/ (incl. dist/src/ui) | src + src/ui | `npm run build` (tsc + scripts/copy-ui.mjs) | No; git-ignored |
| graphify-out/ | Derived analysis graph | Graphify CLI (currently prohibited; probes protected state) | Never commit |

## Common task → location

| Task | Start here |
| --- | --- |
| Change account routing / candidate selection | docs/domain-rules.md; dotnet/src/AG2Router.AG2/Routing/; NativeAutoRouterTests, CandidateSelectorTests |
| Change quota normalization | docs/domain-rules.md; AG2TelemetryNormalizer.cs (+ src/ag2/normalizer.ts for parity) |
| Change switch transaction / recovery | docs/persistence-and-concurrency.md; Switching/NativeAccountSwitchCoordinator.cs, SwitchJournalStore.cs |
| Change enrollment | docs/persistence-and-concurrency.md; Accounts/AccountEnrollmentService.cs |
| Change vault / DPAPI / WinCred | docs/security-and-trust-model.md; Vault/SessionVault.cs; Windows/Security/ |
| Change a loopback endpoint / DTO | docs/api-contracts.md; App/Server/LoopbackServer.cs, Core Models, frontend/src/lib/api/ |
| Change dashboard UI | frontend/ → `npm run build:ui`; docs/api-contracts.md for wire fields |
| Change usage accounting | docs/usage-accounting.md; AG2/Usage/, Persistence/UsageCallLedger.cs |
| Change persistence format | docs/persistence-and-concurrency.md; Persistence/ + the store's tests |
| Change process discovery / provenance | docs/security-and-trust-model.md; Discovery/ |
| Change installer / packaging | docs/build-and-release.md; scripts/package-release.ps1, installer/AG2Router.iss, scripts/install.ps1 |
| Change version | dotnet/Directory.Build.props (single source); mirror to package.json at release time |
| Change release CI | docs/build-and-release.md; .github/workflows/ |
| Add/change tests | docs/testing.md first (isolation + live-gate rules) |
