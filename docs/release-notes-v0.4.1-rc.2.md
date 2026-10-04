# AG2 Router v0.4.1-rc.2

**Version:** 0.4.1-rc.2
**Release channel:** Prerelease for supervised user testing
**Packaged target:** Windows x64

This candidate packages the completed AG2 Router remediation and conversation usage accounting milestones for manual evaluation. It is a testing prerelease / release candidate, not a stable-release readiness certification.

## Highlights

- **Hardened Lifecycle, IPC, Contracts & Account Validation:** Session-isolated IPC mutex/pipe, truthful switching contracts, safe lease disposal with PathLock preservation, robust telemetry polling and AutoRouter lifecycle disposal gates, and bounded account text validation.
- **Conversation Usage Accounting:** Privacy-preserving token usage tracking based on authoritative cascade generator metadata (`(cascadeId, responseId)` tuples).
- **Durable Usage Persistence:** Monthly segmented ledger storage (`usage-YYYY-MM.json`) with cross-process file leases and self-repairing in-memory index caches.
- **Multi-Instance Collection:** Live discovery and collection across multiple running Antigravity Language Server instances without port cross-talk.
- **Strict Account Attribution:** Verified before/after identity sandwich invariant; unmanaged, changing, or ambiguous identities are recorded truthfully as `Unattributed`.
- **Usage API & Dashboard:** Loopback endpoints (`/api/usage/summary`, `/api/usage/timeseries`, `/api/usage/models`, `/api/usage/status`) and integrated Svelte dashboard section with time-range filtering, model breakdowns, and generation-tracked request race prevention.

## Validation

- **Native Test Suite:** 1245 passed, 0 failed, 1 intentional live-gate skip (`AG2LiveParityTests`).
- **Node Test Suite:** 419 passed, 0 failed, 1 intentional live-gate skip (`AG2WinCredReader`).
- **Code & Security Review:** Full independent review completed with `REVIEW_CLEAR`.

## Known Usage Limitations

- **Conversation-Call Coverage Only:** Accounting covers conversation trajectory generator calls; inline completions and non-cascade calls are outside provider telemetry boundaries.
- **No Provider Per-Call Historical Timestamps:** Trajectory modification times are not per-call execution timestamps; historical backlog calls observed on initial discovery are classified as `HistoricalUnknown` and excluded from observation-time bucket charts.
- **Provider Retries:** Provider retries within a conversation are not additively billed.
- **No Invented Credits:** Token usage reflects observed provider metrics; no synthetic credit balances are fabricated.

## Installation and Testing

Download `AG2Router-Setup-v0.4.1-rc.2-win-x64.exe` for the per-user installer, or `AG2Router-v0.4.1-rc.2-win-x64.zip` for the archive installation path. Verify downloaded files against `SHA256SUMS.txt`.

- The application and installer are unsigned; Windows may display an unknown-publisher warning.
- The dashboard requires Microsoft Edge WebView2 Runtime.
- Application data is stored separately under `%LOCALAPPDATA%\AG2-Router`. Keep a known-good backup and discontinue testing if identity or credential integrity becomes uncertain.

The stable v0.4.0 release remains unchanged.
