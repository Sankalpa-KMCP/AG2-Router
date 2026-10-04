# ADR-002: Adopt native .NET/WPF runtime instead of Electron or a Node daemon

Status: Accepted (recorded retrospectively from the completed migration; implementation complete)
Date: 2026-10-04 (decision made and executed during the dotnet migration, before this record existed)

## Context

The repository began as a Node.js/TypeScript daemon for Antigravity quota monitoring. Shipping that as the product would have required either a heavyweight desktop shell or a background daemon without native Windows integration. The credential and switching features require Windows Credential Manager, DPAPI, process control, and tray integration.

## Decision

Ship the product as a self-contained .NET 10 WPF application (`dotnet/src/AG2Router.App`):

- In-process ASP.NET Core (Kestrel) loopback server on an ephemeral `127.0.0.1` port — no separate web-server daemon.
- WebView2 (Evergreen runtime) hosts the Svelte dashboard; no bundled Chromium.
- Direct Win32 interop (Advapi32/Crypt32) for WinCred and DPAPI; WPF tray and single-instance primitives.

The Node/TypeScript implementation under `src/` is preserved as a behavioral reference and CI parity oracle, not as shipped code.

## Rationale

- No heavyweight shell: Electron-style packaging was rejected for installer/memory bloat and bundled runtimes; WebView2 reuses the OS-maintained Evergreen runtime.
- Native security integration: WinCred, DPAPI, and process lifecycle work via supported Windows APIs without interop shims or subprocess piping.
- Single process, single runtime: routing, persistence, HTTP hosting, and UI hosting compose in one executable, which keeps the lock/lease and lifecycle model coherent.

## Alternatives considered

- Electron or equivalent shell over the Node daemon: rejected for resource overhead and duplicated runtimes.
- Continuing with the Node daemon as the product: rejected for lack of native tray/WinCred/DPAPI integration and a weaker single-instance story.
- Separate web-server process serving the dashboard: rejected; in-process Kestrel keeps trust and lifecycle boundaries inside one auditable host.

## Consequences

- Positive: compact self-contained win-x64 payload, native OS integration, one composition root ([runtime-architecture.md](../runtime-architecture.md)).
- Negative: Windows-only host; the repository carries two implementations (.NET shipped, Node reference), which requires the parity discipline documented in [runtime-architecture.md](../runtime-architecture.md) ("TypeScript status").

## Compatibility/migration impact

The staged migration is complete; no migration plan remains operational. Node behavior is not assumed to exist in the native application without corresponding .NET source, composition, and tests.

## Related

- [runtime-architecture.md](../runtime-architecture.md), [build-and-release.md](../build-and-release.md)
- Historical source: the former `docs/dotnet-migration.md` (deleted when this record was created); its durable rationale is preserved here.
