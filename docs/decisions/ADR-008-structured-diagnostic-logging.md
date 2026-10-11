# ADR-008: Structured Privacy-Safe Diagnostic Logging Architecture

Status: ACCEPTED

Date: 2026-10-10

## Context

When Antigravity RPC protocol mismatches and silent connection failures occurred, the application lacked structured, actionable diagnostic evidence to identify failure causes without compromising security. The pre-existing diagnostic files (`app.log` and `js_runtime_errors.json`) consisted of unstructured text or isolated client error logs without schema guarantees, correlation tracking, failure classification, or cross-subsystem visibility.

To make failure causes discoverable and auditable while maintaining strict zero-data-leakage and zero-dependency guarantees, a structured diagnostic foundation was planned.

## Decision

Implement an application-owned, asynchronous, bounded structured diagnostic logging foundation (P0) and wire its initial end-to-end paths across application bootstrap, WebView2 navigation and lifecycle recovery, Antigravity RPC discovery and port probing, and telemetry/usage collection (P1a).

The architecture consists of:
1. **Closed Schema v1 Event Model (`DiagnosticEvent`)**:
   - Resides in `AG2Router.Core.Diagnostics`.
   - Immutable records with allowlisted, typed properties: `schemaVersion` (1), `timestampUtc` (ISO 8601 UTC with 'Z'), `sequence` (monotonic counter in a single run-scoped domain), `eventInstanceId`, `severity`, `eventId`, `eventName`, `component`, `operation`, `runId`, `correlationId`, `operationId`, `outcome`, `failureCategory`, `safeMessage`, `recoveryStatus`, `affectedFeature`, and optional contextual fields (`parentOperationId`, `causeEventId`, `durationMs`, `attempt`, `protocol`, `httpStatus`, `failurePhase`, `exceptionType`, `hresult`, `originSymbol`, `nextActionCode`, `applicationVersion`, `sourceCommit`).
   - Free-form property bags, nested object graphs, and unstructured JSON payloads are strictly prohibited.
   - Serialized JSON event payload budget is bounded to 8 KiB (8,192 bytes). Note: this represents the logical serialized payload budget, distinct from the total managed-memory ceiling of the application.

2. **Pre-Ingestion Privacy by Construction (`DiagnosticSanitizer`)**:
   - Enforces allowlists and defense-in-depth sanitization *before* events enter in-memory buffers or persistent queues.
   - Closed catalog metadata provides fixed safe event descriptions (`SafeMessage`). Callers cannot inject arbitrary sentences, console arguments, or query strings into event messages.
   - Redacts CSRF tokens, bearer JWT tokens, session credentials, passwords, cookies, email addresses, DPAPI ciphertext, raw query parameters, non-user absolute paths, and prompt/conversation turn structures. Note: while extensive synthetic tests falsify secret leakage, privacy guarantees rest on architectural construction and allowlists rather than universal claims from finite test suites alone.
   - Exception handling is strictly restricted to allowlisted type names, inner exception chains bounded to depth <= 3, and numeric HRESULT codes. Raw exception messages, stack traces, and `Exception.Data` are never serialized into diagnostic events.

