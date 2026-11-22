# T02 — Event projection: outbox consumer, contact and survey followers, routing observer

Epic: [E15 — Public event stream and integrations](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Events`: `EventProjector` (an `IConversationOutboxHandler`, consumer `events`) maps conversation changes and their
timeline events to integration events; `EventContactFollower` and `EventSurveyFollower` follow the contact change log and survey
responses with durable checkpoints in the event log store; `EventRoutingObserver` maps routing assignments, overflows and queue-wait
breaches; `IEventPublisher` lets host features append their own drafts.

## Dependencies

E15 T01, E4 (contact change log), E8 (routing events), E9 (SLA events), E12 (survey responses).

## Acceptance criteria

- Event ids are deterministic per source (timeline event, outbox change, contact change, survey invitation), so redelivery and
  replays append nothing twice.
- `sla.*` events are projected only when the timeline actor is the configured SLA worker client; forged ones are ignored.
- Notes never carry bodies; message events carry ids and metadata only.
- Per-conversation order in the log follows the timeline.

## Verification

- `dotnet test tests/Events/TicketFlow.Events.Tests -c Release` (projection scenarios over in-memory features).

## Risks and boundaries

Routing events are best-effort (the routing observer contract swallows failures); there is no `routing.queued` source event in E8,
so it is not emitted.

## Results

Implemented `EventProjector` (consumer `events`; initial-state reconstruction for `conversation.created`; resolved/reopened derived
from status changes; SLA events only from `EventsOptions.SlaWorkerClientId`), `EventContactFollower` (checkpoint `contacts`),
`EventSurveyFollower` (sliding windows with overlap, checkpoint `surveys`), `EventRoutingObserver` (assigned, overflowed,
queue-wait-exceeded) and `EventPublisher`. Tests: `ProjectionTests` (lifecycle order and no bodies, redelivery idempotency,
interleaved conversations, forged SLA events, contact follower with checkpoint replay, survey overlap dedupe, routing mapping).
Deviation: no `routing.queued` (E8 raises no such event); `contact.identity-linked` has a payload but no built-in source (E16 adds no
contact change kind; publish via `IEventPublisher`).

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
