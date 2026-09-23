# AG2 Router v0.2.1 Release Notes

**Version:** 0.2.1
**Target Platform:** Windows 10 (1809+) / Windows 11 (64-bit)

## Highlights

- Quota reporting keeps missing or non-finite values unknown, preserves observed exhaustion and separate reset pools, and requires relevant-model capacity evidence before automatic routing.
- Account enrollment, deletion, manual switching, and automatic switching coordinate lifecycle ownership. Uncertain vault, metadata, or rollback outcomes fail closed and require manual recovery rather than permitting another mutation.
- The dashboard protects polling and configuration edits from stale responses. Unsupported Plan Switch controls are removed; manual account switching remains available.
- WebView recovery is bounded across navigation, initialization, and process failures, with stale events isolated from replacement controls.
- Loopback status responses do not expose switching tokens; browser-origin checks protect mutation requests.
- Release packaging rebuilds the frontend. Install/upgrade stages binaries before replacement, and uninstall stops when application shutdown cannot be proven.

## Download and integrity

The release provides `AG2Router-Setup-v0.2.1-win-x64.exe`, `AG2Router-v0.2.1-win-x64.zip`, and `SHA256SUMS.txt`. Verify downloaded file hashes against the manifest. The repository workflow does not establish Authenticode signing; Windows SmartScreen may warn about an unrecognized publisher.

User account metadata, encrypted vault data, and other persistent state are outside the application-binary install directory. Automated release checks use synthetic or disposable state and do not prove behavior against a live installed application or production credentials.
