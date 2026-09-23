# AG2 Router v0.2.2 Release Notes

**Version:** 0.2.2
**Target Platform:** Windows 10 (1809+) / Windows 11 (64-bit)

## Highlights

- Quota reporting preserves unknown values, observed exhaustion, and separate reset pools. Automatic routing requires proven capacity for the relevant model and fails closed on uncertain activity or account identity.
- Account enrollment, deletion, and switching coordinate lifecycle ownership. Uncertain vault, metadata, or rollback outcomes require manual recovery rather than allowing another mutation.
- The dashboard protects polling and configuration edits from stale responses. Manual account switching remains available; unsupported Plan Switch controls are absent.
- WebView recovery is bounded across navigation, initialization, and process failures, with stale events isolated from replacement controls.
- Loopback status does not disclose switching tokens, and browser-origin checks protect mutation requests.
- The installer stages an existing installation before replacement and checks Win32 move results explicitly during upgrade and rollback. Uninstall fails closed if application shutdown cannot be proven.

## Download and integrity

The release includes `AG2Router-Setup-v0.2.2-win-x64.exe`, `AG2Router-v0.2.2-win-x64.zip`, and `SHA256SUMS.txt`. Verify the downloaded files against the checksum manifest. The repository workflow does not establish Authenticode signing; Windows SmartScreen may warn about an unrecognized publisher.

User account metadata, encrypted vault data, and other persistent state remain outside the application-binary install directory. Automated release checks use synthetic or disposable state and do not prove behavior against a live installed application or production credentials.
