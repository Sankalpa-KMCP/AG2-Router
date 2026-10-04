# ADR-004: Use MIT License for AG2 Router

Status: Accepted
Date: 2026-10-04

## Context

The repository is intended to become publicly accessible. Package metadata (`package.json`) already declared `"license": "MIT"` and the README referenced MIT, but the repository contained no actual license grant — no `LICENSE` file existed, so GitHub reported no license and no legally effective grant accompanied the source. A clear, explicit license is required before public launch. The project owner explicitly authorized the MIT License with the copyright notice `Copyright (c) 2026 Sankalpa KMCP`.

## Decision

AG2 Router is distributed under the MIT License, reproduced verbatim (canonical text, no modifications) in the repository-root `LICENSE` file with the copyright notice:

    Copyright (c) 2026 Sankalpa KMCP

## Rationale

MIT is permissive: it allows use, modification, and redistribution with minimal obligations; it presents low adoption friction for a desktop tool; its notice-preservation requirement is simple to comply with; and it matches the owner's explicitly stated preference for an open, low-friction licensing model.

## Alternatives considered

- **No explicit license / all rights reserved:** rejected — the repository is meant to be public and usable; without a grant, others legally may not use, modify, or redistribute the code.
- **GPL-family copyleft:** rejected — stronger obligations than the owner wants for this project's intended use.
- **Apache-2.0:** rejected — comparable permissiveness but with additional patent and notice terms the owner did not request; MIT is the simpler fit.

## Consequences

- Users may use, copy, modify, merge, publish, distribute, sublicense, and sell copies of the software under MIT's conditions.
- Derivative works are not required to remain open source.
- The copyright and permission notice must be preserved in copies or substantial portions, as MIT requires.
- The software is provided "as is", under MIT's warranty and liability disclaimer.

## Compatibility/migration impact

None for existing behavior. The previously declared-but-ungranted MIT intent (package.json, README) is now backed by the actual license text; `package.json` is unchanged.

## Related

- Root [LICENSE](../../LICENSE)
- [ADR-003](ADR-003-documentation-governance.md) (decision-recording gate that requires this record)
- README license section
