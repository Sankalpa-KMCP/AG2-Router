# Contributing

Thanks for your interest in AG2 Router. This is a solo-maintained project, so the bar is focus over ceremony: well-scoped bug fixes and clearly motivated improvements are welcome.

## Ground rules

1. **Read [AGENTS.md](AGENTS.md) first.** It routes every change to its owning document under `docs/` and sets the repository-wide safety policy (protected resources, generated files, fail-closed behavior).
2. **Open a focused issue before large changes.** Keep pull requests minimal — no unrelated refactors or drive-by formatting.
3. **Tests are required for machine-verifiable behavior.** Default tests must stay isolated: no live Antigravity processes, real Windows Credential Manager entries, or user data — see [docs/testing.md](docs/testing.md). Live-resource gates require explicit opt-in.
4. **Documentation and decision gates apply.** Update affected docs in the same change (Documentation Maintenance Gate in AGENTS.md) and record consequential decisions as ADRs under [docs/decisions/](docs/decisions/README.md). Pull requests should state both impacts — the PR template has fields for them.
5. **Respect the boundaries.** `frontend/` is the authored UI source (`src/ui/` is generated); `src/` and `test/` are the Node/TypeScript reference, not shipped code. Never commit secrets, credential payloads, live account identifiers, or machine-specific paths.

## Validation

Run the applicable checks before submitting:

```powershell
npm ci
npm run typecheck
npm run build
npm test
dotnet test dotnet/AG2Router.sln -c Release
```

Packaging and installer changes additionally require the packaging test suites — see [docs/build-and-release.md](docs/build-and-release.md).

## Coding agents

Using coding assistants or agents is fine, but as the contributor you own the correctness of everything you submit, including tests and documentation updates.
