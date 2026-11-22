# T05 — Signed webhook sink with key rotation

Epic: [E15 — Public event stream and integrations](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Events.Webhooks`: `WebhookEventSink` (sink kind `webhook`, target an https URL) posting CloudEvents batches signed with
the E6 `ApiWebhookSignature` v1 scheme through the `ApiWebhookDestinationPolicy` SSRF guard; keys come from the application through
`IEventWebhookKeyResolver` with an optional secondary (rotation) signature.

## Dependencies

E15 T03, E6 (`TicketFlow.Channels.Api`).

## Acceptance criteria

- Primary signature verifies with `ApiWebhookSignature.Verify`; during rotation the previous key signs a second header; the delivery
  id is stable across retries.
- Blocked destinations are permanent failures; 408/429/5xx retry (honoring `Retry-After`); other 4xx are permanent.

## Verification

- `dotnet test tests/Events/TicketFlow.Events.Webhooks.Tests -c Release`.

## Risks and boundaries

Keys never enter TicketFlow stores; the application owns their storage and rotation schedule.

## Results

Implemented `WebhookEventSink` (`webhook`; reuses `TicketFlow.Channels.Api` `ApiWebhookSignature`, `ApiWebhookHeaders`,
`ApiWebhookDestinationPolicy` and `ApiWebhookHttp`), `EventWebhookKey(s)`, `IEventWebhookKeyResolver`, `InMemoryEventWebhookKeyRing`
(set/rotate/complete rotation), `EventWebhookOptions` and `AddEventWebhooks`. Tests: `WebhookEventSinkTests` (signature verifies with
`ApiWebhookSignature.Verify`, rotation dual signatures, status mapping with `Retry-After`, SSRF/target validation, missing keys, DI).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with concurrent E11a/E16/E17 work in the tree).
- `dotnet test tests/Events/TicketFlow.Events.Tests -c Release` — 26 passed, 0 failed.
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Events/TicketFlow.Events.Stores.Postgres.Tests -c Release` — 24 passed, 0 failed, against an
  isolated `postgres:18` container (`tf-pg-e15`, port 55915); includes the linked projection and delivery scenarios on PostgreSQL.
- `dotnet test tests/Events/TicketFlow.Events.Webhooks.Tests -c Release` — 5 passed.
- `dotnet test tests/Integrations/TicketFlow.Integrations.PostHog.Tests -c Release` — 4 passed.
- `dotnet test tests/Integrations/TicketFlow.Integrations.WalFlow.Sinks.Tests -c Release` — 3 passed.
- `bash scripts/verify-package-metadata.sh` — fails only on a concurrent E17 project (`src/Mcp/TicketFlow.Mcp`) not yet in the solution.

Post-ticket parallel reviews have not been run yet.
