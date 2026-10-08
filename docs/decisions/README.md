# Architecture decision records

This directory stores durable historical rationale for consequential project decisions. ADRs answer why a choice was made; they do not replace current source, tests, or the authoritative subject documents in docs/.

## When to create an ADR

Create one when a decision is expensive to reverse, crosses multiple components, constrains future design, or resolves a recurring ambiguity. Examples include changing the shipped runtime, selecting a persistence format, altering the credential trust model, or establishing a compatibility policy. The `AGENTS.md` "Decision documentation gate" makes this mandatory for consequential decisions.

Do not create an ADR for routine implementation detail, temporary workarounds, task progress, individual bug fixes, release notes, formatting, naming, mechanically equivalent cleanup, or facts already clear from source and tests.

## Naming

Use:

    ADR-NNN-short-kebab-title.md

Assign the next number by inspecting committed ADRs at the time of creation. Do not reserve numbers in advance. Renames should be rare because other records may link to them. Start from [ADR-TEMPLATE.md](ADR-TEMPLATE.md).

## Statuses and supersession

Statuses: `PROPOSED`, `ACCEPTED`, `SUPERSEDED`, `REJECTED`. An accepted ADR records the decision rationale as of its date; it is history, not a living contract. Update a status only as follows:

- `PROPOSED` → `ACCEPTED` when the decision is adopted.
- `ACCEPTED` → `SUPERSEDED` only via a new ADR that links back, with the old record linking forward (`Superseded by:`). Never silently rewrite an accepted ADR to reflect a later decision.
- A considered-and-rejected direction is recorded with status `REJECTED` so the same dead end is not re-explored blindly.

This project is solo-maintained: past decisions create no compatibility obligation by themselves, and any decision may be superseded when a better design is justified. Improving a decision is welcome; erasing the reasoning behind it is not.

## Index

Current records:

| ADR | Title | Status |
| --- | --- | --- |
| [ADR-001](ADR-001-switch-transaction-journal.md) | Durable switch transaction journal and startup reconciliation | Accepted (implemented) |
| [ADR-002](ADR-002-native-dotnet-runtime.md) | Adopt native .NET/WPF runtime instead of Electron or a Node daemon | Accepted |
| [ADR-003](ADR-003-documentation-governance.md) | Documentation governance — understanding layer, mandatory gates, and decision records | Accepted |
| [ADR-004](ADR-004-use-mit-license.md) | Use MIT License for AG2 Router | Accepted |
| [ADR-005](ADR-005-target-activation-provenance.md) | Persist target-activation provenance in the switch journal | Accepted (implemented) |
| [ADR-006](ADR-006-recorded-source-runtime-coherence-recovery.md) | RECORDED journal cleanup requires source-runtime coherence | Accepted (implemented) |
| [ADR-007](ADR-007-evidence-revision-and-pool-status-authority.md) | Durable evidence revision and authoritative pool-status publication | Accepted (implemented) |

When adding an ADR, add its row to this table in the same change.

## Authority rules

- Current operational truth remains in source/tests and the owning subject document, such as [runtime architecture](../runtime-architecture.md) or [security and trust](../security-and-trust-model.md).
- Historical documents may be linked as evidence but should be labeled historical.
- Never include secrets, live account data, transcripts, prompt IDs, temporary branch/commit state, authorization state, or ephemeral work progress.
