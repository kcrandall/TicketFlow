# E15 — Public event stream and integrations

Status: **Completed**

A public, versioned, privacy-levelled integration event stream: a durable per-workspace event log projected from TicketFlow's own
change sources, Access-checked subscriptions with at-least-once fan-out, and sinks for webhooks, analytics and message brokers.

## Completion gate

Public APIs implement the outcome below with focused tests, Release builds of every E15 project, package metadata validation and
parallel post-ticket reviews passing.

Outcome: `ticketflow.*.v1` CloudEvents-compatible integration events (conversation, contact, SLA, routing, survey and channel delivery)
written exactly once per source change into a gapless per-workspace log with retention; subscriptions per workspace (type patterns,
inbox/team filters, privacy level `IdsOnly`/`Metadata`/`Content`, designated API-client principal); a delivery engine with
per-subscription cursors, per-conversation ordering, backoff, dead letters and replay; sinks for in-process subscribers, signed
webhooks (key rotation), PostHog and WalFlow broker sinks.

Boundaries: the projector is an E5 outbox consumer (`events`) and reads details through trusted stores; contact events follow the
E4 change log and survey responses the E12 store, both with durable checkpoints in the event store; routing events arrive through
the best-effort `IRoutingEventObserver`. Event log rows hold identifiers and metadata only — message bodies, survey comments and
contact details are hydrated at delivery time, only for `Content` subscriptions, through the owning feature services as the
subscription principal (so Access masking applies). Hosting loops belong to E11 (`RunOnceAsync` / `CatchUpAsync` workers).

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Integration event model, codec, permissions and store contracts](tickets/T01-event-model-and-stores.md) | Completed | E2, E5 |
| [T02 — Event projection: outbox consumer, contact and survey followers, routing observer](tickets/T02-event-projection.md) | Completed | E15 T01, E4, E8, E9, E12 |
| [T03 — Subscriptions, delivery engine, privacy rendering and in-process subscribers](tickets/T03-subscriptions-and-delivery.md) | Completed | E15 T01 |
| [T04 — PostgreSQL event store](tickets/T04-postgres-event-store.md) | Completed | E15 T01 |
| [T05 — Signed webhook sink with key rotation](tickets/T05-webhook-sink.md) | Completed | E15 T03, E6 (Channels.Api) |
| [T06 — PostHog and WalFlow broker sinks, documentation](tickets/T06-analytics-and-broker-sinks.md) | Completed | E15 T03 |

## Verification record

T01–T06 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Events/TicketFlow.Events.Tests -c Release`
(26 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Events/TicketFlow.Events.Stores.Postgres.Tests -c Release` (24 passed) against an
isolated `postgres:18` container; `TicketFlow.Events.Webhooks.Tests` (5), `TicketFlow.Integrations.PostHog.Tests` (4),
`TicketFlow.Integrations.WalFlow.Sinks.Tests` (3) passed. `bash scripts/verify-package-metadata.sh` fails only on a concurrent E17 project
not yet in the solution. Parallel post-ticket reviews are still outstanding before commit.


### Review fixes (2026-11-22, after commit 4574992)

- Security: subscription create/update require the actor to be able to confer every live allow grant of the API-client principal
  (`IAccessAdministrator.CheckCanConferAsync`); service calls authorize before NotFound/Conflict; delivery ids include the subscription
  revision and a replay generation (bumped by `ReplayAsync`); poison events fail and dead-letter alone; configuration failures (revoked
  principal, missing content permission, unknown or disallowed sink) suspend the subscription after `MaximumAttempts` until it is updated
  or replayed; named sinks (PostHog projects, WalFlow sinks) require `EventScopeRule` allow-lists and `IEventSink.IsValidTarget` takes the
  scope; PostHog distinct ids are namespaced (`contact:`, `external:<issuer>:`, `workspace:`) and conversation events use the conversation's
  contact.
- Correctness/perf: survey comments hydrate through `ISurveyService` (`survey:read`, `survey.comment` masking); hydration is batched (one
  timeline range read per conversation, one contact read per distinct contact; contact profile attached to conversation events);
  round-robin claims (completion resets the claim time, caught-up subscriptions wait `IdlePollInterval`); retention gaps recorded as
  `events-pruned-<from>-<to>`; `EventSinkSplit` halves oversized batches (webhook > 1 MiB or 413, PostHog 413); PostgreSQL migration 2
  (generation/suspended columns, due index excludes suspended, `fillfactor = 50` on heads, BRIN on `recorded_at`).
- `IConversationOutboxHandler` moved to `TicketFlow.Conversations.Abstractions` (same namespace, no consumer changes); `TicketFlow.Events`
  now references only feature abstractions.
- Not done: appending several outbox entries per transaction needs a batch handler contract in the E5 dispatcher (documented).

Verification (2026-11-22): `dotnet build TicketFlow.slnx -c Release` — 0 warnings, 0 errors. `dotnet test` — `TicketFlow.Events.Tests` 36,
`TicketFlow.Events.Webhooks.Tests` 7, `TicketFlow.Integrations.PostHog.Tests` 6, `TicketFlow.Integrations.WalFlow.Sinks.Tests` 3,
`TICKETFLOW_REQUIRE_POSTGRES=1 … TicketFlow.Events.Stores.Postgres.Tests` 34 (own `postgres:18` container, port 55915); outbox consumers
unchanged and passing: Conversations 46 (+18 PostgreSQL), Channels 42, Routing 48, Sla 31, Automation 42, Realtime 12, Reporting 43,
Integrations.Surveys 3.

Open follow-ups: a built-in `contact.identity-linked` source (E16 adds no contact change kind; publish through `IEventPublisher`);
`routing.queued` (E8 raises no queued event); a Segment-compatible sink; E11 worker loops for `EventDeliveryWorker`, the followers and
`EventRetentionWorker`.
