# T06 — Sample host, docs and smoke tests

Epic: [E11 — Hosting and agent console](../PLAN.md)

Status: **Completed**

## Goal

`samples/TicketFlow.SampleHost`: a runnable ASP.NET Core host composing every feature with PostgreSQL stores (connection string from
configuration; in-memory mode for tests), IdentityFlow principal resolution over a clearly marked development-only authentication
handler, SignalR hubs, workers, OpenAPI, health endpoint, a `seed` command (workspace, inbox, team, agents, grants, help center,
widget) and the widget demo page. `samples/docker-compose.yml` for PostgreSQL. docs/hosting.md, docs/identityflow-integration.md and
the README implementation status.

## Dependencies

E11 T01–T05.

## Acceptance criteria

- The sample host boots in-memory and against PostgreSQL; `/health` reports healthy; seeded agents can list conversations.
- The development authentication handler refuses to run outside the Development environment.

## Verification

- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release` (PostgreSQL case with `TICKETFLOW_TEST_POSTGRES`).

## Risks and boundaries

The sample is a finite composition example, not a production deployment.

## Results

Implemented `samples/TicketFlow.SampleHost` (`SampleHostApp.Build/InitializeAsync`, `seed` command, development-only authentication
handler, PostgreSQL or in-memory storage, workers, SignalR, OpenAPI, `/health` with worker and PostgreSQL checks, anonymous rate
limiter, `/demo` widget page), `samples/docker-compose.yml`, `samples/Directory.Build.props`, docs/hosting.md,
docs/identityflow-integration.md and the README implementation status.

Verification (2026-11-22):

- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release` with `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55711;... TICKETFLOW_REQUIRE_POSTGRES=1` against an isolated `postgres:18` container (`tf-pg-e11`) — 3 passed, 0 failed (in-memory boot, PostgreSQL boot with
  repeated idempotent seeding, development authentication refused outside Development).
- `dotnet run --project samples/TicketFlow.SampleHost -c Release -- seed` then `dotnet run` against the same container: `/health`
  Healthy (workers and PostgreSQL), agent conversation list 200, widget bundle 200.
