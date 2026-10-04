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
| Usage accounting domain and durable call ledger | docs/usage-accounting.md |
| Current evidence-backed gaps | docs/known-limitations.md |
| Historical decision rationale | docs/decisions/README.md |

A project-understanding layer orients agents before they descend into subject documents:

| Document | Role |
| --- | --- |
| docs/architecture.md | Current system overview: components, process boundaries, data flow, release path |
| docs/repo-map.md | Navigation: where each subsystem lives, generated-artifact map, common task routes |
| docs/invariants.md | Consolidated cross-domain rules a change must preserve, linked to their owners |

These are orientation and navigation aids, never substitutes for the owning subject documents, source, or tests.

When sources disagree, use this precedence:

1. Explicit current user requirement.
2. The authoritative subject contract for its subject (what the contract is).
3. Current source, configuration, tests, and runtime evidence (what the behavior actually is).
4. Other validated evidence from direct inspection.
5. The project-understanding layer and docs/code-tour.md.
6. Historical material (release notes, ADRs, migration documents).
7. Inference.

Rule 3 outranks rule 2 for behavior: if a subject document conflicts with current source or tests, follow rule 3 of "Truth and authority" — do not let prose win by assertion.

## Required orientation

Before non-trivial project work (anything beyond a localized, self-evident fix):

1. Inspect current Git state (branch, HEAD, working tree) and preserve unrelated work.
2. Read the relevant portions of docs/architecture.md, docs/repo-map.md, and docs/invariants.md.
3. Read the owning subject document for the affected subject.
4. Inspect the relevant source, configuration, and tests. Nested AGENTS.md files, when present, apply to their area.
5. Use runtime evidence when behavior cannot be established statically.

Trivial or strictly local tasks do not require reading every document; keep orientation proportionate to the change's blast radius.

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

- New to the codebase: docs/architecture.md, then docs/repo-map.md and docs/code-tour.md.
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

### Documentation maintenance gate

Every coding agent is responsible for keeping the project-understanding documents (docs/architecture.md, docs/repo-map.md, docs/invariants.md) and the owning subject documents accurate. Before reporting a material repository task complete, answer:

1. Did this change architecture (components, boundaries, flows)?
2. Did this change file or directory responsibility/ownership?
3. Did this change a source-of-truth or generated-artifact relationship?
4. Did this change a public/API/data contract?
5. Did this change a critical invariant?
6. Did this change build, test, run, or release commands or the version mechanism?
7. Was a consequential decision made?

If yes to 1–6: update the affected current-state document(s) in the same task, before completion. If yes to 7: create or update a decision record under docs/decisions/ (see the decision documentation gate below). A task may require both. If no to all: leave the documentation unchanged; do not edit docs merely to touch them.

Every final report for a material task must state:

    Documentation impact:
    - UPDATED: <files and why>
    or
    Documentation impact:
    - NONE: <brief reason>

    Decision impact:
    - RECORDED: <ADR path>
    or
    Decision impact:
    - NONE: no consequential decision made

Prohibited: knowingly leaving architecture/map/invariant documentation stale; speculative updates unsupported by source; documentation-only wording churn unrelated to the task. This gate is mandatory, not optional.

### Decision documentation gate

Every agent making a consequential technical or product decision must record it as an ADR in docs/decisions/, capturing what was decided, why, the alternatives considered and why they were rejected, the consequences/tradeoffs, and what existing behavior or decision it replaces (if any). Start from [docs/decisions/ADR-TEMPLATE.md](docs/decisions/ADR-TEMPLATE.md) and follow [docs/decisions/README.md](docs/decisions/README.md) for naming, statuses, and supersession.

A consequential decision includes changes to: architecture; process boundaries; subsystem ownership; APIs/contracts; persistence/data models; concurrency/lifecycle behavior; security/privacy boundaries; generated/source-of-truth relationships; dependencies with architectural impact; release/version strategy; important UX/product semantics; compatibility behavior; removal or replacement of a major mechanism.

Do not create ADRs for local variable names, formatting, obvious refactors, typo fixes, or mechanically equivalent cleanup. This gate is mandatory, not optional.

### Solo project

This is a solo-maintained project. Historical implementation choices and previous architectural decisions create no compatibility obligation by themselves; when the task authorizes a change, prefer the better design and optimize for correctness, simplicity, maintainability, clarity, security, and performance where relevant rather than preserving historical code for its own sake. Do not erase reasoning history: reverse a documented decision through a new ADR that marks the old record SUPERSEDED and links both directions (see docs/decisions/README.md), never by silently rewriting the old record as though the prior decision never existed.

## Verification baseline

Use docs/testing.md and docs/build-and-release.md for the applicable commands and platform constraints. At minimum, verify changed links and paths, run focused tests for the affected contract, and inspect the final diff for generated files, secrets, and unrelated changes.
