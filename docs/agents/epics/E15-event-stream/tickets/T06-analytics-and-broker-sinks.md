# T06 — PostHog and WalFlow broker sinks, documentation

Epic: [E15 — Public event stream and integrations](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Integrations.PostHog` (sink kind `posthog`: PostHog `/batch/` capture with configurable `distinct_id` strategy and
workspace group analytics over an `HttpClient`) and `TicketFlow.Integrations.WalFlow.Sinks` (sink kind `walflow`: adapts events to
WalFlow `ISink` sink packages — Kafka, RabbitMQ, SQS, SNS, Azure Service Bus, Google Pub/Sub, Kinesis, Redis Streams — referencing
only `WalFlow.Abstractions` `0.1.0-alpha.alpha.20260708090623`). `docs/events.md`.

## Dependencies

E15 T03.

## Acceptance criteria

- PostHog payloads carry `api_key`, `uuid` (the event id, for deduplication), `event`, `distinct_id`, `timestamp` and `$groups`.
- WalFlow payloads are `op = "c"` records keyed by the conversation/contact subject (partition affinity per conversation) with the
  CloudEvent in `after`; the adapter can also emit the structured CloudEvent as the message body.

## Verification

- `dotnet test tests/Integrations/TicketFlow.Integrations.PostHog.Tests -c Release`
- `dotnet test tests/Integrations/TicketFlow.Integrations.WalFlow.Sinks.Tests -c Release`

## Risks and boundaries

A Segment-compatible sink is a follow-up (same shape as PostHog; not needed for the first slice).

## Results

Implemented `TicketFlow.Integrations.PostHog` (`PostHogEventSink`, `PostHogProject`, `PostHogOptions` with
`ContactId`/`ContactExternalId` strategies and workspace groups) and `TicketFlow.Integrations.WalFlow.Sinks` (`WalFlowEventSink` over WalFlow
`ISink`/`SinkRegistration`, `CloudEventSinkPayloadTransformer`, `RequireDurableSinks`), referencing `WalFlow.Abstractions`
`0.1.0-alpha.alpha.20260708090623` (verified on nuget.org; no dependencies). The WalFlow `ISink(DebeziumPayload)` contract fits
app-originated events as `op = "c"` records, so no separate broker abstraction was needed. Wrote `docs/events.md`. Tests:
`PostHogEventSinkTests` (batch shape, distinct ids, masked external id fallback, statuses, DI), `WalFlowEventSinkTests` (record mapping,
ordering and flush, transformer body, failures, durable requirement, DI). Segment sink left as a follow-up (documented).

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
