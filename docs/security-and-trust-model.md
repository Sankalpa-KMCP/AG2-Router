# Security and trust model

This document owns security assets, adversaries, trust boundaries, and intended protection properties. Persistence mechanics are in [persistence-and-concurrency.md](persistence-and-concurrency.md); HTTP shapes are in [api-contracts.md](api-contracts.md).

## Protected assets

- The live Antigravity credential stored under the canonical WinCred target.
- Vaulted account sessions protected by DPAPI.
- Account identity and active-account metadata.
- Process identity, command-line tokens used for local RPC, and discovered loopback ports.
- Routing and switch intent, especially any operation that changes credentials or restarts Antigravity.
- User configuration, diagnostics, and generated release artifacts that could accidentally disclose secrets.

## Threats in scope

- Remote network access to the dashboard server.
- Browser-origin or DNS-rebinding attempts against loopback endpoints.
- Accidental credential disclosure in logs, exceptions, command lines, DTOs, or release payloads.
- Mutation of the wrong process after PID reuse or executable drift.
- Partial enrollment/switch operations and unsafe rollback.
- Corrupted, substituted, or cross-account vault records.
- Install/uninstall actions reaching outside the owned binary directory.

The application does not claim to defend secrets from a fully compromised same-user Windows session. DPAPI CurrentUser and WinCred rely on Windows user-scoped facilities, but repository evidence does not establish the loopback server's reachability or isolation behavior across different Windows users/sessions.

## Loopback and WebView2 boundary

- Kestrel listens on IPAddress.Loopback and rejects non-loopback RemoteIpAddress values.
- Host is restricted to 127.0.0.1 or localhost.
- API requests reject foreign Origin and Sec-Fetch-Site: cross-site. A missing Origin remains accepted for same-user native callers. Both the shipped .NET host and the Node reference server enforce these browser mutation checks centrally across all API mutation endpoints before reading or parsing request bodies or executing state changes.
- Ordinary status and health responses do not issue the switching token. The dedicated POST /api/switching/intent path requires a custom intent-request header and the browser-origin checks before issuing it.
- Explicit switching requires the per-process token plus confirm: true.
- Loopback responses emit a restrictive Content-Security-Policy (`default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'`) across native and reference UI and API endpoints.
- WebView2 is locked to the trusted local application origin: `NavigationStarting` blocks and cancels top-level navigation to foreign origins, external ports, or untrusted schemes (`javascript:`, `data:`, `file:`, etc.); `NewWindowRequested` is handled and blocked; and deliberately cancelled navigations do not trigger connection failure recovery.

INTENDED SECURITY PROPERTY:

Only a local, intended dashboard interaction should be able to request sensitive mutation. Loopback reachability alone does not establish intent or user identity.

LIMITATION: Missing Origin is accepted for same-user local native callers. The switch token strengthens credential-changing intent but is not general user authentication. See [known-limitations.md](known-limitations.md).

Remote-network exposure is blocked by loopback binding plus remote-address and Host checks. Same-user, non-browser local processes can reach loopback without general API authentication; most mutations do not require the switch-intent token. Cross-Windows-user/session loopback reachability is UNKNOWN from repository evidence. Do not claim either proven cross-user compromise or proven multi-user isolation without targeted OS-level validation.

Primary evidence: LoopbackServer.cs, src/server/server.ts, LoopbackServerTests, LoopbackServerAccountApiTests, LoopbackSwitchApiTests, LoopbackRouterApiTests, and test/server.test.ts.

## WinCred and DPAPI roles

WinCred is the live Antigravity credential boundary. The current native runtime has both WindowsWinCredReader and WindowsWinCredWriter.

- Enrollment reads the current credential for the observed account.
- Switching performs controlled writes to apply a vaulted target session.
- Rollback may write the prior credential only after checking that current state still matches the transaction-applied credential.

Therefore, WinCred access is not read-only in the shipped product: the native switch coordinator performs controlled writes and conditional rollback through WindowsWinCredWriter, bounded by the transaction rules in [persistence-and-concurrency.md](persistence-and-concurrency.md).

The session vault encrypts each framed account payload through Windows DPAPI CurrentUser. Framing binds the plaintext to account ID, target, and version; mismatches fail closed. Envelope records store individually encrypted payloads and deliberately add no outer encryption layer, keeping corruption detection and atomic updates simple and robust. DPAPI protects stored bytes but does not prove that an account/process transition is correct—that proof belongs to the transaction and telemetry checks.

