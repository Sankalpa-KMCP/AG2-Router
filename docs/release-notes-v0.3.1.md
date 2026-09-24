# AG2 Router v0.3.1 Release Notes

**Version:** 0.3.1
**Target Platform:** Windows 10 (1809+) / Windows 11 (64-bit)

## What changed since v0.3.0

- Stabilized Windows CI async tests for enrollment, removal, vault, routing, and switching. The tests now coordinate on explicit entry signals where practical and allow more time for synthetic timeout and harness operations under CI load.
- The stabilization changed tests only. Production routing, account, vault, concurrency, and cancellation behavior is unchanged from v0.3.0.

## Installation and state

The existing per-user, in-place installation contract is unchanged. Installer binaries remain under `%LOCALAPPDATA%\Programs\AG2Router`; persistent application state remains outside that directory under `%LOCALAPPDATA%\AG2-Router` and is preserved by the existing upgrade and uninstall paths.

## Download and integrity

The release includes `AG2Router-Setup-v0.3.1-win-x64.exe`, `AG2Router-v0.3.1-win-x64.zip`, and `SHA256SUMS.txt`. Verify downloaded files against the checksum manifest. The repository workflow does not establish Authenticode signing. Automated packaging and upgrade checks use synthetic or disposable state; they do not prove behavior against a live installed application or production credentials.
