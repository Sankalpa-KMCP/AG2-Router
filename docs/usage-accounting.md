# Usage accounting

Authoritative document for the usage-accounting domain and its durable ledger. Current implementation covers only the domain model and persistence (stage U1). The Antigravity collector (U2), aggregation and HTTP API (U3), and dashboard views (U4) are **not yet implemented**; nothing in this document describes live collection or an HTTP surface.

## Source scope

One durable usage record represents exactly one **observed conversation-model call** — a model invocation inside an Antigravity cascade conversation, identified by the provider's `(cascadeId, responseId)` tuple. The ledger MUST NOT be presented as "all Antigravity usage": non-conversation completions and any usage outside cascade conversation calls are not covered. Live validation of the source (Antigravity IDE 1.107.0 `GetCascadeTrajectoryGeneratorMetadata`) established this scope; later stages must not widen the claim without new validation.

## Accounting semantics

- **Conversation tokens** per call = `InputTokens + OutputTokens`, over unique calls. This is the headline "tokens used (conversations)" metric.
- **Cache reads are additive to uncached input** in the provider's own accounting (a validated live call reported `inputTokens = 3372` with `cacheReadTokens = 16307`). Cache reads are therefore a **separate counter** and are never silently added to the headline metric. `ProcessedInputTokens = InputTokens + CacheReadTokens` exists only as an explicitly named derived value, and only when cache reads were reported — absence is not proven zero.
- **Output composition**: live validation observed `OutputTokens = ResponseOutputTokens + ThinkingOutputTokens`. The provider's `OutputTokens` remains authoritative; the component counters are optional and a mismatch between the sum and the authoritative value is preserved and diagnosable (`OutputComponentSum`, `HasOutputComponentMismatch`), never repaired. Any future aggregation uses `OutputTokens` as the headline output value.
- **No credits, no quotas, no percentages**: per-call credits were absent in the validated build and are not invented; persisted values are provider-reported token counters only. No percentage-to-token or credit-to-token conversion exists.
- **Retries**: `retryInfos` duplicated main usage in every validated sample; retry usage is excluded from totals and is not persisted. Failed-retry accounting is unresolved and out of scope.
- Token counters are non-negative int64 values. No floating-point token storage.

## Call identity, dedup, and privacy

- The durable call key is `SHA-256("AG2U1K1" + length-prefixed(cascadeId) + length-prefixed(responseId))` (4-byte little-endian UTF-8 byte-length prefixes; lowercase hex). Length prefixing makes concatenation ambiguity impossible, and the same tuple always yields the same key, so dedup is deterministic across language-server instances and across ingestion months.
- **Raw cascade IDs and response IDs are not persisted.** The key is the only identifier in durable storage; no prompt text, system prompt, prompt sections, assistant text, conversation titles, tool schemas, raw provider responses, tokens (auth/CSRF), email, command lines, process paths, or ports may enter the ledger.
- `executionId` is not unique per model call and is not used as an identity or dedup key.

## Account attribution

Attribution is explicit, never inferred:

- `VerifiedObservation` — attribution backed by explicit AG2 Router evidence at observation time; requires a managed **account ID** (the internal `acc_…` id, not email).
- `Unattributed` — no defensible attribution; the account id is absent.

There is no per-call owner field in the upstream data, and no durable historical account-transition ledger exists that could reconstruct past ownership. Attribution is therefore **immutable after first persistence**: re-observing a call never upgrades it, and historical calls are never assigned to the currently active account. "All Accounts" totals include unattributed records, so the sum of per-account totals may be less than the global total; that remainder is explicit and must not disappear.

## Time attribution

- `ObservationTime` — the call was first observed during continuous forward collection; its `FirstObservedAtUtc` may later be bucketed into trends with poll-resolution semantics.
- `HistoricalUnknown` — the call was discovered during historical/backfill/recovery enumeration; it contributes to totals only.

The upstream source provides no per-call event timestamp (`chatStartMetadata.createdAt` was null), so **no record claims an event time** and no `OccurredAtUtc` field exists. `FirstObservedAtUtc` is always the ledger's first-observation instant, persisted in UTC. The ledger's physical month segmentation follows this observation instant and is a storage boundary only — it is never a provider event date.

## Durable store

