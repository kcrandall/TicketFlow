# T05 — Documentation

Epic: [E14 — Reporting](../PLAN.md)

Status: **Completed**

## Goal

`docs/reporting.md` and package READMEs: facts, metrics, rollups, percentiles, buckets, access, export, retention, store contracts
and limits.

## Dependencies

E14 T03, E14 T04.

## Acceptance criteria

- Documents implemented behavior only and lists known limits and the deferred InsightFlow export.

## Verification

- `bash scripts/verify-package-metadata.sh`.

## Risks and boundaries

None.

## Results

Wrote `docs/reporting.md` and the three package READMEs; deferred InsightFlow export recorded in T06.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with concurrent E11 work in the tree).
- `dotnet test tests/Reporting/TicketFlow.Reporting.Tests -c Release` — 37 passed, 0 failed.
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Reporting/TicketFlow.Reporting.Stores.Postgres.Tests -c Release` — 11 passed,
  0 failed, against an isolated `postgres:18` container (`tf-pg-e14`, port 55914).
- `bash scripts/verify-package-metadata.sh` — "Validated 51 package projects and central versions" after adding the three reporting
  packages (a later run failed only on a concurrent E11 project, `TicketFlow.Hosting.Worker`, not yet in the solution).

Post-ticket parallel reviews have not been run yet.
