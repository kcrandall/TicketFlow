# T04 — Release tooling, package graph and manual CI

Epic: [E1 — Foundation](../PLAN.md)

Status: **Completed**

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

Implemented (ported from EnvelopeFlow, renamed to TicketFlow; performance, Mailpit, digest, resilience and Node web-client tooling omitted):

- `scripts/release.py` (stdlib-only): version/channel resolution from `TicketFlowVersion`, package graph validation (solution membership, `TicketFlow`/`TicketFlow.*` IDs, dependency order, README, central versions), package content/metadata validation (`https://github.com/kcrandall/TicketFlow`, MIT, net11.0 assembly + XML docs, symbols), SHA-256 release manifest, isolated exact-version consumer smoke with `ticketflow-local` source mapping.
- Wrappers: `scripts/pack.sh`, `resolve-version.sh`, `verify-package-metadata.sh`, `verify-package-contents.sh`, `smoke-package-consumption.sh`, `publish-nuget.sh` (rejects anything but a manually dispatched main-branch Actions run with confirmation and trusted-publishing key; no `--skip-duplicate`), `test-release-scripts.sh` + `test_release_scripts.py` (11 regression tests), `test-postgres.sh` (digest-pinned PostgreSQL 18 container, loopback random port, labeled cleanup, `TICKETFLOW_TEST_POSTGRES`).
- `scripts/README.md`, `docs/agents/release-versioning.md` (SDK 11.0.100 / net11.0; owner must create the nuget.org trusted publishing policy for `kcrandall/TicketFlow`, `nuget-publish.yml`, pattern `TicketFlow*` before the first public publish).
- `.github/workflows/ci.yml` and `nuget-publish.yml`: `workflow_dispatch` only, main-branch guards, pinned action SHAs, PostgreSQL 18 service with `TICKETFLOW_TEST_POSTGRES`, dry-run default, separate publish job using `NuGet/login` OIDC with environments `nuget-stable`/`nuget-prerelease`. Node and Mailpit steps dropped; service `ports` value quoted for strict YAML parsers.

Verification commands (run from the repository root unless noted):

- `bash scripts/test-release-scripts.sh` (also from `/tmp`) — Ran 11 tests, OK.
- `bash scripts/verify-package-metadata.sh` — Validated 3 package projects and central versions.
- `bash scripts/pack.sh --version 0.1.0-alpha.1 --prerelease alpha --output artifacts/nuget-rehearsal-t04-<ts>` — Release build 0 warnings/0 errors; tests 40 passed (Core 29, TicketFlow 6, Stores.InMemory 5); 3 `.nupkg` + 3 `.snupkg` packed; manifest written.
- `bash scripts/verify-package-contents.sh --package-dir artifacts/nuget-rehearsal-t04-<ts>` — Validated contents and metadata for 3 packages and symbols; checksums verified.
- `bash scripts/smoke-package-consumption.sh --package-dir artifacts/nuget-rehearsal-t04-<ts>` — Consumer restored, built and loaded TicketFlow.Core, TicketFlow, TicketFlow.Stores.InMemory from an isolated cache.
- `bash scripts/test-postgres.sh` — 40 passed against an isolated PostgreSQL 18 container; no labeled containers left behind.
- `bash -n scripts/*.sh`; workflow YAML parsed — both workflows have only the `workflow_dispatch` trigger.

Package set at verification time: TicketFlow.Core, TicketFlow, TicketFlow.Stores.InMemory (Access projects were not yet present). Post-ticket parallel reviews (security, correctness, tests, perf/API) completed 2026-11-22. E1-specific findings: `TicketFlowBuilder` moved from the `TicketFlow` meta-package into `TicketFlow.Core` (public constructor, same `TicketFlow` namespace) so feature packages never reference the meta-package; `TICKETFLOW_REQUIRE_POSTGRES=1` turns inconclusive PostgreSQL tests into failures (set in both workflows and `scripts/test-postgres.sh`, covered by `TestingHelperTests`); `scripts/test_release_scripts.py` now asserts both workflows are `workflow_dispatch`-only, check `refs/heads/main`, and grant `id-token: write` only to the publish job. Re-verified: `dotnet build TicketFlow.slnx -c Release` (0 warnings), `TICKETFLOW_REQUIRE_POSTGRES=1 bash scripts/test-postgres.sh` (154 passed), `bash scripts/test-release-scripts.sh` (15 OK), `bash scripts/verify-package-metadata.sh` (7 packages). The nuget.org trusted publishing policy and GitHub environments are owner-provisioned settings and do not exist yet.
