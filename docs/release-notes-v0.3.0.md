# AG2 Router v0.3.0 Release Notes

**Version:** 0.3.0
**Target Platform:** Windows 10 (1809+) / Windows 11 (64-bit)

## Highlights

- **Windows Credential Identity Disambiguation:** Resolves incorrect conflation of Windows Credential `UserName` metadata with authenticated Antigravity account identity. Credential writes canonicalize to `antigravity` while retaining seamless compatibility with legacy credential targets, and transactional rollback preserves the exact captured pre-switch credential payload.
- **Streamlined Provider Quota Overview:** Replaces the crowded per-model overview matrix and separate Prompt/Flow credit panels with two unified provider summaries: Google Gemini and Anthropic Claude.
- **Conservative Quota Aggregation:** Overview provider metrics display the minimum remaining capacity across underlying models, showing a synchronized reset time only when model windows align. Differing reset windows are preserved as multiple/varying reset windows rather than collapsed into one misleading timestamp, unknown quota states are never misrepresented as healthy (100%), and incomplete reset evidence remains explicitly uncertain.
- **Detailed Telemetry Preserved:** Full per-model telemetry, reset timers, and automated routing details remain accessible in the Telemetry & Quotas view.
- **In-Place Upgrade Compatibility:** Existing v0.2.2 installations upgrade in place at `%LOCALAPPDATA%\Programs\AG2Router`. Persistent application state (`%LOCALAPPDATA%\AG2-Router`) and credential vault stores remain isolated outside the installation directory and are preserved during upgrade and uninstall.

## Known Limitations

- Provider attribution for the Overview cards currently maps models via recognized key prefixes and display names (e.g., Gemini and Claude) because the upstream telemetry DTO does not yet expose an explicit provider enum. Unclassified models remain fully visible in the Telemetry & Quotas view.

## Download and integrity

The release includes `AG2Router-Setup-v0.3.0-win-x64.exe`, `AG2Router-v0.3.0-win-x64.zip`, and `SHA256SUMS.txt`. Verify the downloaded files against the checksum manifest. The repository workflow does not establish Authenticode signing; Windows SmartScreen may warn about an unrecognized publisher.

User account metadata, encrypted vault data, and other persistent state remain outside the application-binary install directory. Automated release checks use synthetic or disposable state and do not prove behavior against a live installed application or production credentials.
