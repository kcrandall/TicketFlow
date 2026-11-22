# T01 — Integration event model, codec, permissions and store contracts

Epic: [E15 — Public event stream and integrations](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Events.Abstractions`: the public integration event envelope (CloudEvents 1.0 attributes `id`, `source`, `type`
`ticketflow.<area>.<name>.v1`, `time`, `subject`, `dataschema`, scope extension attributes and the workspace `sequence`), the typed
payload union for every v1 event type, privacy levels, subscription model, permissions (`events:read`, `events:manage` and
`events:content`, the latter two sensitive), sink and subscriber contracts, and the `IEventLog` / `IEventSubscriptionStore`
contracts with a development in-memory store and a shared store-contract library.

## Dependencies

E2 (Access), E5 (identifiers used by payloads).

## Acceptance criteria

- Persisted payloads use `{"v":1,…}` JSON keyed by the event type (the explicit discriminator); decoding re-validates and rejects unknown
  versions, types and properties. Unions are never serialized.
- Rendering at `IdsOnly` emits identifiers only, `Metadata` adds non-identifying state, `Content` adds hydrated content; no level
  ever reads bodies from the log (the log never stores them).
- Event log appends are idempotent per (scope, event id) and assign gapless per-workspace sequences in draft order; subscription
  writes are revision fenced; delivery claims are leased and fenced; dead letters and replay are atomic with the cursor.

## Verification

- `dotnet test tests/Events/TicketFlow.Events.Tests -c Release` (codec round trips, privacy rendering, in-memory store contract).

## Risks and boundaries

Payload enum-like values are lowercase string tokens owned by the event contract (not feature enums) so feature refactors do not
change the public wire format. New fields require a new type version (`.v2`).

## Results

Implemented `TicketFlow.Events.Abstractions`: `IntegrationEvent`, the 27-case `IntegrationEventData` union (records implement
`IIntegrationEventPayload` for type/subject), `IntegrationEventTypes`, `EventTypePattern`, `EventPrivacy`, `EventResource` (with
`AccessCheck`), `IntegrationEventCodec` (strict `{"v":1}` payload codec and CloudEvents rendering with privacy levels and content),
`EventText.EventId` (deterministic v8 UUIDs), subscriptions/filters/start positions, `EventPermissions`, sink/subscriber/publisher/service
contracts and the `IEventLog` / `IEventSubscriptionStore` contracts. `InMemoryEventStore` and `tests/Events/TicketFlow.Events.StoreContracts`
(append, prune/checkpoints, subscriptions, delivery, concurrent claims). Tests: `CodecTests` (every type round-trips, strict decoding,
privacy rendering, ids, patterns), `InMemoryEventStoreTests`.

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
