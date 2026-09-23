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

Other version-bearing files and release names must agree at release time, but copied literals are not independent authorities. A tag identifies a release snapshot; it is not a living documentation value.

## Validation pipelines

- .github/workflows/ci.yml runs Node 20 and 22 on Windows and Ubuntu: install, typecheck, build, and Node tests.
- .github/workflows/dotnet-ci.yml restores, builds, and tests the .NET solution on Windows.
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

The Inno Setup installer is per-user, uses the stable AppId declared in installer/AG2Router.iss, and targets the per-user Programs/AG2Router directory. It coordinates with running instances using the session mutex `Local\AG2Router_Session_Mutex` (via Win32 OpenMutexW/CloseHandle), requests graceful application exit (--exit), and polls boundedly (up to 5 seconds) before proceeding. Both Inno Setup and PowerShell installers fail closed if the shutdown request fails, if the executable is missing, or if the mutex remains held.

Inno Setup implements atomic staged upgrades: existing installations are staged to `{app}.bak` using Win32 MoveFileW at `ssInstall`, new binaries are extracted, verified at `ssPostInstall` before pruning the backup, and automatically rolled back in `DeinitializeSetup()` if setup is aborted or fails. It cleans legacy script uninstall registrations only after strict ownership checks.

The PowerShell installer is the archive installation path. It also installs per-user, performs atomic directory swaps with backup and rollback, creates an owned shortcut, and registers an uninstall entry.

Persistent application data under the separate AG2-Router user-data directory is outside the binary installation directory. Install and upgrade procedures must not rewrite it.

## Uninstall boundary

Uninstall may remove only owned program binaries, shortcuts, uninstall registration, and an autostart value positively matched to this installation. It must preserve account metadata, vault data, WebView/user data unless an explicitly designed future policy says otherwise, and the live Antigravity credential.

Ownership checks are part of the safety contract. Path or registry cleanup must fail/skip safely when ownership cannot be established.

Both PowerShell and Inno uninstall entry points coordinate with the session mutex `Local\AG2Router_Session_Mutex`, verify executable presence, and fail closed if the application exit request fails or if the mutex remains held; destructive removal does not continue after an unsuccessful shutdown signal.

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
