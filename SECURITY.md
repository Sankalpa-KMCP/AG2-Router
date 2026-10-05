# Security policy

AG2 Router handles Antigravity credentials, a local loopback API, and persistent user data. Reports about anything in that trust boundary are welcome and taken seriously.

## Reporting a vulnerability

Do not disclose suspected vulnerabilities through public issues, and do not exploit them against live accounts.

Report suspected vulnerabilities privately through GitHub's private vulnerability reporting: open the repository's Security tab and use "Report a vulnerability". The repository is public and this channel is verified active; reports submitted there reach the maintainer privately and are not publicly visible.

No other private channel is offered, and no personal contact details are published.

## Scope

In scope:

- The shipped Windows application under `dotnet/src/` (WPF host, loopback server, WebView2 dashboard host, Antigravity integration, vault, switching).
- The install/upgrade/uninstall scripts (`scripts/install.ps1`, `scripts/uninstall.ps1`) and `installer/AG2Router.iss`.
- The release packaging pipeline insofar as it could leak secrets into artifacts.

Out of scope for security reports (please use a regular issue instead):

- The Node/TypeScript tree under `src/` and `test/` — a reference implementation used for CI parity, not shipped code.
- Antigravity itself and other third-party software.
- Best-effort memory-hygiene limitations that are already documented in [docs/security-and-trust-model.md](docs/security-and-trust-model.md).

## What to include

- AG2 Router version (from the release asset name or the dashboard) and install type.
- Windows version.
- Reproduction steps and, if relevant, redacted excerpts from `%LOCALAPPDATA%\AG2-Router\app.log`.

Never include credential material, vault contents, tokens, or prompt transcripts in a report — redact before sending.

## Supported versions

Fixes target the latest stable release. Release-candidate prereleases receive fixes at the maintainer's discretion; they are explicitly labeled as test prereleases.

This is a solo-maintained project: reports are handled as time allows, and no response-time guarantee is made.
