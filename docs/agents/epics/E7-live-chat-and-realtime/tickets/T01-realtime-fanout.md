# T01 — Realtime abstractions and Access-aware fan-out

Epic: [E7 — Live chat and realtime](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Realtime.Abstractions` + `TicketFlow.Realtime`: transport-neutral topics (conversation, inbox queue, agent, visitor
session), subscribers (agent principal or authenticated visitor session), id-only realtime events, `IRealtimePublisher`, a
pluggable `IRealtimeBus` (in-process default), a per-node subscription registry that authorizes subscriptions and filters every
delivery per subscriber, the `realtime` conversation outbox handler and an `IRoutingEventObserver` adapter for presence, reply
locks, agent status and routing.

## Dependencies

E2, E3, E5, E8.

## Acceptance criteria

- Conversation subscriptions need `conversation:read`; queue subscriptions need `inbox:read` and deliver only conversations matching
  the subscriber's planned `conversation:read` filter; agent topics are self-only; visitors subscribe only to their own session.
- Every delivery is re-authorized against the current conversation; note summaries need `note:read`; visitor-only events never
  reach agents and agent events never reach visitors. Notifications carry identifiers only (clients refetch through Access).

## Verification

- `dotnet test tests/Realtime/TicketFlow.Realtime.Tests -c Release`.

## Risks and boundaries

Delivery is best-effort per node; the in-process bus does not span nodes (cross-node buses are documented, not shipped).

## Results

Implemented `TicketFlow.Realtime.Abstractions` (topics, subscribers, `RealtimeEvent` union, `RealtimePublication`, publisher, bus,
connection, subscription, agent-action and visitor-directory contracts) and `TicketFlow.Realtime` (`RealtimeSubscriptionRegistry` with
`RealtimeDeliveryPolicy`, `InProcessRealtimeBus`, `BusRealtimePublisher`, `RealtimeOutboxHandler` consumer `realtime`,
`RealtimeRoutingEventObserver`, `RealtimeAgentActions`, `AddRealtime`). Payload choice: identifier-only notifications plus refetch
(docs/realtime.md).

- `dotnet test tests/Realtime/TicketFlow.Realtime.Tests -c Release` — 8 passed, 0 failed (subscription authorization, note stripping,
  deny-grant re-authorization of live subscriptions, queue filter semantics, self-only agent topics, routing observer mapping, visitor
  isolation and anonymization, agent actions, limits).

Review fixes (2026-11-22): bounded publication queue with one background fan-out worker (publishers and the outbox handler no longer
wait for delivery); one conversation read per change; agents grouped by principal with a cached `IAccessDecisionContext`
(`conversation:read`, `note:read`) reloaded after `DecisionContextLifetime`; bounded per-connection queues (drop-oldest) with
per-connection senders and `SendTimeout`; queue subscriptions re-check `inbox:read` at every re-plan and are removed when revoked;
`VisitorSessionEndedEvent` closes the session's connections (`IRealtimeConnectionLifetime`); `Realtime.Abstractions` no longer
references `Routing.Abstractions` (`RealtimeAgentStatus`, `RealtimeActivity`; the observer maps routing enums in `TicketFlow.Realtime`).
- `dotnet test tests/Realtime/TicketFlow.Realtime.Tests -c Release` — 12 passed (new: slow/throwing connections do not block others and
  publishing only queues; concurrent subscribe/disconnect; `inbox:read` revocation after `QueueFilterLifetime`; one read per change).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
