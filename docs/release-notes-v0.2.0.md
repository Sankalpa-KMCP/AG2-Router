# AG2 Router v0.2.0 Release Notes

**Release Date:** September 2026  
**Version:** 0.2.0  
**Target Platform:** Windows 10 (1809+) / Windows 11 (64-bit)

---

## Highlights

AG2 Router v0.2.0 is a major milestone introducing a redesigned Svelte dashboard, friendly account aliases, canonical quota normalization, and safe in-place installer upgrade semantics.

### 1. Modern Svelte + TypeScript + Vite Dashboard
- Completely redesigned frontend interface featuring a clean white aesthetic with subtle borders and clear typography.
- High-visibility circular ring indicators displaying canonical quota pools, truthful remaining percentages, provider reset countdowns, and model tier/reasoning mode tags.
- Enriched accounts view displaying active account badges, quota health indicators, switch controls, and inline friendly alias editing.
- Retains full compatibility with the .NET / WPF native shell and Microsoft Edge WebView2 Evergreen host.

### 2. Friendly Account Aliases
- Users can assign custom friendly names/aliases to enrolled Antigravity accounts.
- Aliases persist durably in `%LOCALAPPDATA%\AG2-Router\data\accounts.json` via atomic file swaps with disk flush.
- Backward-compatible schema with automatic fallback to name, email, or masked account ID when no alias is set.
- Secure loopback API endpoint (`PATCH /api/accounts/{id}`) with origin and CSRF validation for safe inline alias editing.

### 3. Canonical Quota Normalization & Truthful Telemetry
- Unified telemetry schema collapsing only proven shared model variants across runtime telemetry variants without data loss.
- Truthful remaining percentage and provider reset countdown telemetry derived directly from upstream models without unverified window assumptions.
- Preserves segregated `promptCredits` and `flowCredits` without invented totals or speculative sum calculations.
- Robust, type-safe fallback logic ensuring accurate metrics across differing upstream API schemas.

### 4. Seamless In-Place Upgrades
- Inno Setup installer (`AG2Router-Setup-v0.2.0-win-x64.exe`) configured with stable per-user `AppId` (`{D37E7404-585A-4B6A-B7F9-5360980DF628}`).
- Upgrades previous v0.1.0 installations in `%LOCALAPPDATA%\Programs\AG2Router` in place.
- Reuses existing native single-instance controls (`AG2Router.exe --exit`) with bounded wait and file-unlock stabilization before file replacement.
- Cleans legacy script uninstaller registry entries cleanly using 5-guard ownership verification to avoid duplicate entries in Windows Settings > Installed Apps.

---

## Data Preservation & Security Architecture

### Data Preservation Design
1. **Strict Directory Isolation:** Installer and uninstaller payloads operate exclusively within `%LOCALAPPDATA%\Programs\AG2Router`. User configuration, account metadata, and encrypted session vaults reside separately in `%LOCALAPPDATA%\AG2-Router` and are never deleted, overwritten, or modified by installer actions.
2. **Local Loopback Isolation:** Background API server binds strictly to `127.0.0.1` on an ephemeral port. Remote network requests and cross-site fetch origins are rejected with `403 Forbidden`.
3. **DPAPI Protected Sessions:** Sensitive session tokens are encrypted using Windows DPAPI `CurrentUser` scope and never exposed in logs or plaintext files.
4. **Fail-Closed Persistence:** All metadata and session vaults employ durable `.tmp` swap writes with disk flush to prevent corruption from unexpected power or process termination.
5. **Non-Elevated Execution:** Operates entirely within standard per-user privileges; administrative elevation is not requested or required.

### Verification Scope & Disclosed Runtime Limitations

To maintain engineering integrity, the following boundaries distinguish automated test proof from unverified live environment scenarios:

- **Verified by Automated Testing:**
  - Byte-for-byte preservation of synthetic `sessions.dat` and `accounts.json` across simulated binary upgrades (`UpgradePreservationTests`).
  - Session vault round-trip read/write compatibility across schema variants verified through `FakeDpapiProvider`.
  - Inno Setup directory isolation, AppId stability, non-admin execution, and uninstaller registry ownership verified through automated test suites.
  - Graceful process termination signal (`--exit`) verified with bounded timeout and process exit tracking.

- **Explicitly Disclosed Runtime Limitations (Not Live-Verified):**
  - **Real DPAPI Upgrade Execution:** Compatibility of real Windows DPAPI `CurrentUser` encryption keys across a live production installer execution was not executed in live environment tests to protect developer machine state.
  - **Live WinCred Execution:** Behavior of active Windows Credential Manager tokens (`gemini:antigravity`) during a live production installer execution was not tested against a live Antigravity user session.
  - **Registry Sandbox Execution:** The compiled Setup executable (`AG2Router-Setup-v0.2.0-win-x64.exe`) was not executed inside an isolated Windows registry sandbox during release packaging.

---

> [!WARNING]
> ### Windows SmartScreen Advisory
> These release candidate binaries are not Authenticode-signed with a commercial EV certificate.
> Windows SmartScreen may warn that the application or installer is unrecognized:
> *"Windows protected your PC - Microsoft Defender SmartScreen prevented an unrecognized app from starting."*
> 
> To proceed safely:
> 1. Click **More info**.
> 2. Click **Run anyway** (if desired after verifying integrity).
> 3. Always verify that the SHA-256 hash of your downloaded file matches the authoritative hash published in `SHA256SUMS.txt`.

---

## Installation & Upgrade

### Option A: Setup Installer (Recommended)
1. Download `AG2Router-Setup-v0.2.0-win-x64.exe` and `SHA256SUMS.txt`.
2. Verify checksum: `Get-FileHash -Algorithm SHA256 AG2Router-Setup-v0.2.0-win-x64.exe`.
3. Run the installer. It will automatically request a graceful shutdown of any running instance and upgrade binaries in place.

### Option B: Standalone Archive
1. Download `AG2Router-v0.2.0-win-x64.zip`.
2. Extract into a folder of your choice and run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```
