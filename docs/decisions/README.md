# Architecture decision records

This directory stores durable historical rationale for consequential project decisions. ADRs answer why a choice was made; they do not replace current source, tests, or the authoritative subject documents in docs/.

No ADR is created as part of the documentation foundation. Existing migration-era documents remain preserved and can be used as evidence when a later task reconstructs a decision.

## When to create an ADR

Create one when a decision is expensive to reverse, crosses multiple components, constrains future design, or resolves a recurring ambiguity. Examples include changing the shipped runtime, selecting a persistence format, altering the credential trust model, or establishing a compatibility policy.

Do not create an ADR for routine implementation detail, temporary workarounds, task progress, individual bug fixes, release notes, or facts already clear from source and tests.

## Naming

Use:

    ADR-NNN-short-kebab-title.md

Assign the next number by inspecting committed ADRs at the time of creation. Do not reserve numbers in advance. Renames should be rare because other records may link to them.

## Template

Each ADR should contain:

    # ADR-NNN: Decision title

    Status: Proposed | Accepted | Superseded by ADR-NNN
    Date: YYYY-MM-DD

    ## Context
    The durable problem, constraints, and evidence.

    ## Decision
    The selected approach and its scope.

    ## Alternatives considered
    Material alternatives and why they were not selected.

    ## Consequences
    Benefits, costs, risks, and migration/compatibility effects.

    ## Evidence
    Repository-relative source, test, issue, or stable review references.

## Authority rules

- An accepted ADR records the decision rationale as of its date.
- Current operational truth remains in source/tests and the owning subject document, such as [runtime architecture](../runtime-architecture.md) or [security and trust](../security-and-trust-model.md).
- When direction changes, add a new ADR and mark the old one superseded; do not rewrite history to make the old decision appear different.
- Historical documents may be linked as evidence but should be labeled historical.
- Never include secrets, live account data, transcripts, prompt IDs, temporary branch/commit state, authorization state, or ephemeral work progress.
