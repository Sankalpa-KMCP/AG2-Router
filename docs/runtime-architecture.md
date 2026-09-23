# Runtime architecture

This document owns the current shipped runtime topology and component boundaries. Domain semantics belong in [domain-rules.md](domain-rules.md); wire shapes belong in [api-contracts.md](api-contracts.md); synchronization details belong in [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Shipped application

AG2 Router v0.2.1 is a per-user Windows desktop application built on .NET 10 and WPF. App.xaml.cs composes the runtime:

1. SingleInstanceGuard establishes the primary process and named-pipe command path.
2. AG2ProcessDetector and AG2LiveAdapter provide the Antigravity boundary.
3. WindowsDpapiProvider, SessionVault, LocalMetadataAccountStore, and WinCred reader/writer provide account state and credential facilities.
4. AccountEnrollmentService, NativeAccountSwitchCoordinator, and NativeAutoRouter provide enrollment and routing behavior.
5. TelemetryPollingCoordinator produces the status snapshot consumed by the tray and loopback server.
6. LoopbackServer binds an ephemeral loopback port and serves API responses and dashboard assets.
7. DashboardLifecycleManager creates MainWindow lazily; WebView2 loads the loopback dashboard.
8. TrayIconManager and QuickStatusWindow provide tray-first interaction and explicit shutdown.

The composition root is dotnet/src/AG2Router.App/App.xaml.cs. A service existing elsewhere does not prove that the shipped composition starts or uses it; confirm wiring here.

## Project boundaries

| Project/path | Responsibility | Must not own |
| --- | --- | --- |
| dotnet/src/AG2Router.Core | DTOs, domain records, interfaces | Win32, WPF, HTTP hosting, concrete persistence |
| dotnet/src/AG2Router.AG2 | Discovery, RPC, normalization, account store, vault, router, switch transaction | WPF presentation and direct application composition |
| dotnet/src/AG2Router.Windows | WinCred, DPAPI, registry, taskbar, single-instance primitives | Domain routing policy |
| dotnet/src/AG2Router.App | Composition, WPF/tray, polling, loopback HTTP, WebView2 | Duplicated domain or storage implementations |
| frontend | Authored Svelte UI and API client | Server-side authority or secret handling |
| src/ui | Generated deployable dashboard assets | Hand-authored business logic |

References: dotnet/AG2Router.sln, project files under dotnet/src/, and dotnet/src/AG2Router.App/App.xaml.cs.

## Runtime flows

### Telemetry

TelemetryPollingCoordinator periodically calls IAG2Adapter. AG2LiveAdapter uses discovery and Connect-RPC, while AG2TelemetryNormalizer maps upstream responses into Core DTOs. The coordinator rejects older sequence results and publishes a SystemStatusDto snapshot to the tray and loopback API.

The coordinator also invokes NativeAutoRouter.EvaluateCycleAsync. Router policy and quota meaning are defined in [domain-rules.md](domain-rules.md).

### Dashboard

The Svelte build produces src/ui. The .NET application project links those files into wwwroot at build/publish time. LoopbackServer serves wwwroot, and MainWindow navigates WebView2 to the bound ephemeral address.

DashboardLifecycleManager avoids creating WebView2 until the dashboard is opened. WebView2EnvironmentCoordinator serializes environment creation and handles closing/reopening coordination. Detailed synchronization is in [persistence-and-concurrency.md](persistence-and-concurrency.md).

### Enrollment and switching

Enrollment reads the observed account identity and current credential, protects a session in the vault, updates metadata, and compensates guardedly on failure. Switching validates the target, workload state, and process provenance before a controlled credential/process transition and post-restart identity verification.

Security properties are in [security-and-trust-model.md](security-and-trust-model.md). Transaction mechanics are in [persistence-and-concurrency.md](persistence-and-concurrency.md).

### Process lifecycle

The application is tray-first and uses explicit shutdown. Secondary invocations signal the primary instance. The loopback server and polling coordinator are stopped during application exit, and the dashboard/WebView2 environment has its own lazy creation and close lifecycle.

## TypeScript status

The Node/TypeScript implementation under src/ and its tests under test/ remain build and CI inputs. They are useful reference and parity evidence, but the released Windows executable is composed from dotnet/src/ and the built frontend assets. A TypeScript behavior must not be assumed to exist in the native application without corresponding .NET source, composition, and tests.

Historical documents such as architecture.md and dotnet-migration.md describe earlier stages. They do not supersede this current topology or executable evidence.

## Evidence map

- Composition and lifecycle: dotnet/src/AG2Router.App/App.xaml.cs
- Polling: dotnet/src/AG2Router.App/Services/TelemetryPollingCoordinator.cs
- Loopback host: dotnet/src/AG2Router.App/Server/LoopbackServer.cs
- Dashboard lifecycle: dotnet/src/AG2Router.App/Lifecycle/ and Views/
- WebView2 coordination: dotnet/src/AG2Router.App/Services/WebView2EnvironmentCoordinator.cs
- AG2 boundary: dotnet/src/AG2Router.AG2/Adapter, Discovery, Rpc, and Normalization
- Routing/switching: dotnet/src/AG2Router.AG2/Routing and Switching
- Native integration: dotnet/src/AG2Router.Windows
- Authored UI: frontend/src/
- Generated UI: src/ui/