- Layout: `DurableUsageCallLedger` stores one JSON segment per UTC observation month under the ledger directory (default `%LOCALAPPDATA%/AG2-Router/data/usage`, or `DATA_DIR/usage`), file `usage-calls-YYYY-MM.json`. Segmentation bounds rewrite cost to the segments a batch touches; years of accumulation produce many bounded segments, with no automatic retention or pruning and no silent caps.
- Schema: magic `AG2USAGELDGR`, `schemaVersion` 1, `segmentMonth` (must match the file name), `updatedAt`, and the `calls` array. Future stages extend via new schema versions; v1 documents with unknown fields are tolerated, but version or magic mismatches fail closed.
- Writes use `DurableFileWriter` atomic replacement inside one cross-process transaction per ledger directory. Lock order is fixed and released in reverse: process-local gate (`PathLockRegistry` on `<ledgerDir>.ingest`) → global ledger lease (`CrossProcessFileLease`, delete-on-close, on the same path) → per-segment leases (sorted) → per-segment path locks (sorted, innermost). The global lease makes refresh, duplicate/conflict validation, planning, and commit a single critical section shared by every writer process, so independent processes can never plan the same new call key into different month segments. No code path takes segment locks before the global lease.
- The dedup index is rebuilt inside that critical section with per-segment (last-write, length) freshness validation: durable state, never a process-local memory snapshot, is authoritative for duplicate and conflict decisions. Cross-segment duplicate keys detected during the refresh fail closed as corruption.
- A batch is atomic as one logical unit: conflicts or invalid input abort the whole batch before any write; once committed, cancellation cannot tear it. A hard crash between two segment writes of a month-spanning batch may leave a per-segment prefix (each segment stays structurally valid and globally unique); restart deterministically discovers the committed subset, and re-ingesting the original batch idempotently completes the missing records without duplicating or manufacturing conflicts.
- Queries take only per-segment path locks and read files that writers replace atomically, so every reader — in-process or in another process — observes either the complete old or the complete new segment document, never partial JSON; a reader racing a replacement sees at most a brief sharing-violation window that the read path retries with the `CrossProcessFileLease` cadence before failing.
- Duplicates: identical accounting payload for a known call key is an idempotent no-op (durable bytes unchanged; first observation time wins). Conflicting payloads fail closed with `UsageLedgerConflictException` and the prior record intact; no last-writer-wins, max/min, or merge resolution exists.
- Corruption or unsupported schema fails closed (`UsageLedgerCorruptionException`): the corrupt bytes are preserved, never replaced with an empty ledger, and all writes are blocked until remediated.
- Scale: designed for tens of thousands of records and years of accumulation. The dedup index holds call keys only; queries read segments directly without requiring a full in-memory copy of the ledger.

## Historical account references

Account attribution is a durable historical ID reference, not a live foreign key. Removing an account from the account store must not destroy or hide its historical usage; records attributed to removed accounts remain readable, and no U1 change modifies account deletion behavior.

## Collection (U2)

`UsageCollectionService` runs one collection cycle at a time on an internal default cadence (30 seconds; not user-configurable). Each cycle:

1. Discovers **every** validated Antigravity language-server instance (`UsageInstanceDiscovery` + `ProcessProvenanceValidator.ValidateAll`): the same provenance rules as telemetry, applied to all candidates instead of selecting one, deduplicated by process, with per-instance loopback-port resolution and authenticated Connect-RPC probes. Unreachable or vanishing processes are skipped; one broken instance never erases another's results.
2. Observes each endpoint's account identity before fetching (GetUserStatus → email → internal managed account id via the account store; emails never persist).
3. Lists trajectory summaries per instance and applies change gating: a conversation is refetched only when its (status, step count, last-modified) signature changes or the marker is ambiguous — a missed call is worse than an unnecessary RPC. Cursors are in-memory; after a restart every conversation is refetched and the ledger deduplicates.
4. Fetches `GetCascadeTrajectoryGeneratorMetadata` per changed conversation and maps the minimal accounting subset through tolerant typed extraction. Prompt-adjacent payload fields (systemPrompt, promptSections, tool schemas) are never modeled, logged, or persisted; malformed individual entries are skipped with diagnostics and never fabricate counters.
5. Applies the attribution rules below and ingests one combined batch; global dedup merges the same conversation appearing on multiple instances.

Collector failures degrade to sanitized diagnostics (`UsageCollectorDiagnostics`); persisted history remains viewable whenever Antigravity is not running and collection failure never zeroes totals.

