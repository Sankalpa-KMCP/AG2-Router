# AG2 Router

A lightweight, native Windows desktop application and service for Antigravity 2 that monitors per-model quota across multiple connected accounts and performs safe, automated account routing.

---

## 1. What AG2 Router Is

AG2 Router eliminates session and quota exhaustion during heavy Antigravity agent workflows:

* **Real-Time Quota Telemetry:** Continuously polls active Antigravity language server telemetry for model quota and prompt/flow credits.
* **Autonomous Low-Quota Routing:** Automatically identifies low-quota conditions and switches to the highest-priority eligible candidate account.
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
  - Encrypted sessions are stored in `%LOCALAPPDATA%\AG2-Router\vault\sessions.dat`.
  - Application data is **never** deleted or overwritten during upgrades or uninstallation.

---

## 3. Installation & Deployment

### Prerequisites
* Windows 10 (1809+) or Windows 11 (64-bit).
* [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (pre-installed on Windows 11 and modern Windows 10).
* Antigravity 2 installed.

### Per-User Installation (Non-Admin)
1. Download the release archive `AG2Router-v0.2.2-win-x64.zip` from releases.
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
Autostart can be toggled directly in the Dashboard Settings or via API. It writes a per-user registry key:
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AG2Router` = `"{InstallDir}\AG2Router.exe" --tray`

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
powershell -ExecutionPolicy Bypass -File scripts/package-release.ps1 -Configuration Release -Version 0.2.2
```

The packaging script outputs:
- `dist/AG2Router-v0.2.2-win-x64.zip`: Standalone self-contained release archive.
- `dist/SHA256SUMS.txt`: SHA-256 checksum manifest for artifact integrity.
- `dist/AG2Router-Setup-v0.2.2-win-x64.exe`: Inno Setup installer (if `iscc` compiler is present in PATH).

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
* **Resetting / Manual Recovery:** If you ever need to inspect or back up account data, all configuration files reside in:
  - Account metadata: `%LOCALAPPDATA%\AG2-Router\data\accounts.json`
  - Encrypted sessions: `%LOCALAPPDATA%\AG2-Router\vault\sessions.dat`
  - Application diagnostics: `%LOCALAPPDATA%\AG2-Router\app.log`

---

## License

MIT
