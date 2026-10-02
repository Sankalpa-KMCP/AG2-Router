# AG2 Router v0.4.1-rc.1

**Version:** 0.4.1-rc.1
**Release channel:** Prerelease for supervised user testing
**Packaged target:** Windows x64

This candidate packages the current native account-routing implementation for manual evaluation. It is not a stable-release readiness certification.

## Candidate behavior

- Automatic routing uses explicit workload-model intent and coherent durable candidate observations, then verifies live target identity and quota before metadata commit.
- Interrupted switching blocks further mutation until proof-based recovery establishes coherence; successful recovery may require application restart.
- The dashboard provides model-specific settings, candidate evidence, and recovery guidance while preserving unknown quota and unsaved configuration edits.

## Installation and testing

Download `AG2Router-Setup-v0.4.1-rc.1-win-x64.exe` for the per-user installer, or `AG2Router-v0.4.1-rc.1-win-x64.zip` for the archive installation path. Verify both against `SHA256SUMS.txt`.

Begin with monitoring and manual switching. Configure the exact workload model before enabling Auto Switch. Inactive accounts need a prior coherent quota observation; enrollment alone does not establish eligibility. When recovery requires restart, exit from the system tray and launch again.

## Limitations

- Application and installer are unsigned; checksums verify bytes rather than publisher identity.
- Installed Windows/WebView2 behavior, live account switching, and extended stability remain subject to user testing.
- Inactive-account evidence is conservative cached observation, not a live quota query. Unknown or stale evidence blocks automatic eligibility.
- The loopback API does not promise isolation from other processes running as the same Windows user; cross-user/session isolation is unverified.
- Existing synthetic installer tests do not certify every real upgrade or uninstall scenario.

Persistent user data is separate from the binary installation directory. Keep a known-good release available and stop testing if identity or credential/data integrity becomes uncertain.
