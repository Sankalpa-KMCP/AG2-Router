# Critical project invariants

Semantic rules that future changes must preserve. This is a consolidated checklist for review and planning; it is navigation, not the owner of any subject — each section links the owning document, which wins on detail. Every invariant suitable for automation should have a focused test or schema check; prose alone is not proof. Where an intended rule is not yet satisfied by implementation, the owning document labels it INTENDED INVARIANT and [known-limitations.md](known-limitations.md) records the gap.

Architecture context: [architecture.md](architecture.md). File locations: [repo-map.md](repo-map.md).

## Source and generated artifacts

- `frontend/` is the only authored dashboard source; `src/ui/` is generated output and must be regenerated through the declared build, never hand-edited as the primary change. The `.NET` project consumes it as `wwwroot` via the csproj content link. Owner: [build-and-release.md](build-and-release.md).
- `dotnet/Directory.Build.props` `Version` is the canonical release-version input. The release tag must equal `v<Version>`; other version literals (package.json, installer names) mirror it at release time and are not independent authorities. Owner: [build-and-release.md](build-and-release.md).
- Never commit `graphify-out/` or other derived graph artifacts; the derived graph is navigation only and never authority. Owner: [AGENTS.md](../AGENTS.md).
- Persistent user data stays outside the install directory and out of release artifacts. Owner: [build-and-release.md](build-and-release.md).

## Quota and telemetry truthfulness

- Unknown quota is unknown: never represented, displayed, cached, ranked, or routed as known capacity; non-finite fractions serialize as `null`, observed zero stays zero.
- Prompt credits and Flow credits are separate pools; never merged into one balance. Per-model quotas stay separate without evidence of a shared bucket. Cross-account percentages are relative measures, never additive totals.
- Activity is IDLE only when an explicitly observed keyed trajectory collection is empty or all statuses are known-inactive; missing/malformed/unrecognized evidence is UNKNOWN; RUNNING is BUSY. UNKNOWN is never treated as EXHAUSTED.
- Owner: [domain-rules.md](domain-rules.md). Evidence: AG2TelemetryNormalizer, CandidateSelector, and their tests.

## Routing and workload intent

- `WorkloadModelKey` is explicit user intent, matched exactly (trimmed, invariant-lowercase) against raw telemetry keys; unset, absent, or unknown requested-model evidence fails closed. The IDE model dropdown is not observed.
- Candidate eligibility requires known, non-exhausted, at-or-above-minimum capacity for every row matching the relevant model key (weakest link); observations must be strictly younger than two hours; accounts without a coherent live observation are ineligible.
- An automatic switch requires: live active quota pressure on the configured model → durable-evidence selection → admission revalidation of the same record/config/freshness → verified process provenance and IDLE activity → target identity verification → fresh live target-quota verification before metadata commit. Disproven cached evidence is conditionally invalidated; cooldown alone never revives it.
- BUSY or UNKNOWN activity, failed provenance, ambiguous identity, any non-`NONE` journal recovery state, and any quarantine block mutation. Manual recovery blocks automatic switching until explicitly reset; `RESTART_REQUIRED` is terminal for the process lifetime.
- Owner: [domain-rules.md](domain-rules.md). Evidence: NativeAutoRouter, CandidateSelector, RoutingSafetyGate, TargetQuotaVerificationSwitchTests.

## Account identity and credentials

- Account identity comes from verified Antigravity telemetry, not user-entered email. The WinCred `UserName` is keyring metadata (canonical value `antigravity`), never identity.
- Live account email must match the persisted active account before quota is attributed to it or a switch is admitted; a mismatch is not an implicit transition.
- A switch is complete only after the restarted, verified process reports the target identity and the metadata commit succeeds.
- Compensation must not overwrite newer concurrent state: use the guarded CAS operations (`RemoveAccountIfUnchangedAsync`, `RestoreAccountIfUnchangedAsync`, `CompareExchangeActiveAccountIdAsync`, `TryFinalizeSwitchAsync`); plain writes are not substitutes.
- Owner: [domain-rules.md](domain-rules.md), [persistence-and-concurrency.md](persistence-and-concurrency.md). Evidence: AccountEnrollmentService, NativeAccountSwitchCoordinator, and their tests.

## Concurrency and lifecycle

- Durable writes use the atomic write-through primitive (`DurableFileWriter`); read-modify-write mutations take the path lock, then the cross-process lease, then re-read the latest snapshot. Maintain the documented acquisition order (switch gate → switch lease → interruption admission → short router state); a new order requires deadlock analysis and contention tests.
- Router configuration applies with a monotonic `ConfigGeneration`; stale polling-interval updates are rejected. Configuration is validated at load; invalid persisted config fails startup visibly instead of silently defaulting.
- Polling assigns monotonic sequence numbers and never replaces newer status with older; account identity and quota derive from one unified observation so snapshots never mix accounts.
- A mutation that times out or ignores cancellation keeps its lock/lease ownership until it settles; the caller gets an explicit recovery-uncertain result and quarantine blocks later admission until restart and reconciliation.
- Switch cancellation is `CANCELLED` (HTTP 408) only before process transition; after that, caller cancellation is detached and forward/rollback completion is internally owned and bounded.
- Owner: [persistence-and-concurrency.md](persistence-and-concurrency.md). Evidence: TelemetryPollingCoordinatorTests, RoutingInterruptionAdmissionTests, SwitchCoordinatorLifecycleTests.

