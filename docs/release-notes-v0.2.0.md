# AG2 Router v0.2.0 Release Notes

**Release Date:** September 2026  
**Version:** 0.2.0  
**Target Platform:** Windows 10 (1809+) / Windows 11 (64-bit)

---

## Highlights

AG2 Router v0.2.0 is a major milestone introducing a redesigned Svelte dashboard, friendly account aliases, canonical quota normalization, and safe in-place installer upgrade semantics.

### 1. Modern Svelte + TypeScript + Vite Dashboard
- Completely redesigned frontend interface featuring a clean white aesthetic with subtle borders and clear typography.
- High-visibility circular ring indicators for weekly and 5-hour quota consumption and reset timings.
- Enriched accounts view displaying active account badges, quota status, switch controls, and inline alias editing.
- Retains full compatibility with the .NET / WPF native shell and Microsoft Edge WebView2 Evergreen host.

### 2. Friendly Account Aliases
- Users can now assign custom friendly names/aliases to enrolled Antigravity accounts.
- Aliases persist durably in `%LOCALAPPDATA%\AG2-Router\data\accounts.json` via atomic file swaps.
- Seamless fallback to email or masked account ID when no alias is set.

### 3. Canonical Quota Normalization
- Unified quota processing pipeline accommodating legacy nested structures and modern telemetry formats.
- Robust, type-safe fallback logic ensuring accurate usage metrics even across API variations.

### 4. Seamless In-Place Upgrades
- Inno Setup installer (`AG2Router-Setup-v0.2.0-win-x64.exe`) with stable per-user `AppId` (`{D37E7404-585A-4B6A-B7F9-5360980DF628}`).
- Seamlessly upgrades previous v0.1.0 installations in `%LOCALAPPDATA%\Programs\AG2Router` without duplicate Installed Apps entries.
- **Strict Data Preservation:** Never deletes, overwrites, or alters user account state (`%LOCALAPPDATA%\AG2-Router`), DPAPI encrypted session vaults, or Windows Credential Manager (`gemini:antigravity`).

---

## Security & Architecture Principles

1. **Loopback Isolation:** The background API server binds strictly to `127.0.0.1` on an ephemeral port. Remote requests are rejected with `403 Forbidden`.
2. **DPAPI Protected Sessions:** Sensitive session tokens remain encrypted using Windows DPAPI `CurrentUser` scope.
3. **Fail-Closed Persistence:** All metadata and session vaults employ durable `.tmp` swap writes with disk flush.
4. **Non-Elevated Execution:** Operates entirely within standard per-user privileges; administrative elevation is not required.

---

> [!WARNING]
> ### Windows SmartScreen Advisory
> These release candidate binaries are not signed with an extended validation (EV) code-signing certificate.
> When running `AG2Router-Setup-v0.2.0-win-x64.exe` or `AG2Router.exe` for the first time, Windows Defender SmartScreen may display:
> *"Windows protected your PC - Microsoft Defender SmartScreen prevented an unrecognized app from starting."*
> 
> To proceed:
> 1. Click **More info**.
> 2. Click **Run anyway**.
> 3. Verify that the SHA-256 hash matches the authoritative hash published in `SHA256SUMS.txt`.

---

## Installation & Upgrade

### Option A: Setup Installer (Recommended)
1. Download `AG2Router-Setup-v0.2.0-win-x64.exe` and `SHA256SUMS.txt`.
2. Verify checksum: `Get-FileHash -Algorithm SHA256 AG2Router-Setup-v0.2.0-win-x64.exe`.
3. Run the installer. It will automatically shut down any running instance and upgrade binaries in place.

### Option B: Standalone Archive
1. Download `AG2Router-v0.2.0-win-x64.zip`.
2. Extract into a folder and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
