# Runtime architecture

This document owns the current shipped runtime topology and component boundaries. Domain semantics belong in [domain-rules.md](domain-rules.md); wire shapes belong in [api-contracts.md](api-contracts.md); synchronization details belong in [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Shipped application

AG2 Router is a per-user Windows desktop application built on .NET 10 and WPF (release candidate 0.4.1-rc.4). App.xaml.cs composes the runtime:

1. SingleInstanceGuard establishes the primary process and named-pipe command path.
2. AG2ProcessDetector and AG2LiveAdapter provide the Antigravity boundary.
3. WindowsDpapiProvider, SessionVault, LocalMetadataAccountStore, and WinCred reader/writer provide account state and credential facilities.
4. AccountEnrollmentService, NativeAccountSwitchCoordinator, and NativeAutoRouter provide enrollment and routing behavior.
5. TelemetryPollingCoordinator produces the status snapshot consumed by the tray and loopback server.
6. LoopbackServer binds an ephemeral loopback port and serves API responses and dashboard assets.
7. DashboardLifecycleManager creates MainWindow lazily; WebView2 loads the loopback dashboard.
8. TrayIconManager and QuickStatusWindow provide tray-first interaction and explicit shutdown.

The composition root is dotnet/src/AG2Router.App/App.xaml.cs. A service existing elsewhere does not prove that the shipped composition starts or uses it; confirm wiring here.

The host creates SwitchJournalStore and a shared DurableQuotaObservationStore beside accounts.json, and loads router configuration from sibling config.json. It reconciles the journal before starting routing, polling, and the loopback dashboard. AG2LiveAdapter receives a provider for the router's configured WorkloadModelKey; it does not discover IDE dropdown selection. The router and real switch coordinator share durable candidate evidence authority.

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

Automatic routing follows configured model intent → live active requested-model quota → durable candidate observations → eligibility/ranking → recovery/activity/identity/process admission → target switch and identity verification → fresh live target quota verification → metadata commit or verified rollback. The router revalidates the selected durable observation through admission; the coordinator verifies target quota before commit and conditionally invalidates disproven cached evidence. Manual switching uses the same coordinator without automatic workload/quota requirements. Detailed policy and transaction ownership remain in the linked domain and persistence documents.

### Dashboard

The Svelte build produces src/ui. The .NET application project links those files into wwwroot at build/publish time. LoopbackServer serves wwwroot, and MainWindow navigates WebView2 to the bound ephemeral address.

DashboardLifecycleManager avoids creating WebView2 until the dashboard is opened. WebView2EnvironmentCoordinator serializes environment creation and handles closing/reopening coordination. Detailed synchronization is in [persistence-and-concurrency.md](persistence-and-concurrency.md).

Settings exposes all routing fields, including Workload Model. Telemetry-derived raw model-key suggestions supplement explicit entry; configuration saves preserve workload intent. The dashboard reads backend candidate-evidence status and switching/recovery status rather than inferring candidate health from displayed active quota. These endpoints do not query inactive accounts upstream. Wire fields and unavailable-state behavior are defined in [api-contracts.md](api-contracts.md).

### Enrollment and switching

Enrollment reads the observed account identity and current credential, protects a session in the vault, updates metadata, and compensates guardedly on failure. Switching validates the target, workload state, and process provenance before a controlled credential/process transition and post-restart identity verification.

Security properties are in [security-and-trust-model.md](security-and-trust-model.md). Transaction mechanics are in [persistence-and-concurrency.md](persistence-and-concurrency.md).

### Process lifecycle

The application is tray-first and uses explicit shutdown. Secondary invocations signal the primary instance. The loopback server and polling coordinator are stopped during application exit, and the dashboard/WebView2 environment has its own lazy creation and close lifecycle.

Telemetry polling shutdown closes snapshot publication and cancels admitted work. StopAsync uses the caller's wait budget and retains ownership after an expired wait. DisposeAsync waits at most two seconds; cancellation-ignoring work is drained by an owned completion task before its synchronization and cancellation resources are disposed. This includes explicitly requested PollAsync calls as well as the periodic loop. Telemetry failures retain their error indication while the router projection comes from the current AutoRouter status. Behavioral coverage is in TelemetryPollingCoordinatorTests; configuration-generation and latest-wins checks remain in the polling test suites.

### Structured Diagnostic Logging

AG2 Router persists asynchronous, bounded, schema v1 diagnostic event streams to `%LOCALAPPDATA%\AG2-Router\diagnostics\owned-segments-v1\diag_*.jsonl`. The subsystem consists of:

- `AG2Router.Core.Diagnostics`: Closed Schema v1 record `DiagnosticEvent` (8 KiB maximum serialized size), allowlisted fields only, typed event catalog (`DiagnosticEventCatalog`), pre-ingestion privacy sanitizer with ReDoS timeouts (`DiagnosticSanitizer`), segment header model (`DiagnosticSegmentHeader`), memory buffer sink (`MemoryDiagnosticSink`), and universal sink interface (`IDiagnosticSink`).
- `AG2Router.App.Diagnostics`: `BoundedJsonlDiagnosticSink` backed by a bounded channel queue (capacity 512, 4 MiB memory bound) with admission reservation and rollback. Segment rotation occurs at 4 MiB (`diag_YYYYMMDD_HHMMSS_seq.jsonl`) or age > 14 days, with a maximum of 8 owned segments, a 32 MiB total size bound, and a 14-day retention window. Every line in the `.jsonl` file is valid parseable JSON: line 1 is a valid JSON `DiagnosticSegmentHeader` record flushed immediately upon creation; all subsequent lines are valid JSON `DiagnosticEvent` records. Retention sweeps verify the strict filename regex, absence of reparse points, and the parsed JSON segment header; safe owned-file deletion couples non-destructive ownership validation directly to the same stream handle used for deletion, opening without DeleteOnClose and rejecting unverified/foreign candidates before deletion. Foreign and imitation files are preserved untouched; failed deletions increment `RetentionFailureCount` and maintain file and byte accounting. An in-memory ring buffer (256 items, 2 MiB memory bound) provides low-overhead operational queries. `FlushAsync` propagates truthful disk persistence, returning false on memory-only sinks or persistence faults. Shutdown flush is bounded by a 2-second timeout budget with exclusive worker stream and lease cleanup in `finally`.
- Multi-instance safety & pre-admission buffering: Early startup and secondary instances buffer events in a bounded in-memory `MemoryDiagnosticSink`. Promotion to the persistent disk sink and acquisition of the exclusive `.active_writer.lock` (`FileShare.None`) occurs strictly after `SingleInstanceGuard` acquisition. Restart ownership transfer relinquishes writer lease before single-instance guard release, with bounded retry on primary admission. Secondary instances degrade gracefully to memory-only ring-buffer mode (`IsWriterLeaseHeld = false`) without touching disk files or interfering with the primary writer.
- Subsystem wiring: Application bootstrap/shutdown (`App.xaml.cs`), WebView2 lifecycle and recovery (`MainWindow.xaml.cs`, `WebView2EnvironmentCoordinator.cs`, `JsRuntimeDiagnostics.cs`), Antigravity process discovery and Connect-RPC probe classification (`AG2ProcessDetector.cs`, `UsageInstanceDiscovery.cs`), and telemetry/usage service degradation/recovery (`TelemetryPollingCoordinator.cs`, `UsageCollectionService.cs`). Production legacy store initialization is bound to `App.OnStartup`; test isolation is guaranteed by `AssertNotProductionAppData` and in-memory fallbacks.
- Rationale and boundaries are recorded in [ADR-008](decisions/ADR-008-structured-diagnostic-logging.md).

## TypeScript status

The Node/TypeScript implementation under src/ and its tests under test/ remain build and CI inputs. They are useful reference and parity evidence, but the released Windows executable is composed from dotnet/src/ and the built frontend assets. A TypeScript behavior must not be assumed to exist in the native application without corresponding .NET source, composition, and tests.

docs/architecture.md is the orientation-level overview of the current system; this document remains the owner of runtime topology and component boundaries. Decision rationale lives in docs/decisions/. Neither the overview nor decision records supersede this current topology or executable evidence.

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
