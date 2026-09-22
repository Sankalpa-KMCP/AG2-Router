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

Trust checks and their limits are defined in [security-and-trust-model.md](security-and-trust-model.md).

## Frontend-consumed routes

| Method and path | Request | Success response | Current notes |
| --- | --- | --- | --- |
| GET /api/status | none | SystemStatusDto | Direct object containing status, ag2, router, and nullable telemetry |
| GET /api/accounts | none | AccountsListDto | accounts, totalCount, activeAccountId; each account is enriched with active/vault flags |
| POST /api/accounts | CreateAccountInput | 201 with { account } | Metadata registration; does not itself capture a live session |
| POST /api/accounts/enroll-current | optional EnrollmentOptions | { success, account, isNew, message } | Enrolls observed active identity and credential |
| PATCH /api/accounts/{id} | { alias: string or null } | { success, account } | Whitespace/null clears alias under current storage normalization |
| DELETE /api/accounts/{id} | none | { success, removedId, vaultRecordDeleted } | Removes metadata, then attempts vault removal |
| GET /api/switching/status | none | { status } and X-AG2-Switch-Token header | Token is required by explicit switch execution |
| POST /api/accounts/{id}/switch-plan | none | intended { plan } | CURRENT IMPLEMENTATION always returns 501 |
| POST /api/accounts/{id}/switch | { confirm: true } plus switch token header | NativeSwitchResult | Status depends on stable string result code |
| GET /api/config | none | { config: RouterConfigDto } | Current process-memory configuration |
| POST /api/config | complete RouterConfigDto | { config } | Replaces current router config; not durable across restart |
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

CURRENT IMPLEMENTATION account-field drift: backend AccountMetadata serializes lastActiveAt, while frontend AccountMetadata declares lastUsedAt. Neither field is currently consumed by the account-card/table UI, so this is a wire/type contract mismatch but not evidence of a rendered dashboard failure.

## Mutation semantics

CURRENT IMPLEMENTATION:

- Mutation handlers that call IsAllowedMutationOrigin reject a supplied Origin unless its scheme is HTTP, its host is allowed loopback, and its port matches the server port.
- Some account mutation handlers also reject Sec-Fetch-Site: cross-site.
- A missing Origin is accepted by IsAllowedMutationOrigin.
- Explicit switching additionally requires the per-process X-AG2-Switch-Token obtained from GET /api/switching/status and a body with confirm set to true.
- Switch token comparison is fixed-time, and returned dependency messages are redacted.

Do not describe loopback binding or Origin checks as user authentication. See [security-and-trust-model.md](security-and-trust-model.md).

## Status and error behavior

Common meanings in current handlers:

- 400: invalid payload, invalid identifier, validation failure, or rejected enrollment input.
- 403: remote/invalid Host, disallowed Origin, cross-site metadata mutation, or missing/invalid explicit switch token.
- 404: account not found.
- 408: the CANCELLED switch result, including cancellation before process mutation, is mapped to request timeout.
- 409: switch conflict such as busy, already active, missing vaulted target, unsafe process, or switch already in progress.
- 500: failed switch/rollback categories not mapped more specifically.
- 501: required service is absent or the switch planner route is not implemented.
- 503: switch telemetry unavailable.

Native switch result codes are strings declared by SwitchResultCodes. HTTP status and result code serve different purposes and should both be tested.

After process transition begins, cancellation follows the coordinator rollback path rather than necessarily returning CANCELLED. In particular, an explicit cancellation check occurs after target identity verification and before metadata finalization; a late client abort can therefore produce SWITCH_FAILED_ROLLED_BACK and its current 500 mapping. Transaction detail belongs in [persistence-and-concurrency.md](persistence-and-concurrency.md).

## Verified source drift

These are CURRENT IMPLEMENTATION discrepancies, not intended alternatives:

1. CanonicalModelQuotaDto serializes key, label, modelOrTier, remainingFraction, resetTime, isExhausted, and modes. The frontend CanonicalModelDto instead declares canonicalKey, displayLabel, timeUntilReset, and isRollingWindow.
2. Backend RouterConfigDto includes autoSwitchEnabled; the frontend RouterConfigDto declaration omits it even though saveConfig sends it.
3. NativeSwitchResult.Code is a string; frontend SwitchStatusDto declares lastResult.code as number.
4. The frontend expects switch-plan success, while the server route always returns 501.
5. The frontend executeSwitch return type exposes only a partial result and declares an optional numeric code, while the server returns the full NativeSwitchResult with a string code.
6. Backend AccountMetadata exposes lastActiveAt, while the frontend declares lastUsedAt. Current account UI components consume neither field.

Until these are reconciled and contract-tested, backend DTOs plus actual serialization define server output, while frontend declarations describe only current compile-time assumptions. See [known-limitations.md](known-limitations.md).

## Evidence and enforcement

- Server tests: LoopbackServerTests, LoopbackServerAccountApiTests, LoopbackSwitchApiTests, and LoopbackRouterApiTests.
- DTO serialization tests: CoreModelTests and loopback response assertions.
- Frontend client/types: frontend/src/lib/api/.
- UI consumers: frontend/src/App.svelte and frontend/src/lib/components/.

The durable fix is a generated or shared machine-checked contract. Documentation is navigation, not schema enforcement.