Account API hasVaultedSession is derived from successful vault decryption and framing validation, not a metadata flag or ciphertext-record presence. Temporary returned byte buffers are zeroed after this availability check; complete managed-memory erasure remains outside this guarantee.

Primary evidence: WindowsWinCredReader.cs, WindowsWinCredWriter.cs, WindowsDpapiProvider.cs, SessionVault.cs, AccountEnrollmentService.cs, and NativeAccountSwitchCoordinator.cs.

## Process provenance

INTENDED SECURITY PROPERTY:

- Process discovery is untrusted until provenance checks establish the expected executable and generation.
- A transaction must revalidate process identity before destructive steps.
- PID reuse, start-time change, executable-byte drift, unexpected rediscovery, or stale telemetry generation must block mutation or verification.
- Rollback must not stop a process generation the transaction does not own.

Primary evidence: AG2ProcessDetector, ProcessProvenanceValidator, WindowsProcessInspector, WindowsAG2ProcessLifecycle, WindowsAG2ProcessLifecycleTests, ProcessProvenanceValidatorTests, and switch coordinator tests.

## Secret handling

- AG2Security sanitizes known sensitive flags, headers (including multi-parameter Digest and line-bounded Authorization headers containing internal brackets, braces, or punctuation, terminating strictly at line boundaries or EOF), and JSON/diagnostic key/value patterns across structural separator whitespace (spaces, tabs, newlines) before messages cross diagnostics or API boundaries.
- RPC calls keep discovered tokens inside the adapter boundary and redact error snippets.
- Credential buffers are copied narrowly and zeroed where practical. Managed-runtime zeroing is best-effort, not guaranteed erasure of all transient strings or copies.
- Public account/status DTOs must contain metadata and telemetry only, never credential material.
- Release packaging scans selected text assets for known secret markers, but that scan is not a complete secret detector.

Primary evidence: AG2Security.cs, AG2RpcClient.cs, Windows DPAPI/WinCred implementations, packaging script, and security/RPC tests.

## Daemon RPC transport security

INTENDED SECURITY PROPERTY:

- The sensitive daemon authentication token (`x-codeium-csrf-token`) must be transmitted strictly to the verified local loopback endpoint (`127.0.0.1`) discovered via process provenance.
- Production RPC HTTP handlers (`SocketsHttpHandler`) explicitly disable automatic redirect following (`AllowAutoRedirect = false`) and explicitly bypass all system/environment HTTP proxies (`UseProxy = false`). Loopback communication does not rely on ambient proxy bypass rules or platform-dependent loopback exemption heuristics.
- All request dispatches employ header-first completion semantics (`HttpCompletionOption.ResponseHeadersRead`). All redirect-class responses (HTTP 300, 301, 302, 303, 307, 308) are treated as transport failures and rejected immediately from headers before response bodies are buffered or consumed, preventing memory exhaustion, hangs on delayed bodies, or credential forwarding, and never following `Location` targets or logging secrets.
- A single bounded cancellation deadline (defaulting to 5 seconds for standard RPCs, 3 seconds for port probes) spans request dispatch, response headers, redirect validation, and all permitted nonredirect response-body reads. When headers complete via `HttpCompletionOption.ResponseHeadersRead`, subsequent response-body reads execute under the exact same linked cancellation token budget. Stalled or withheld response bodies cannot delay completion past the configured 5-second RPC deadline, while immediate header-first redirect rejection is preserved.
- Requests attach `x-codeium-csrf-token` per-request; tokens are never placed on global or shared `DefaultRequestHeaders`.
- Destination URIs are validated before request construction: non-loopback hosts, out-of-range ports, and unexpected schemes are rejected fail-closed.
- Daemon port probing during process discovery refuses 3xx responses, preventing false daemon detection or token leakage during scans.

Primary evidence: AG2RpcClient.cs, AG2RpcTransportSecurityTests.cs, AG2ProcessDetector.cs, UsageInstanceDiscovery.cs, and AG2RpcClientTests.

## Fail-closed behavior

The intended default for ambiguous identity, unavailable activity, unsafe process provenance, corrupted vault state, and uncertain rollback is to refuse further automatic mutation. Manual-recovery state exists for cases where restoration cannot be proven.

Do not weaken a fail-closed branch merely to keep routing available. Changes require focused negative-path and concurrency tests.

## Repository and test guardrail

Default development and tests must use fakes and task-owned temporary directories. Live Antigravity, real WinCred, production DPAPI data, user vault/account files, registry state, and installed application state are protected resources. The opt-in boundary is documented in [testing.md](testing.md).