3. **Bounded Asynchronous Persistence (`BoundedJsonlDiagnosticSink`)**:
   - Application-owned bounded channel (capacity 512, memory bound 4 MiB) with non-blocking best-effort caller semantics.
   - Dedicated background worker sequentially persists events to UTF-8 JSON Lines (JSONL) files in `%LOCALAPPDATA%\AG2-Router\diagnostics\owned-segments-v1\`.
   - Bounded rotation: 4 MiB per segment (`diag_YYYYMMDD_HHMMSS_seq.jsonl`).
   - Bounded retention: maximum 8 owned segments, 32 MiB total size bound, and 14-day retention policy target. Retention operations represent policy targets rather than guaranteed immediate physical deletion when files are locked by external processes; failed deletions are truthfully tracked in `RetentionFailureCount`.
   - Safe owned-file deletion (Approach B): uses native Windows handle-based deletion (`WindowsHandleFileDeletion`) executing `SetFileInformationByHandle` on the exact validated file handle without pathname re-resolution, eliminating TOCTOU hazards and avoiding `File.Delete(path)`.
   - Conclusive segment ownership validation: couples verification to the open handle, requiring valid format, schema version, expected segment ID, and matching `RunId` from the active run context. Cross-run or unverifiable candidates are preserved untouched rather than risking data destruction.
   - Cursor progression: maintenance passes progress beyond previous candidate cursors to prevent starvation when foreign or locked files precede owned segments.
   - Bounded inspection bounds first-record reading to 4 KiB and incrementally enumerates candidates.
   - Bounded in-memory recent ring buffer (256 items, 2 MiB memory bound) for low-overhead health inspection.
   - Bounded shutdown flush budget (default 2 seconds) bounds caller waiting, while the background worker exclusively owns and closes stream handles in `finally`.
   - Single deadline in `FlushAsync`: a single linked cancellation token bounds both barrier admission and wait, preventing sequential deadline accumulation.
   - Truthful persistence accounting: `TotalPersisted` strictly tracks flushed/durable events on disk, incremented only after disk commits succeed.
   - Idle retention: worker periodically checks and rotates aged active segments and runs retention cleanup even when no new events are emitted.

4. **Zero External Logging Dependencies**:
   - Pure BCL implementation (`System.Text.Json`, `System.Threading.Channels`, `System.IO`, and native Windows handle APIs).
   - Zero third-party logging packages (no Serilog, NLog, or Microsoft.Extensions.Logging dependencies).

5. **Subsystem Wiring (P1a)**:
   - **Bootstrap & Shutdown**: `StartupStarted`, `StartupCompleted` (with duration), `StartupFailed`, `ShutdownStarted`, `ShutdownCompleted`, and `UnhandledFailure`.
   - **WebView2 & JavaScript**: `WebViewInitializationFailed`, `NavigationFailed`, `WebViewProcessFailed`, `WebViewRecoveryAttempted`, `WebViewRecoverySucceeded`, and `WebViewRecoveryExhausted`, correlated via `_recoveryCorrelationId`. Legacy error capture preserves `js_runtime_errors.json` writes while routing sanitized events to `JavaScriptException` or `JavaScriptCaptureRejected`.
   - **Antigravity RPC Discovery & Transport**: `ProbePortDetailedAsync` classifies port probe outcomes (`ProtocolMismatch`, `TlsHandshakeFailure`, `ProbeTimeout`, `Cancelled`, `IncompleteResponseBody`, `RejectedRedirect`, `ServerError`, `TransportError`), emitting distinct events without logging ports or CSRF tokens. `DiscoveryFailed` and `DiscoveryRecovered` record daemon state transitions, correlated across probe and recovery passes.
   - **Telemetry & Usage Accounting**: `TelemetryUnavailable` / `TelemetryRecovered` and `UsagePartialCollection` / `UsageRecovered` track service degradation and recovery, propagating correlation across degraded-to-recovered transitions. Telemetry recovery requires affirmative usable quota evidence (distinguishing valid zero from empty or all-unknown observations). `UsageCollectorDiagnostics` returned under `GET /api/usage/summary` includes an additive `FailureCategory` string field to distinguish failure modes without string matching.

6. **Single-Instance Safety and Restart Ownership Transfer**:
   - Early startup and secondary instances buffer events in `MemoryDiagnosticSink`.
   - Single active writer lock (`.active_writer.lock`) is acquired and primary persistent sink promoted strictly after `SingleInstanceGuard` acquisition.
   - On shutdown, the active writer lease is flushed and relinquished before releasing `SingleInstanceGuard`.
   - Bounded asynchronous lease reacquisition (`TryAcquireWriterLockOnce`): if the outgoing instance holds the lock beyond the initial wait window, the admitted primary can reacquire writer leasing during worker cycles rather than remaining permanently memory-only.

## Rationale

- **Deterministic Causality**: Strict categorization (`DiagnosticFailureCategory`, `RecoveryStatus`, `AffectedFeature`) allows instant identification of whether an issue is an environment misconfiguration, an external Antigravity failure, a network transport error, or an internal bug.
- **Fail-Safe Operation**: A logging failure, saturated channel queue, or disk I/O error must never degrade or disrupt core routing, vault, or UI operations.
- **Privacy and Security by Construction**: Because sanitization and fixed catalog descriptions are enforced before event instantiation, sensitive tokens or private paths cannot reside in memory buffers or be written to disk.
- **Bounded Resource Consumption**: Exact memory and disk ceilings prevent runaway logging from consuming excessive system resources.

## Alternatives considered

1. **Adopt Serilog / NLog**:
   - *Rejected*: External logging frameworks introduce external supply-chain dependencies, reflection overhead, and risk unbounded serialization of exception and object trees.
2. **Expand legacy `app.log` and `js_runtime_errors.json`**:
   - *Rejected*: Plaintext logs lack schema safety and machine queryability. Modifying existing formats risks breaking existing troubleshooting workflows and log collectors.
3. **Open schema with dynamic key-value properties**:
   - *Rejected*: Free-form property bags inevitably lead to schema drift, unredacted secret leaks, and unbounded payload sizes.

## Consequences

- **Positive**:
  - Full machine-readable audit trail of application startup, runtime errors, and recovery cycles.
  - Clear distinction between protocol mismatches, caller cancellations, timeouts, and transport errors.
  - Bounded resource utilization guaranteed at all times.
  - Sensitive user data and credentials excluded from diagnostics by construction.
- **Negative / Trade-offs**:
  - Adding new diagnostic events requires registering them in `DiagnosticEventCatalog` and `DiagnosticEnums`.

## Compatibility/migration impact

Legacy files `app.log` and `js_runtime_errors.json` remain active and are written alongside the new diagnostic sink. No existing log file is modified, deleted, or converted. The additive `FailureCategory` field on `UsageCollectorDiagnostics` is backward-compatible with frontend wire contracts.

## Related

- `docs/runtime-architecture.md`
- `docs/security-and-trust-model.md`
- `docs/invariants.md`
- `docs/api-contracts.md`
- `docs/usage-accounting.md`
- `docs/decisions/ADR-001-switch-transaction-journal.md`
