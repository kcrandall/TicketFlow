# T03 — Subscriptions, delivery engine, privacy rendering and in-process subscribers

Epic: [E15 — Public event stream and integrations](../PLAN.md)

Status: **Completed**

## Goal

`EventSubscriptionService` (Access-checked management, replay and dead-letter listing), `EventDeliveryWorker` (claim due
subscriptions, filter, authorize as the subscription principal, render, deliver, advance/backoff/dead-letter),
`EventContentHydrator` (Content level through feature services), the in-process sink for `IEventSubscriber`s and
`EventRetentionWorker`.

## Dependencies

E15 T01.

## Acceptance criteria

- At-least-once: the cursor advances only after the sink accepted the batch (or the batch was dead-lettered).
- Ordering: a subscription receives events in workspace sequence order (so per conversation in timeline order); concurrent workers
  never deliver one subscription at the same time (leases and fences).
- Retries back off exponentially; after the maximum attempts (or a permanent failure) the batch is dead-lettered and the cursor
  advances; replay from a sequence rewinds the cursor and clears later dead letters.
- Every delivered event is decided for the subscription principal (`events:read`, then `conversation:read` / `contact:read` on the
  event resource); `Content` also requires `events:content` (sensitive, audited per batch) and hydrates through the conversation,
  contact and survey services so masked values stay masked.

## Verification

- `dotnet test tests/Events/TicketFlow.Events.Tests -c Release` (filters, privacy, retry/dead-letter/replay, access).

## Risks and boundaries

Total order per subscription means a failing event blocks later events of that subscription until it is dead-lettered (bounded by
`MaximumAttempts` and the backoff ceiling).

## Results

Implemented `EventSubscriptionService` (fences before authorization; `events:manage`, `events:content` for content, sink/target
validation, start positions, replay, dead letters), `EventDeliveryWorker` (leased claims, filter + per-event Access decisions from one
decision context, rendering, content hydration, backoff, dead letters, per-claim failure isolation), `EventContentHydrator` (through
`IConversationService`/`IContactService` as the principal, survey comments), `InProcessEventSink`, `EventRetentionWorker` and DI
(`AddEvents`, `AddEventSubscriber<T>`, `AddInMemoryEventStores`). Tests: `DeliveryTests` (ids-only/metadata without bodies, content with
masked `conversation.channel` and `contact.email`, content permission, filters, team-scoped principal, batch ordering,
backoff/dead-letter/replay, management access, retention).

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

Review fixes (2026-11-22): see the [epic verification record](../PLAN.md#review-fixes-2026-11-22-after-commit-4574992).