## Attribution continuity (U2A)

Continuity is scoped to each detected language-server observation source, not the collector process. An endpoint's first successful cycle in the running collector — a newly appearing instance, a returning instance after an unobserved gap, or a collector restart — is a conservative **baseline**: its newly seen calls are HistoricalUnknown + Unattributed because nothing proves when they ran or who ran them. After that baseline, the endpoint's newly seen calls receive ObservationTime + VerifiedObservation(account) only when the same managed account was verified at that endpoint immediately before the fetch, immediately after it, and at the end of that endpoint's previous cycle. Time and account attribution rest on separate evidence: a forward-observed call under an unverifiable account is Unattributed but still honestly ObservationTime.

A successful discovery that no longer lists a previously known endpoint breaks that endpoint's continuity (it will be re-baselined when it returns); a failed or ambiguous discovery changes nothing, so transient enumeration failures cannot silently reset honest observation windows. Any identity change — a router-controlled switch, a manual account change, or a new language-server process identity — costs one conservative cycle instead of manufacturing attribution. The durable checkpoint (`UsageCollectorStateStore`, versioned, fail-closed) records the last collection instant and verified account ids for operators; continuity decisions themselves rest on the per-source evidence described above, so unreadable checkpoint state can neither create nor revoke attribution. The dashboard's usage requests are generation-controlled: only the latest account/range request may update the view, and component teardown invalidates in-flight requests.

## Aggregation and HTTP API (U3)

`UsageAggregationService` scans the ledger per request with checked arithmetic. Scopes: `all` (every unique call, including unattributed), `account:{id}` (verified attribution only; durable historical ids remain queryable after account removal), `unattributed` (the explicit remainder — per-account totals may sum to less than All Accounts, and that gap is always visible). Headline totals count conversation tokens (input+output), with cache reads reported separately including reported-vs-unreported call counts, response/thinking component sums, and an output-component mismatch diagnostic. Time series bucket only ObservationTime calls in UTC (hourly for 24h, daily for 7d/30d, monthly for all); HistoricalUnknown usage stays in lifetime/model/account totals with its own totals block so the timeline never fabricates event dates.

Native routes on the loopback server (read-only GET): `/api/usage/summary?scope=...`, `/api/usage/timeseries?range=24h|7d|30d|all&scope=...`, `/api/usage/models?scope=...`. Responses contain aggregate counters and account ids only — never cascade ids, response ids, prompt content, ports, or process details. Ledger corruption returns a sanitized 503 ("integrity issue"); persisted totals remain viewable when the collector is unavailable. The Node reference server deliberately does not implement usage and answers `/api/usage/*` with 501; the dashboard renders this as an explicit unsupported-backend state rather than synthesized zeros.

## Dashboard (U4)

The Usage tab presents "Tokens used (conversations)" with the coverage caption, an account selector (All Accounts / managed accounts / Unattributed), a range selector, summary cards (conversation tokens, input, output, thinking, cache reads — visually separate — and calls, with exact values in tooltips), an observation-time trend, a per-model breakdown with an explicit Unknown-model bucket, a per-account distribution that keeps Unattributed visible, a historical-unknown notice ("included in totals but not in the timeline"), collector status, and honest loading/empty/error/unsupported states. Persisted history remains viewable when Antigravity is not running.

## Stage boundary

Implemented: the complete native feature — domain model and durable ledger (U1/U1A), multi-instance collection with change gating and typed privacy-minimal parsing (U2), attribution continuity and checkpoint state (U2A), aggregation with the native HTTP API (U3), the dashboard Usage tab with regenerated UI (U4/U4A), and integrated verification (U5). Live runtime validation against a real Antigravity installation was NOT_EXECUTED_PROTECTED_RESOURCE_BOUNDARY: the request/response wire assumptions for `GetCascadeTrajectoryGeneratorMetadata` follow the previously validated runtime facts and are covered by synthetic fixtures, but were not re-verified against a live language server. Evidence: the suites named throughout this document plus `UsageInstanceDiscoveryTests`, `UsageCollectorTests`, `UsageAggregationServiceTests`, `LoopbackUsageApiTests`, `UsageCollectorStateStoreTests`, `test/frontend-usage.test.ts`, and `test/usage-node-unsupported.test.ts`.
