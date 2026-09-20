# AG2 Router: Native C#/.NET Windows Migration Architecture

## 1. Migration Overview & Strategic Decision

AG2 Router is transitioning from a Node.js/TypeScript daemon to a high-performance native Windows desktop application built with **.NET 10 LTS** and **WPF**.

### 1.1 Why Native .NET (and Why Not Electron)
- **Zero Heavyweight Framework Bloat:** Electron introduces ~150–200MB+ installer overhead, multiple bundled Chromium/Node runtimes, and high idle RAM usage.
- **Native OS & Security Integration:** Windows Credential Manager (`advapi32.dll`), DPAPI (`CryptProtectData`), and Windows taskbar/notification area integration operate natively without fragile Node-gyp bindings or child process piping.
- **Single Runtime Footprint:** Using WebView2 leverages the Evergreen Microsoft Edge WebView2 runtime already installed and maintained on modern Windows systems, allowing the native binary to remain compact and lightweight.
- **In-Process Loopback Hosting:** ASP.NET Core Kestrel runs in-process via `WebApplication.CreateSlimBuilder`, eliminating the need for a separate Node runtime or external web server daemon.

### 1.2 Preservation of the TypeScript Reference Implementation
During migration, the existing Node/TypeScript implementation in `src/` and `test/` remains the authoritative **behavioral oracle**.
- It is NOT deleted, altered, or superseded in this PR.
- Automated CI continues to validate both the TypeScript suite (98 tests) and the .NET suite.
- Parity will be proven slice-by-slice before any deprecation.

---

## 2. Solution Architecture

The .NET solution resides in `dotnet/` and enforces strict boundary isolation across 5 projects:

```
dotnet/
├── AG2Router.sln
├── Directory.Build.props      # net10.0, latest C#, nullable enable, RepoRoot resolver
│
├── src/
│   ├── AG2Router.Core/       # net10.0: Platform-neutral domain contracts, DTOs, interfaces
│   ├── AG2Router.AG2/        # net10.0: AG2 process discovery and Connect-RPC boundary
│   ├── AG2Router.Windows/    # net10.0-windows: Win32 lifecycle, single-instance guard, tray helper
│   └── AG2Router.App/        # net10.0-windows: WPF host, embedded Kestrel, WebView2, Tray UX
│
└── tests/
    └── AG2Router.Tests/      # net10.0-windows: xUnit test suite (loopback, guards, contracts)
```

### Component Responsibilities:
1. **`AG2Router.Core`:** Contains domain models (`SystemStatusDto`, `AccountMetadata`, `RouterConfigDto`) and contracts (`IAccountStore`, `IAG2Adapter`, `ISessionVault`). Contains zero UI or Win32 dependencies.
2. **`AG2Router.AG2`:** Isolated adapter boundary for Antigravity 2 integration. In PR 1, provides `PlaceholderAG2Adapter` returning truthful un-migrated statuses.
3. **`AG2Router.Windows`:** Platform lifecycle services including session-scoped `SingleInstanceGuard` (using a `Local\` mutex and named pipe to activate existing instances), harmless autostart abstraction, and display/taskbar positioning helpers.
4. **`AG2Router.App`:** WPF executable hosting:
   - In-process ASP.NET Core loopback host binding strictly to `127.0.0.1:0` (ephemeral port).
   - Links and serves canonical dashboard assets from `src/ui/` without Git file duplication.
   - `NotifyIcon` system tray management with single-click quick-status flyout and double-click / context menu dashboard activation.
   - `MainWindow` hosting `WebView2` with close-to-tray cancellation and render suspension when hidden.
5. **`AG2Router.Tests`:** Automated test suite verifying loopback server routes, security filters, single-instance detection, and contract defaults.

---

## 3. Desktop Lifecycle & User Experience

### 3.1 Session-Scoped Single Instance Guard
Uses a `Local\AG2Router_Session_Mutex` to ensure only one instance runs per Windows user session. If a user launches a second instance:
1. The secondary instance detects the existing mutex.
2. It sends an `"ACTIVATE"` command across `AG2Router_Session_IPC_Pipe`.
3. The primary instance brings its dashboard window to the foreground and restores from tray.
4. The secondary instance terminates immediately.

### 3.2 System Tray & Close-to-Tray
- **Single Left Click:** Toggles the compact, native `QuickStatusWindow` flyout anchored near the taskbar.
- **Double Left Click / Tray Menu "Open Dashboard":** Opens or foregrounds the main WebView2 dashboard window.
- **Window Close `[X]`:** Intercepted (`e.Cancel = true`) to hide the window to tray rather than terminating the background router. Calls `CoreWebView2.TrySuspendAsync()` to release rendering memory.
- **Tray Menu "Exit AG2 Router":** Performs clean shutdown: removes tray icon immediately (eliminating ghost icons), stops Kestrel, disposes mutex/pipes, and exits process with code 0.

### 3.3 Dynamic Ephemeral Loopback Port
To avoid port conflicts with the running Node daemon (`39250`) or other developer tools:
- The embedded Kestrel server binds to `127.0.0.1:0`.
- The OS assigns a free ephemeral port, which `LoopbackServer` captures.
- WebView2 navigates directly to `http://127.0.0.1:{port}/index.html`.
- Non-loopback requests are blocked with HTTP `403 Forbidden`.

---

## 4. Staged Migration Roadmap

Subsequent migration slices will port the remaining subsystems from TypeScript to .NET:
1. **PR 1 (Current):** Native application shell, tray, embedded loopback host, WebView2, and test baseline.
2. **PR 2:** Native Antigravity 2 dynamic process discovery and read-only telemetry client (`AG2Router.AG2`).
3. **PR 3:** Native Windows Credential Manager reader/writer and DPAPI session vault (`AG2Router.Windows`).
4. **PR 4:** Native account enrollment and store persistence.
5. **PR 5:** Native manual switch transaction machine, rollback coordinator, and autostart registration.
