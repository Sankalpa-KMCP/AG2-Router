# AG2 Router

A lightweight, native Windows desktop application for Antigravity 2 that monitors active per-model quota and manages enrolled accounts. Automatic routing protects an explicitly configured workload model using live active quota, durable candidate observations, and verified account switching. Unknown or unsafe evidence fails closed; see [domain rules](docs/domain-rules.md) and [remaining limitations](docs/known-limitations.md).

---

## 1. What AG2 Router Is

AG2 Router is intended to reduce session and quota interruptions during heavy Antigravity agent workflows:

* **Real-Time Quota Telemetry:** Polls active Antigravity language server telemetry for per-model quota alongside informational prompt and flow credit metrics. Inactive accounts use expiring durable observations; they are not queried directly upstream.
* **Low-Quota Routing Policy:** Uses the configured workload model, live active quota, and conservative candidate evidence. Candidates rank by reserve status, usable quota, priority, and deterministic tie-breaking. Automatic switches verify target identity and fresh live requested-model quota before metadata commit, or attempt verified rollback.
* **Workload-Aware Safety Gating:** Strictly prevents account switching whenever Antigravity is active (`BUSY` or running trajectories $> 0$). Switches occur exclusively during verified `IDLE` states.
* **Per-User Encrypted Session Vault:** Stores account tokens with native Windows DPAPI (`DataProtectionScope.CurrentUser`), preventing plaintext credential exposure.
* **Tray-First Windows Desktop Architecture:** Runs quietly in the Windows notification area (system tray) with zero background window overhead and instant quick status tooltips.
* **In-Process Loopback Dashboard:** Embedded WebView2 dashboard served strictly over loopback (`127.0.0.1`), hardened with security headers (`X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`).

---

## 2. Architecture & Domain Boundaries

The application is built on .NET 10 LTS with modular domain boundaries:

```
dotnet/src/
├── AG2Router.Core/       # Canonical domain models, interfaces, and contracts
├── AG2Router.Windows/    # Native Win32 P/Invoke: WinCred, DPAPI, single-instance mutex & IPC, autostart registry
├── AG2Router.AG2/        # Antigravity adapter, Connect-RPC telemetry, session vault, auto-router, switch coordinator
└── AG2Router.App/        # WPF host, tray icon manager, in-process ASP.NET Core Kestrel loopback server, WebView2 dashboard
```

* **Process & Session Guard:** Single-instance execution enforced via `Local\AG2Router_Session_Mutex` and named-pipe IPC (`AG2Router_Session_IPC_Pipe`).
* **Persistent Data vs Binaries:**
  - Binaries are installed per-user to `%LOCALAPPDATA%\Programs\AG2Router\`.
  - Persistent user metadata is stored in `%LOCALAPPDATA%\AG2-Router\data\accounts.json`.
  - Routing configuration, candidate evidence, and interrupted-switch records use sibling `config.json`, `quota-observations.json`, and `switch-journal.json` files. See [persistence and recovery](docs/persistence-and-concurrency.md).
  - Encrypted sessions are stored in `%LOCALAPPDATA%\AG2-Router\vault\sessions.dat`.
  - Application data is **never** deleted or overwritten during upgrades or uninstallation.

---

## 3. Installation & Deployment

### Prerequisites
* Windows 10 (1809+) or Windows 11 (64-bit).
* [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (pre-installed on Windows 11 and modern Windows 10).
* Antigravity 2 installed.

### Per-User Installation (Non-Admin)
This checkout targets a prerelease for supervised user testing; stable releases remain available separately.

1. Download the release archive `AG2Router-v0.4.1-rc.1-win-x64.zip` from releases.
2. Extract the archive to a folder of your choice.
3. Open PowerShell and run the installer:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
4. The installer:
   - Shuts down any existing instance gracefully (`--exit`).
   - Copies self-contained binaries to `%LOCALAPPDATA%\Programs\AG2Router`.
   - Creates a Start Menu shortcut: `AG2 Router`.
   - Registers an Add/Remove Programs entry in Windows Settings.

### Uninstallation
Run the uninstaller script or uninstall via Windows Settings > Installed Apps:
```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\AG2Router\uninstall.ps1"
```
**Safety Guarantee:** The uninstaller removes only program binaries, shortcuts, and owned startup entries. It strictly preserves your account metadata, encrypted vault (`%LOCALAPPDATA%\AG2-Router`), and active Windows credentials (`gemini:antigravity`).

---

## 4. Command-Line Usage & Startup Controls

AG2 Router supports deterministic command-line controls for system startup and automation:

| Command | Description |
| :--- | :--- |
| `AG2Router.exe` | Launches the application into the system tray. |
| `AG2Router.exe --tray` | Explicit silent tray launch (used by Windows autostart). |
| `AG2Router.exe --open` | Launches or signals the running instance to bring the dashboard window to foreground. |
| `AG2Router.exe --close` | Signals the running instance to close and hide the dashboard window to the tray. |
| `AG2Router.exe --exit` | Signals the running instance to perform a graceful shutdown and exit. |

### Windows Autostart Registration
Autostart is exposed through the native `/api/settings/autostart` API. It writes a per-user registry key:
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AG2Router` = `"{InstallDir}\AG2Router.exe" --tray`

