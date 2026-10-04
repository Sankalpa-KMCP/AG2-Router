# ADR-003: Documentation governance — understanding layer, mandatory gates, and decision records

Status: Accepted
Date: 2026-10-04

## Context

The repository accumulated overlapping and partially stale Markdown: a Node-era architecture spec, duplicated legacy security documents (one asserting WinCred access is strictly read-only, which the shipped switch coordinator disproves), a completed migration plan, and no enforced process keeping current-state docs synchronized with source. The project is solo-maintained with AI coding agents as primary contributors, so orientation quality and reasoning history directly determine work quality. A prior pass created the project-understanding layer; this decision ratifies it and completes the rationalization.

## Decision

1. **Documentation taxonomy with single ownership.** `AGENTS.md` (operating rules + navigation), `README.md` (human entry point), `docs/architecture.md` (current system overview), `docs/repo-map.md` (navigation map), `docs/invariants.md` (consolidated invariant checklist), per-subject owner documents (the table in `AGENTS.md`), `docs/known-limitations.md` (current gaps only), `docs/decisions/` (decision rationale), and immutable per-release notes. No two current documents own the same truth; links replace duplication.
2. **Documentation Maintenance Gate (mandatory, in `AGENTS.md`).** Before completing a material task, every agent assesses architecture/ownership/contract/invariant/build impacts and updates affected canonical documents in the same task, reporting `Documentation impact:` in the final report.
3. **Decision Documentation Gate (mandatory, in `AGENTS.md`).** Consequential decisions (architecture, contracts, persistence, security boundaries, generated-source relationships, release strategy, and similar) require an ADR in `docs/decisions/` recording decision, rationale, alternatives, and consequences. Trivial implementation details do not. Agents report `Decision impact:` alongside documentation impact.
4. **Solo-project improvement rule (in `AGENTS.md`).** Past decisions create no compatibility obligation by themselves; a better design may replace an older one when authorized — but never by rewriting history. Supersession happens through a new ADR that marks the old one `SUPERSEDED` and links both directions.
5. **Rationalization executed with this decision:** deleted `docs/dotnet-migration.md` (rationale preserved in [ADR-002](ADR-002-native-dotnet-runtime.md)), deleted `docs/security-model.md` and `docs/security.md` (superseded by [security-and-trust-model.md](../security-and-trust-model.md); their one still-current unique rationale — the single-layer DPAPI envelope — was merged there), and closed the corresponding backlog item TODO-014.

## Rationale

Docs that contradict source actively mislead future agents (the read-only-WinCred claim is the concrete example). Orientation and rules belong in small, well-linked documents; detailed truth belongs in subject owners and, ultimately, source and tests. Decision rationale is cheap to store and expensive to reconstruct, hence the ADR gate; but ADRs must stay lightweight or they will be skipped.

## Alternatives considered

- Keep historical documents untouched with "historical" labels: rejected — stale current-state claims kept being quoted as truth; labels alone did not prevent drift.
- Generate a doc-freshness CI gate: rejected — semantic staleness cannot be reliably inferred from file changes; the gate belongs in agent behavior.
- One comprehensive architecture document instead of a layer: rejected — violates progressive disclosure and recreates a monolith that goes stale.

## Consequences

- Positive: coherent, non-duplicative documentation; explicit supersession path; every future agent has mandatory maintenance and decision-recording behavior.
- Negative: agents carry a documentation-assessment duty on every material task; ADR overhead for consequential changes (accepted as small relative to the changes themselves).

## Compatibility/migration impact

None for product code. Historical rationale for deleted documents is preserved in this directory; git history retains the original files.

## Related

- [README](README.md) (decision system), [ADR-TEMPLATE](ADR-TEMPLATE.md), [ADR-001](ADR-001-switch-transaction-journal.md), [ADR-002](ADR-002-native-dotnet-runtime.md)
- The `AGENTS.md` sections: "Truth and authority", "Documentation maintenance gate", "Decision documentation gate", "Solo project"