## Interrupted switch consistency

- The switch journal is written before any process/credential mutation and reconciled at startup before routing, polling, or dashboard services start. WinCred and metadata are separate durable stores; reconciliation never blindly repairs divergence.
- Every non-`NONE` recovery state and any quarantine blocks manual and automatic switching. Resolution requires fresh coherence proof across metadata, vault, live identity, and credential; `RESTART_REQUIRED` follows successful resolution of serious states.
- Owner: [persistence-and-concurrency.md](persistence-and-concurrency.md). Evidence: SwitchJournalStoreTests, NativeAccountSwitchCoordinatorJournalTests, NativeAccountSwitchCoordinatorResolutionTests.

## Persistence fail-closed behavior

- Malformed, truncated, wrong-magic, or unsupported-version persisted data fails closed; corrupt stores (vault, quota observations, usage ledger) are never overwritten, normalized, or silently replaced — original bytes are preserved and writes are blocked until remediation.
- Vault save success requires authoritative durable readback proving the committed record; uncertain outcomes are manual-recovery results, not successes.
- Owner: [persistence-and-concurrency.md](persistence-and-concurrency.md). Evidence: SessionVaultTests, DurableQuotaObservationStoreTests, UsageCallLedger tests.

## Usage accounting

- One ledger record per observed conversation-model call, keyed by `SHA-256("AG2U1K1" + length-prefixed(cascadeId) + length-prefixed(responseId))`; raw cascade/response IDs and `executionId` are never identity, and raw IDs are never persisted.
- The ledger never contains prompt text, prompt sections, titles, tool schemas, provider payloads, emails, tokens, ports, command lines, or process paths. Usage API responses expose aggregate counters and internal account IDs only.
- Headline tokens = input + output over unique calls; cache reads are a separate counter (never silently added); retries are excluded; there are no credits, quotas, percentages, or float token storage.
- Attribution is explicit and immutable: `VerifiedObservation` (managed account ID) or `Unattributed`; historical calls are never re-attributed to the currently active account. `HistoricalUnknown` calls contribute to totals only — no record claims an event time.
- Identical duplicate payloads are idempotent no-ops; conflicting payloads fail closed with the prior record intact; cross-segment duplicate keys are corruption. Usage for removed accounts remains readable.
- Owner: [usage-accounting.md](usage-accounting.md). Evidence: UsageCallLedger, UsageCollectionService, UsageAggregationService, and their tests.

## Privacy and secret handling

- No secrets, credential payloads, live account identifiers, private local paths, or unsanitized command lines in source, fixtures, logs, documentation, commits, or test output.
- Public account/status DTOs never expose credential blobs, DPAPI ciphertext, WinCred payloads, RPC tokens, or raw process command lines; AG2Security redacts at diagnostics and API boundaries; error bodies use fixed safe messages.
- Tests default to synthetic fixtures, fakes, and task-owned temporary directories; live-resource switches are explicit authorization gates, never ordinary configuration.
- Owner: [security-and-trust-model.md](security-and-trust-model.md), [testing.md](testing.md), [AGENTS.md](../AGENTS.md).

## Loopback API and trust

- Loopback binding, remote-address, and Host checks block remote exposure but are not authentication. Mutating browser endpoints reject foreign `Origin` and `Sec-Fetch-Site: cross-site` before body parsing; a missing Origin remains accepted for same-user native callers (documented limitation).
- Explicit switching and journal resolution require the per-process switch-intent token plus `confirm: true`; token comparison is fixed-time.
- DTO renames, type changes, nullability changes, and enum/result-code changes are wire-contract changes: update both sides (.NET DTOs and frontend types) plus tests in the same change.
- Do not weaken a fail-closed branch to keep routing available; changes need focused negative-path and concurrency tests.
- Owner: [api-contracts.md](api-contracts.md), [security-and-trust-model.md](security-and-trust-model.md).

## Installer and release

- Stopped-state checks are fail-closed: `UNKNOWN` is not `STOPPED`; install, upgrade, and uninstall refuse to mutate files or registry when stopped state or ownership cannot be proven.
- Installation ownership is classified (PowerShell/Inno/Ambiguous/ForeignConflict) before any destructive step; foreign or unproven registrations are preserved untouched.
- Uninstall removes only owned binaries, shortcuts, registration, and matched autostart values; it always preserves user data under `%LOCALAPPDATA%\AG2-Router` and the live Antigravity credential.
- Release payloads must contain required binaries and wwwroot assets and must not contain test/debug material or secret markers (the packaging scan is necessary, not sufficient).
- Owner: [build-and-release.md](build-and-release.md). Evidence: ReleasePackagingTests, UpgradePreservationTests, installer/AG2Router.iss, scripts/install.ps1, scripts/uninstall.ps1.