### Configure automatic routing

Dashboard Settings exposes **Auto Switch**, **Workload Model**, **Low Quota Threshold**, **Minimum Candidate Quota**, and **Polling Interval**. Workload Model accepts an exact upstream model key and suggests valid keys observed in current telemetry; it does not use a fabricated model list. Saves preserve the configured model across other setting changes.

The workload model expresses routing intent; AG2 Router does not automatically detect the IDE model dropdown. With Auto Switch enabled but no model configured, the dashboard explains that routing cannot operate. Candidate status distinguishes unobserved, unknown, stale, unavailable, and usable evidence. Manual switching does not require automatic-routing model or quota configuration, but retains the same activity, identity, recovery, and process safety gates.

---

## 5. Development & Building from Source

### Prerequisites
* .NET SDK 10.0.x (x64)
* Node.js 20+ / npm 9+ (for UI asset bundling and verification oracle)

### Build Commands

```powershell
# Restore and run the full .NET test suite
dotnet test dotnet/AG2Router.sln -c Release

# Run TypeScript typechecks and test oracle
npm run typecheck
npm test

# Build and package the self-contained release candidate
powershell -ExecutionPolicy Bypass -File scripts/package-release.ps1 -Configuration Release -Version 0.4.1-rc.1
```

The packaging script outputs:
- `dist/AG2Router-v0.4.1-rc.1-win-x64.zip`: Standalone self-contained release archive.
- `dist/SHA256SUMS.txt`: SHA-256 checksum manifest for artifact integrity.
- `dist/AG2Router-Setup-v0.4.1-rc.1-win-x64.exe`: Inno Setup installer (if `iscc` compiler is present in PATH).

---

## 6. Security Principles

1. **Local Loopback Only:** Loopback HTTP server binds strictly to `127.0.0.1` on an ephemeral port. Remote network requests are rejected with `403 Forbidden`.
2. **DPAPI Protected Sessions:** Sensitive session tokens are encrypted using Windows DPAPI `CurrentUser` scope and never written to logs or transmitted over unauthenticated interfaces.
3. **Fail-Closed Persistence:** Account metadata and session vaults employ atomic durable file writes (`.tmp` swap with disk flush). Malformed or zero-byte files fail closed and never overwrite intact storage.
4. **Non-Destructive Operations:** Upgrades and uninstalls never purge user credentials or account databases.

---

## 7. Troubleshooting & Recovery

* **Dashboard displays "Waiting for Antigravity":** Antigravity is not currently running. The router will automatically connect when Antigravity starts.
* **WebView2 runtime missing:** Ensure Microsoft Edge WebView2 Evergreen Runtime is installed. Download from Microsoft's official site.
* **Automatic routing unavailable:** Configure Workload Model and inspect candidate evidence. An unknown or stale observation is not healthy quota; a reset time alone does not prove replenishment.
* **Recovery or quarantine warning:** Use the dashboard recovery status and its available resolve action. Resolution requires current safety proof and may require application restart; absence of a journal alone does not clear quarantine. Do not delete recovery records to bypass admission. See [interrupted switch recovery](docs/persistence-and-concurrency.md#interrupted-switch-consistency).
* **Resetting / Manual Recovery:** If you ever need to inspect or back up account data, all configuration files reside in:
  - Account metadata: `%LOCALAPPDATA%\AG2-Router\data\accounts.json`
  - Encrypted sessions: `%LOCALAPPDATA%\AG2-Router\vault\sessions.dat`
  - Application diagnostics: `%LOCALAPPDATA%\AG2-Router\app.log`

---

## License

MIT
