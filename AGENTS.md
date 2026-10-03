# AG2 Router agent guide

Read this file before changing the repository. It is the routing index and repository-wide safety policy, not a substitute for source or tests.

## Truth and authority

1. Source and tests are the executable technical truth.
2. The documents under docs/ explain intent, boundaries, and how to find that truth.
3. If prose conflicts with source or tests, do not make the prose win by assertion. Record the discrepancy, inspect the relevant tests, and change implementation, tests, and documentation together when the task authorizes it.
4. Release notes and decision records are historical evidence, not current operational contracts.
5. Do not infer current behavior from prior chats, agent memory, migration plans, or installed applications.

Each subject has one documentation owner:

| Subject | Authoritative document |
| --- | --- |
| Runtime topology and component boundaries | docs/runtime-architecture.md |
| Quota, routing, and identity invariants | docs/domain-rules.md |
| Loopback HTTP and frontend wire contracts | docs/api-contracts.md |
| Security assets and trust boundaries | docs/security-and-trust-model.md |
| Durable state, transactions, locks, and recovery | docs/persistence-and-concurrency.md |
| Test layers, fidelity, and isolation | docs/testing.md |
| UI build, packaging, installation, and release | docs/build-and-release.md |
| Current evidence-backed gaps | docs/known-limitations.md |
| Historical decision rationale | docs/decisions/README.md |

## Repository roles

- dotnet/src/AG2Router.Core: platform-neutral models and interfaces.
- dotnet/src/AG2Router.AG2: Antigravity discovery, RPC, normalization, accounts, vault, routing, and switching.
- dotnet/src/AG2Router.Windows: Windows-specific DPAPI, WinCred, process, registry, and session integration.
- dotnet/src/AG2Router.App: shipped WPF host, tray lifecycle, polling, loopback server, and WebView2 dashboard.
- dotnet/tests/AG2Router.Tests: native implementation and packaging tests.
- frontend/: authored Svelte and TypeScript dashboard source.
- src/ui/: generated frontend output consumed by the .NET project and Node reference server. Do not hand-edit it.
- src/ and test/: Node/TypeScript reference implementation and tests. They remain CI inputs, but do not automatically define shipped .NET behavior.
- scripts/ and installer/: build, packaging, install, upgrade, and uninstall implementation.
- .github/workflows/: enforced CI and release entry points.
- docs/: durable explanation and navigation. Existing migration and release documents may be historical.

## Deterministic reading path

For every task:

1. Read this file.
2. Read only the authoritative document for the affected subject, plus any document it directly links as a prerequisite.
3. Follow its source and test references.
4. Inspect the exact implementation and tests before editing.
5. For cross-boundary changes, update every affected contract and its tests; avoid copying the same rule into multiple documents.

Common routes:

- Dashboard or endpoint work: api-contracts.md, then LoopbackServer, Core DTOs, frontend API types/client, and loopback tests.
- Quota or AutoRouter work: domain-rules.md, then normalization/routing source and tests.
- Enrollment, vault, switching, or race work: persistence-and-concurrency.md and security-and-trust-model.md.
- Process discovery, credential, or loopback hardening: security-and-trust-model.md.
- Polling or WebView2 lifecycle work: runtime-architecture.md and persistence-and-concurrency.md.
- Build, generated UI, installer, or release work: build-and-release.md and packaging tests.
- Test design or live integration: testing.md.
- Codebase onboarding & comprehensive architecture tour: code-tour.md.

## Derived graph navigation

- Graphify is optional derived navigation only; never project authority. The authority chain remains: source and tests > owning documentation > derived index.
- Use an existing local graph (`graphify-out/graph.json`) only when its recorded `built_at_commit` exactly equals current `HEAD`.
- A missing, stale, or malformed graph must never block work; fall back to the deterministic reading path.
- Verify every material conclusion against owning documentation, source, and tests.
- Treat `INFERRED` edges as speculative hypotheses, not proof of coupling.
- Do not treat shortest paths through shared DTOs, namespaces, test fakes/mocks, interfaces, or project dependencies as runtime, control, or data flow.
- Frontend/backend wire contracts require deterministic inspection of source, schemas, and tests; graph links do not reliably bridge wire contracts.
- Svelte 5 rune and internal symbol coverage is incomplete; file imports and component hierarchy are more reliable than internal component symbols.
- Under the current protected-resource policy, do not execute the Graphify CLI to refresh, build, or query the graph, because currently verified tooling (v0.9.65) probes protected user state (`.gemini`) during startup. This prohibition applies to the currently verified tooling and policy; it can be re-evaluated if a future version is independently proven not to probe protected state. Direct read-only inspection of an already-present local graph via standard JSON tooling is permitted when otherwise authorized.
- Never commit `graphify-out/` or any generated graph artifacts.

## Protected resources

Repository work must default to synthetic fixtures, fakes, and task-owned temporary directories.

Do not access or mutate any of the following unless the user explicitly authorizes that exact live-resource operation:

- an installed or running AG2 Router instance;
- live Antigravity processes, ports, or RPC endpoints;
- Windows Credential Manager entries;
- production DPAPI plaintext or ciphertext;
- real account, session, or vault data;
- %LOCALAPPDATA%/AG2-Router;
- user registry state;
- Antigravity brain data, transcripts, or logs;
- production services or accounts.

Live-test environment switches are authorization gates, not ordinary test configuration. See docs/testing.md.

Never place secrets, credential payloads, live account identifiers, private local paths, or unsanitized command lines in source, fixtures, logs, documentation, commits, or test output.

## Change rules

- Preserve unrelated user changes.
- Modify authored frontend files under frontend/, then regenerate src/ui/ through the declared build. Never patch generated UI as the primary change.
- Treat frontend types and .NET DTOs as one wire contract; validate both sides.
- Keep unknown telemetry distinguishable from known numeric telemetry.
- Keep account identity changes transactional and safe under concurrency, cancellation, rollback, and external interference.
- Keep loopback binding separate from request authorization: loopback alone is not proof of caller intent.
- Keep persistent user data outside the install directory and out of release artifacts.
- Add focused tests for machine-verifiable behavior. Do not rely on prose for executable guarantees.

## Documentation maintenance

- Put a rule in its single owning document and link to it elsewhere.
- Use repository-relative paths and stable symbol names; avoid pasted source excerpts and line numbers.
- Label a statement as INTENDED INVARIANT when current implementation does not satisfy it.
- Put verified current gaps in docs/known-limitations.md and remove them when implementation and tests close them.
- Keep ADRs focused on why a durable choice was made. Current behavior belongs in subject documentation and source/tests.
- Keep release notes immutable for their release; do not use them as living architecture.
- Update documentation in the same change when a boundary, contract, invariant, persistence format, or release procedure changes.

Do not store prompt numbers, agent transcripts, temporary commit or branch state, current authorization state, review status, transient test counts, incident scratch notes, or ephemeral work progress in repository documentation.

## Verification baseline

Use docs/testing.md and docs/build-and-release.md for the applicable commands and platform constraints. At minimum, verify changed links and paths, run focused tests for the affected contract, and inspect the final diff for generated files, secrets, and unrelated changes.
