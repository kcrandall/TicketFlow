# E11 — Hosting and agent console

Status: **Completed**

A runnable help desk on top of the libraries.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Optional ASP.NET Core endpoints, IdentityFlow adapter, Blazor (static SSR) management UI.

Part A (T01–T06) delivers hosting, workers, the IdentityFlow adapter and the sample host; part B (T07) delivers the Blazor management UI, which replaces the originally planned React/TanStack console.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Endpoint foundation, workspaces and access administration](tickets/T01-endpoint-foundation-workspaces-access.md) | Completed | E1–E10, E12, E13 |
| [T02 — Support endpoints: contacts, conversations, routing, SLA and live chat](tickets/T02-support-endpoints.md) | Completed | E11 T01 |
| [T03 — Automation, help center, surveys, assist and public endpoints](tickets/T03-content-and-public-endpoints.md) | Completed | E11 T01 |
| [T04 — Worker hosting, checkpoints and telemetry](tickets/T04-worker-hosting.md) | Completed | E3–E10, E12 |
| [T05 — IdentityFlow integration](tickets/T05-identityflow-integration.md) | Completed | E11 T01, E7 |
| [T06 — Sample host, docs and smoke tests](tickets/T06-sample-host-and-docs.md) | Completed | E11 T01–T05 |
| [T07 — Blazor management UI](tickets/T07-agent-console-mvp.md) | Completed | E11 T01–T06 |

## Verification record

Part A, T01–T06 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors);
`dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release` (18 passed); `dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Tests -c Release`
(7 passed); `dotnet test tests/Integrations/TicketFlow.Integrations.IdentityFlow.Tests -c Release` (11 passed); with `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55711;... TICKETFLOW_REQUIRE_POSTGRES=1` against an isolated `postgres:18` container (`tf-pg-e11`):
`TicketFlow.Hosting.Worker.Stores.Postgres.Tests` (2 passed), `TicketFlow.SampleHost.Tests` (3 passed), `TicketFlow.Workspaces.Stores.Postgres.Tests`
(14 passed); `TicketFlow.Workspaces.Tests` (51 passed) and `TicketFlow.Realtime.SignalR.Tests` (6 passed) unchanged. `bash scripts/verify-package-metadata.sh`
currently stops on a concurrent epic's project (`src/Events/...` missing from the solution), not on E11 projects. Parallel post-ticket reviews
are outstanding. See [docs/hosting.md](../../../hosting.md) and [docs/identityflow-integration.md](../../../identityflow-integration.md).

Part B, T07 (2026-11-22): `dotnet test tests/UI/TicketFlow.UI.Blazor.Tests -c Release` (52 passed: bUnit component tests and TestServer
smoke tests through the seeded sample host); `TicketFlow.SampleHost.Tests` against an isolated `postgres:18` container (`tf-pg-e11ui`,
4 passed); `TicketFlow.Hosting.AspNetCore.Tests` (18 passed); `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors).
Screenshots in `docs/ui/screenshots/`. See [docs/ui-blazor.md](../../../ui-blazor.md).

Review fixes (2026-11-22, on top of 4574992): see the T01, T02, T04, T05 and T06 Results. `dotnet test` — Hosting.AspNetCore 23, Hosting.Worker
10, Worker.Stores.Postgres 2 (PostgreSQL), IdentityFlow 15, SampleHost 4 (PostgreSQL), Access 102, UI.Blazor 60, Mcp.AspNetCore 3,
Realtime.SignalR 6 passed. New package `TicketFlow.Integrations.IdentityFlow.SignalR`.
