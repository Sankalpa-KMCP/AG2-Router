# Build and release

This document owns build inputs, generated assets, packaging, version provenance, and install/upgrade/uninstall boundaries. Runtime behavior belongs in [runtime-architecture.md](runtime-architecture.md).

## Artifact flow

    frontend/src and frontend/index.html
        -> Vite/Svelte build
        -> src/ui
        -> AG2Router.App linked wwwroot content
        -> dotnet publish win-x64
        -> publish/win-x64
        -> ZIP and optional Inno Setup installer
        -> checksum manifest and CI artifact

### Authored and generated ownership

- frontend/ is the authored dashboard source.
- frontend/vite.config.ts sets the output directory to src/ui and clears it before building.
- src/ui is generated deployable output. Do not hand-edit it as the primary source of a UI change.
- AG2Router.App.csproj links src/ui files as wwwroot content and copies them to build and publish output.
- scripts/copy-ui.mjs separately stages src/ui into the Node build output used by the TypeScript reference server.

CURRENT LIMITATION: CI builds the UI but does not explicitly fail when committed src/ui differs from a clean frontend rebuild. See [known-limitations.md](known-limitations.md).

## Version authority

dotnet/Directory.Build.props is the canonical release-version input for .NET assemblies and scripts/package-release.ps1 when no explicit override is supplied. The release workflow reads the same property.

Prerelease versions retain their suffix in Version and InformationalVersion and in package/installer names. AssemblyVersion and FileVersion use the numeric major.minor.patch portion required by their version formats.

Other version-bearing files and release names must agree at release time, but copied literals are not independent authorities. A tag identifies a release snapshot; it is not a living documentation value.

## Validation pipelines

- .github/workflows/ci.yml runs Node 20 and 22 on Windows and Ubuntu: install, typecheck, build, and Node tests.
- .github/workflows/dotnet-ci.yml restores, builds, and tests the .NET solution on Windows, then compiles the installer with pinned Inno Setup 6.4.0 against disposable input without producing or running an installer. This is the pre-tag installer syntax gate.
- .github/workflows/release.yml runs combined Node validation, .NET tests, provisions pinned Inno Setup, packages with an installer requirement, checks required outputs, and uploads workflow artifacts.

The current release workflow uploads build artifacts; it does not itself create a GitHub Release or prove external publication/signing.

## Packaging

scripts/package-release.ps1:

1. Rebuilds frontend/ into generated src/ui and fails if that build fails, including standalone invocations.
2. Publishes AG2Router.App self-contained for win-x64.
3. Removes PDB files from the release payload and includes install/uninstall helpers.
4. Requires core binaries, WebView2 assemblies, and wwwroot assets.
5. Creates the ZIP and SHA256SUMS.txt.
6. Compiles installer/AG2Router.iss when ISCC is available or required.
7. Extracts and inspects the ZIP for required files, forbidden test/debug material, and selected secret markers.

ReleasePackagingTests assert important script and installer contracts. They supplement but do not replace executing the packaging workflow on Windows.

The release workflow also builds frontend assets before invoking packaging. The packaging script itself enforces freshness, so standalone invocation does not silently reuse stale src/ui.

## Installer and upgrade boundary

The Inno Setup installer is per-user, uses the stable AppId declared in installer/AG2Router.iss, and targets the per-user Programs/AG2Router directory. It coordinates with running instances using the session mutex `Local\AG2Router_Session_Mutex` (via Win32 OpenMutexW/CloseHandle), launches the graceful --exit helper without an unbounded process wait, and polls stop state for at most five seconds. A mutex query error is unknown and fails closed. Both Inno Setup and PowerShell installers refuse to proceed when stopped state cannot be established.

Inno Setup stages an existing complete `{app}` directory to `{app}.bak` in the abortable `PrepareToInstall` phase. A failed move or pre-existing backup prevents extraction and an old-file overlay. The backup remains until successful `ssDone`; cancellation or failure attempts checked removal of any partial replacement and checked promotion of the complete backup. If rollback cannot finish, the backup is retained for explicit manual recovery. The successful new directory contains no old-only files. Legacy script uninstall registration is cleaned only after strict ownership checks.

