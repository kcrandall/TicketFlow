# T04 — Release tooling, package graph and manual CI

Epic: [E1 — Foundation](../PLAN.md)

Status: **Planned**

## Goal

Port EnvelopeFlow release scripts (pack, resolve-version, verify-package-metadata/contents, smoke-package-consumption, publish-nuget, test-release-scripts, test-postgres) and manual-only `ci.yml`/`nuget-publish.yml` for TicketFlow with trusted publishing pattern `TicketFlow*`.

## Dependencies

E1 T03.

## Acceptance criteria

Release script regression tests pass; package graph verification passes; workflows trigger only on manual dispatch from main.

## Verification

- Run the focused MSTest project(s) for touched packages.
- Release build of `TicketFlow.slnx` with zero warnings.
- Post-ticket security, correctness, test and perf/API reviews.

## Risks and boundaries

Keep Core free of hosting/database/sibling dependencies. Preserve full scope. In-memory implementations are development examples only.

## Results

Not started.
