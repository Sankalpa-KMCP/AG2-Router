# AG2 Router

A native Windows desktop app that protects your Antigravity workload: it watches per-model quota, switches accounts safely when the active one runs low, and keeps private conversation-usage accounting — entirely on your machine.

[![Latest release](https://img.shields.io/github/v/release/Sankalpa-KMCP/AG2-Router)](https://github.com/Sankalpa-KMCP/AG2-Router/releases/latest)
[![CI](https://github.com/Sankalpa-KMCP/AG2-Router/actions/workflows/ci.yml/badge.svg)](https://github.com/Sankalpa-KMCP/AG2-Router/actions/workflows/ci.yml)
[![.NET Windows CI](https://github.com/Sankalpa-KMCP/AG2-Router/actions/workflows/dotnet-ci.yml/badge.svg)](https://github.com/Sankalpa-KMCP/AG2-Router/actions/workflows/dotnet-ci.yml)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-blue)](#install)

**[Download the latest stable release](https://github.com/Sankalpa-KMCP/AG2-Router/releases/latest)** · [All releases](https://github.com/Sankalpa-KMCP/AG2-Router/releases) · [Architecture](docs/architecture.md)

## Features

- **Quota-aware automatic routing** — protects one explicitly configured workload model. Switches only while Antigravity is verifiably idle, verifies the target account's identity and fresh live quota before committing, and fails closed on unknown or stale evidence.
- **Multi-account management** — enroll the running Antigravity session into an encrypted local vault (Windows DPAPI, `CurrentUser`); alias, remove, and switch accounts from the dashboard.
- **Conversation usage accounting** — a durable local ledger of per-call conversation tokens (input, output, thinking, cache reads) collected from every detected Antigravity instance, viewable per account, per model, and over time.
- **Crash-safe switching with real recovery** — a durable switch journal, quarantine, and proof-based operator recovery. Interrupted transitions are detected and gated at startup, never guessed.
- **Tray-first Windows experience** — a native tray icon with quick status; the dashboard runs in a WebView2 window served in-process over loopback.
- **Local-only by design** — per-user install, no cloud service. The only integration point is your local Antigravity installation.

## How it works

```
Windows (one instance per session)
  AG2 Router — WPF tray host
    |- Antigravity integration   process discovery, provenance checks, Connect-RPC over loopback
    |- Routing & switching       quota evaluation, verified account transitions, rollback journal
    |- Usage ledger              local conversation-token accounting
    |- Loopback API              in-process ASP.NET Core server on 127.0.0.1 (ephemeral port)
    \- Dashboard                 WebView2 window loading the Svelte UI
```

Component boundaries and data flow: [docs/architecture.md](docs/architecture.md). Routing semantics and safety gates: [docs/domain-rules.md](docs/domain-rules.md).

## Privacy and security

- **Loopback-only surface** — the API and dashboard bind to `127.0.0.1` on an ephemeral port; remote requests are rejected.
- **Credentials stay in Windows** — vaulted sessions are encrypted with DPAPI; the live Antigravity credential is handled through Windows Credential Manager. Credential material is kept out of logs and API responses by design, and release payloads are scanned for secret markers before publication.
- **No cloud service** — all state is stored locally under `%LOCALAPPDATA%\AG2-Router` and survives upgrades and uninstallation; AG2 Router's only network endpoint is your local Antigravity installation over loopback.
- **Privacy-minimal usage ledger** — token counters only. Prompts, responses, conversation identifiers, and account emails are never persisted.
- **Fail-closed defaults** — ambiguous identity, unsafe process provenance, or corrupted data blocks mutation instead of guessing.

One honest boundary: loopback binding is not authentication. Same-user local processes can reach the API, and account switching additionally requires a per-session intent token. The complete trust model and its limits are documented in [docs/security-and-trust-model.md](docs/security-and-trust-model.md) and [docs/known-limitations.md](docs/known-limitations.md).

## Install

Requirements: **Windows 10 (1809+) or Windows 11, 64-bit**, the [WebView2 Evergreen runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (preinstalled on Windows 11 and current Windows 10), and Antigravity installed.

**Stable release (recommended)** — download from [Releases → Latest](https://github.com/Sankalpa-KMCP/AG2-Router/releases/latest):

| Asset | Purpose |
| --- | --- |
| `AG2Router-Setup-<version>-win-x64.exe` | Per-user installer (Inno Setup). |
| `AG2Router-<version>-win-x64.zip` | Portable archive; extract and run `install.ps1`. |
| `SHA256SUMS.txt` | Checksum manifest to verify the download. |

Release binaries are currently unsigned, so SmartScreen may show a warning on first run; verify the checksums and proceed only if you trust the source. Uninstallation (Windows Settings or `uninstall.ps1`) always preserves your account data, vault, and Antigravity credential.

**Prereleases** — release candidates are published as clearly marked pre-releases on the [Releases page](https://github.com/Sankalpa-KMCP/AG2-Router/releases) for supervised testing. Packaging, upgrade, and uninstall safety boundaries: [docs/build-and-release.md](docs/build-and-release.md).

## Using AG2 Router

AG2 Router is tray-first: launch it and it lives in the notification area; open the dashboard from the tray. Command-line controls:

| Command | Behavior |
| :--- | :--- |
| `AG2Router.exe` | Launch into the system tray. |
| `AG2Router.exe --open` | Bring the dashboard to the foreground. |
| `AG2Router.exe --close` | Close the dashboard to the tray. |
| `AG2Router.exe --exit` | Graceful shutdown. |
| `AG2Router.exe --tray` | Explicit tray launch (used by Windows autostart). |

Windows autostart can be toggled from the dashboard Settings. Routing configuration lives there too: **Auto Switch**, **Workload Model**, **Low Quota Threshold**, **Minimum Candidate Quota**, and **Polling Interval**. Auto Switch requires an explicit Workload Model — AG2 Router routes to protect that model and does not guess your IDE selection; the routing rules are documented in [docs/domain-rules.md](docs/domain-rules.md).

## Development

Prerequisites: [.NET SDK 10.0.x (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) and Node.js 20.19+, 22.12+, or 24+ with npm.

```powershell
npm ci
npm run typecheck                            # TypeScript + Svelte checks
npm run build                                # dashboard UI + Node reference server
npm test                                     # Node reference test suite
dotnet test dotnet/AG2Router.sln -c Release  # native .NET test suite
```

Deeper reading: [code tour](docs/code-tour.md) · [repository map](docs/repo-map.md) · [testing](docs/testing.md) · [build and release](docs/build-and-release.md). Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) first; the repository's documentation and decision gates in [AGENTS.md](AGENTS.md) apply to every change.

## Project status

Active solo project, early stage. Stable releases and clearly marked release-candidate prereleases are published on the [Releases page](https://github.com/Sankalpa-KMCP/AG2-Router/releases). The project does not claim production certification; current evidence-backed limitations are maintained in [docs/known-limitations.md](docs/known-limitations.md).

## Security

Please do not open public issues for suspected vulnerabilities. Report them privately through GitHub Security → "Report a vulnerability" — see [SECURITY.md](SECURITY.md) for scope and reporting guidance.

## License

AG2 Router is licensed under the [MIT License](LICENSE).