The PowerShell installer is the archive installation path. It also installs per-user, performs atomic directory swaps with backup and rollback, creates an owned shortcut, and registers an uninstall entry. Installation replacement proceeds only after stopped state is positively established (`process == STOPPED` and `mutex == STOPPED`); an initial transient `UNKNOWN` query may be retried, but any unresolved `UNKNOWN` fails closed without replacing files or mutating directory state. When a running instance is detected, graceful `--exit` shutdown is requested and polled for up to five seconds before failing closed if stopped state cannot be positively verified.

### Cross-channel ownership and transitions

Installation ownership at `%LOCALAPPDATA%\Programs\AG2Router` is explicitly classified (`None`, `PowerShellOwned`, `InnoOwned`, `Ambiguous`, or `ForeignConflict`):

- **Inno-owned installation detected:** PowerShell `install.ps1` refuses to overwrite it, and PowerShell `uninstall.ps1` refuses to delete it, directing the user to the Inno installer or registered Inno uninstaller. This prevents broken Windows Add/Remove registrations or orphaned uninstallers.
- **Ambiguous ownership state detected:** When both registrations are present or uninstaller files conflict with registry records, both PowerShell scripts fail closed without replacing or deleting files or registry keys.
- **Foreign or unproven registration at fixed PowerShell key (`ForeignConflict`):** Exact fixed-key collisions at `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AG2Router` that cannot be positively proven to belong to this installation are classified as ownership conflicts (`ForeignConflict`). Foreign or unprovable registrations are preserved untouched; installation and uninstallation fail closed before any directory replacement, file copy, shortcut creation, or registry modification.
- **Switching from Inno to ZIP/PowerShell:** The user must first uninstall AG2 Router via Windows Settings > Installed Apps (or the registered Inno uninstaller) to cleanly clear the Inno registration, then run `install.ps1`.
- **Switching from ZIP/PowerShell to Inno:** Supported directly; the Inno Setup installer stages the existing directory and prunes the legacy script uninstall registration after verifying strict ownership guards.

Persistent application data under the separate AG2-Router user-data directory is outside the binary installation directory. Install and upgrade procedures must not rewrite it.

## Uninstall boundary

Uninstall may remove only owned program binaries, shortcuts, uninstall registration, and an autostart value positively matched to this installation. It must preserve account metadata, vault data, WebView/user data unless an explicitly designed future policy says otherwise, and the live Antigravity credential.

Ownership checks are part of the safety contract. `uninstall.ps1` classifies installation ownership before stopping processes or removing files:
- If `PowerShellOwned`, uninstallation proceeds with existing cleanup steps.
- If `InnoOwned`, `uninstall.ps1` refuses removal and directs the user to the registered Inno uninstaller or Windows Installed Apps.
- If `Ambiguous`, `uninstall.ps1` fails closed without touching files, shortcuts, or registry entries.
- If `ForeignConflict`, `uninstall.ps1` refuses to remove or modify unproven/foreign registrations.

Both PowerShell and Inno uninstall entry points coordinate with the session mutex `Local\AG2Router_Session_Mutex` and fail closed if stopped state cannot be proven. PowerShell requires a successful process enumeration both before and after its bounded exit request; query failure is unknown, not stopped. Inno's helper invocation is nonblocking and followed by bounded mutex polling; a query failure or held mutex prevents destructive removal. A missing executable by itself is not proof that the application stopped.

Evidence: installer/AG2Router.iss, scripts/install.ps1, scripts/uninstall.ps1, ReleasePackagingTests, and UpgradePreservationTests.

## Release provenance limits

- SHA256SUMS.txt proves artifact bytes match the produced manifest, not publisher identity.
- Current artifacts are not proven Authenticode-signed by the repository workflow.
- Synthetic preservation tests do not prove every live DPAPI, WinCred, registry, or WebView2 upgrade scenario.
- Release notes describe one release and must not silently become current architecture.

## Change checklist

When the UI or packaging changes:

1. Edit authored inputs, not generated output alone.
2. Rebuild src/ui.
3. Run frontend type checks and Node tests.
4. Run native tests, especially API/package tests when boundaries changed.
5. Verify publish contains the expected wwwroot files.
6. Verify persistent-data and uninstall ownership boundaries.
7. Check canonical version and artifact names from one release input.
8. Inspect the produced payload for tests, debug files, secrets, and machine-specific paths.
