# AG2 Router v0.4.0 Release Notes

**Version:** 0.4.0
**Target Platform:** Windows 10 (1809+) / Windows 11 (64-bit)

## Highlights

- **Streamlined Live Capacity Overview:** Redesigns the overview into a compact, truthful current-account capacity monitor. Displays conservative minimum bottleneck capacity across active model families with unambiguous account scoping.
- **Verified Model Family Cards:** Surfaces conservative capacity metrics for verified model telemetry: Gemini 3.6 Flash, Gemini 3.1 Pro, Claude Opus 4.6, and Claude Sonnet 4.6. Unverified versions, future/prior variants, and generic versionless names are excluded from overview cards to avoid false capacity reports.
- **Truthful Telemetry Semantics:** Unknown quota states remain explicitly unknown without fabricating false 100% or healthy metrics; exhausted evidence takes precedence; differing reset windows remain distinct; and relative countdowns are presented truthfully without fabricating 5-hour or weekly pool classifications.
- **Preserved Detailed Telemetry:** Complete per-model telemetry, individual variant rows, and detailed routing metrics remain fully accessible in the Telemetry & Quotas view.
- **Release Tag & Version Consistency Guard:** Automated release packaging now verifies that tag-triggered release events require the Git tag to strictly equal `v${canonicalVersion}` extracted from `dotnet/Directory.Build.props`, failing closed before packaging or upload if a mismatch occurs.

## Known Limitations

- Provider attribution for Overview cards maps models via recognized keys and labels because upstream telemetry does not yet provide an explicit provider enum. Unclassified models remain fully visible in the Telemetry & Quotas view.
- Automated packaging and upgrade checks use synthetic or disposable state; they do not prove behavior against a live installed application or production credentials.
- The repository workflow does not establish Authenticode signing; Windows SmartScreen may warn about an unrecognized publisher.

## Download and integrity

The release includes `AG2Router-Setup-v0.4.0-win-x64.exe`, `AG2Router-v0.4.0-win-x64.zip`, and `SHA256SUMS.txt`. Verify downloaded files against the checksum manifest.

User account metadata, encrypted vault data, and other persistent state remain outside the application-binary install directory at `%LOCALAPPDATA%\AG2-Router` and are preserved during upgrade and uninstall.
