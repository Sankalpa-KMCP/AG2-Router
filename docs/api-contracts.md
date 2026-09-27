# Loopback API contracts

This document owns the HTTP wire contract consumed by the dashboard. The implementation is dotnet/src/AG2Router.App/Server/LoopbackServer.cs; the main client declarations are frontend/src/lib/api/types.ts and client.ts.

Source and tests remain authoritative. The intended contract below is separated from known source drift so incompatible declarations are not normalized by prose.

## Transport and serialization

- The shipped server binds IPAddress.Loopback on an ephemeral port and advertises a 127.0.0.1 URL.
- Allowed Host values are 127.0.0.1 and localhost.
- JSON uses ASP.NET web defaults: public C# record properties serialize as camelCase unless JsonPropertyName specifies otherwise.
- API responses are not cacheable. Common response headers include X-Content-Type-Options: nosniff, X-Frame-Options: DENY, and Cache-Control: no-store.
- Error bodies generally use { "error": "safe message" }. Callers must also use the HTTP status; error text is not a stable machine code.
- DTO additions may be tolerated by JavaScript, but renames, type changes, nullability changes, and enum/result-code changes are wire-contract changes.
- Non-finite quota fractions are emitted as null (unknown) at the status boundary, never as invalid JSON or an endpoint error; observed zero remains zero.

Trust checks and their limits are defined in [security-and-trust-model.md](security-and-trust-model.md).

## Frontend-consumed routes

| Method and path | Request | Success response | Current notes |
| --- | --- | --- | --- |
| GET /api/status | none | SystemStatusDto | Direct object containing status, ag2, router, and nullable telemetry |
| GET /api/accounts | none | AccountsListDto | accounts, totalCount, activeAccountId; each account is enriched with active/vault flags |
| POST /api/accounts | CreateAccountInput | 201 with { account } | Metadata registration; does not itself capture a live session |
| POST /api/accounts/enroll-current | optional EnrollmentOptions | { success, account, isNew, message } | Enrolls observed active identity and credential |
| PATCH /api/accounts/{id} | { alias: string or null } | { success, account } | Whitespace/null clears alias under current storage normalization |
| DELETE /api/accounts/{id} | none | { success, removedId, vaultRecordDeleted } | Shared switch ownership; vault-first removal with compensation on metadata failure |
| GET /api/switching/status | none | { status } | Does not return the switch-intent token |
| POST /api/switching/intent | X-AG2-Intent-Request: 1 | { ready: true } and X-AG2-Switch-Token header | Same-origin browser bootstrap; foreign Origin/cross-site requests rejected |
| POST /api/accounts/{id}/switch-plan | none | 501 | Unsupported legacy endpoint; no dashboard Plan Switch workflow |
| POST /api/accounts/{id}/switch | { confirm: true } plus switch token header | NativeSwitchResult | Status depends on stable string result code |
| GET /api/config | none | { config: RouterConfigDto } | Persisted configuration loaded at startup |
| POST /api/config | complete RouterConfigDto | { success, config } | Validated by RouterConfigValidator before lock or durable write; invalid bounds return 400 |
| GET /api/router/status | none | { router: RouterStatusDto } | Detailed router state |
| POST /api/router/reset-recovery | none | { success, router } | Explicitly clears manual-recovery state when present |
| GET /api/settings/autostart | none | { enabled, supported } | Native registry-backed capability when configured |
| POST /api/settings/autostart | { enabled: boolean } | { enabled, supported } | Changes per-user autostart state |

The Svelte client currently calls the status, account, enrollment, alias, deletion, switch, and config routes. Routes not yet called by frontend/src/lib/api/client.ts are still public loopback contracts and require server tests.

## Core shapes

The following C# records define the backend serialization surface:

- SystemStatusDto, Ag2StatusDto, RouterStatusDto, RouterConfigDto, TelemetryDto, QuotaSnapshotDto, ModelQuotaDto, and CanonicalModelQuotaDto in dotnet/src/AG2Router.Core/Models/AppStatus.cs.
- AccountMetadata and AccountsListDto in AccountMetadata.cs.
- CreateAccountInput, UpdateAccountInput, EnrollmentOptions, and EnrollmentResult in AccountModels.cs.
- NativeSwitchResult, NativeSwitchStatus, ExplicitSwitchRequest, and SwitchResultCodes in SwitchModels.cs.

Account objects must not expose credential blobs, DPAPI ciphertext, WinCred payloads, RPC tokens, or raw process command lines.

AccountMetadata uses lastActiveAt on both backend wire and frontend type. Canonical quota rows expose canonicalKey and displayLabel aliases alongside key and label; frontend consumption accepts those wire fields. NativeSwitchResult.code is a string.

## Mutation semantics

- API browser requests reject foreign Origin and Sec-Fetch-Site: cross-site. A missing Origin remains accepted for same-user native clients.
- Explicit switching requires the per-process X-AG2-Switch-Token obtained from POST /api/switching/intent with X-AG2-Intent-Request: 1, plus a body with confirm set to true.
- Switch token comparison is fixed-time, and returned dependency messages are redacted.

Do not describe loopback binding or Origin checks as user authentication. See [security-and-trust-model.md](security-and-trust-model.md).

## Status and error behavior

Common meanings in current handlers:

- 400: invalid payload, invalid identifier, validation failure, or rejected enrollment input. For POST /api/config, RouterConfigValidator enforces:
  - PollingIntervalMs >= 1 (must be positive).
  - LowQuotaThresholdPercent between 5 and 50 inclusive.
  - MinimumCandidateQuotaPercent between 10 and 90 inclusive.
  Any value outside these ranges is rejected with 400 Bad Request prior to lock acquisition or persistence.
- 403: remote/invalid Host, disallowed Origin, cross-site metadata mutation, or missing/invalid explicit switch token.
- 404: account not found.
- 408: the CANCELLED switch result, including cancellation before process mutation, is mapped to request timeout.
- 409: switch conflict such as busy, already active, missing vaulted target, unsafe process, or switch already in progress.
- 500: failed switch/rollback categories not mapped more specifically.
- 501: required service is absent or the unsupported legacy switch planner route is called.
- 503: switch telemetry unavailable.

Native switch result codes are strings declared by SwitchResultCodes. HTTP status and result code serve different purposes and should both be tested.

After process transition begins, caller cancellation is detached; internally bounded forward completion and rollback determine the result. Transaction detail belongs in [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Remaining contract limitation

There is no generated/shared schema or browser end-to-end contract gate. Backend DTO serialization and server tests define the wire; frontend types are maintained alongside them. See [known-limitations.md](known-limitations.md).

## Evidence and enforcement

- Server tests: LoopbackServerTests, LoopbackServerAccountApiTests, LoopbackSwitchApiTests, and LoopbackRouterApiTests.
- DTO serialization tests: CoreModelTests and loopback response assertions.
- Frontend client/types: frontend/src/lib/api/.
- UI consumers: frontend/src/App.svelte and frontend/src/lib/components/.

The durable fix is a generated or shared machine-checked contract. Documentation is navigation, not schema enforcement.
