# T08 — WalFlow change-capture fan-out (follow-up)

Epic: [E7 — Live chat and realtime](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Integrations.WalFlow`: drive realtime fan-out from PostgreSQL change capture instead of (or in addition to) polling the
conversation outbox, and provide the cross-node `IRealtimeBus` that multi-node realtime needs.

## Evaluation (E7 T07)

`WalFlow.Abstractions 0.1.0-alpha.alpha.20260708090623` (nuget.org, `net10.0`, no dependencies) exposes `ISink.SendAsync(DebeziumPayload)`
with `Before`/`After` as `Dictionary<string, object?>` row images. Not a clean fit yet:

- The only stable change record is the `tf_conversation_outbox` row, whose layout (`payload` text via
  `ConversationJsonCodec.DeserializeChange`, scope columns, `kind`, `revision`) is private to `TicketFlow.Conversations.Stores.Postgres`.
  An adapter would couple to another package's table layout without a published row contract.
- Column value types differ between WalFlow's snapshot and streaming paths (smallint/bigint/timestamptz may arrive as numbers,
  strings or `JsonElement`s), so decoding needs a tolerant, versioned mapping verified against a real logical-replication pipeline.
- Change capture sees committed rows on one node; it still needs a node-to-node transport to reach every node's registry.

## Dependencies

E7 T01; a published outbox row contract from E5 (`ConversationOutboxRowCodec` or similar) and a WalFlow release with typed column values.

## Acceptance criteria

- An `ISink` that decodes outbox inserts into `RealtimePublication`s through a published TicketFlow contract, idempotent by entry id.
- A cross-node `IRealtimeBus` (for example WalFlow → Redis/Kafka sink → per-node subscriber) with a versioned publication envelope.
- Integration test with a real PostgreSQL logical replication slot.

## Verification

- `dotnet test tests/Integrations/TicketFlow.Integrations.WalFlow.Tests -c Release` against an isolated PostgreSQL with `wal_level=logical`.

## Risks and boundaries

Outbox polling (`RealtimeOutboxHandler`) remains the default for single-node deployments; `TicketFlow.Integrations.WalFlow` adds CDC fan-out and cross-node delivery.

## Results

Implemented `ConversationOutboxRowCodec` in `TicketFlow.Conversations.Abstractions` and `TicketFlow.Integrations.WalFlow` (2026-11-22): `WalFlowRealtimeSink` (`ISink`) decoding `tf_conversation_outbox` snapshot and streaming rows idempotently by `entry_id`, versioned v1 `RealtimePublicationEnvelope`, `CrossNodeRealtimeBus` over `IRealtimeBackplane`, and `PostgresRealtimeBackplane` (`LISTEN`/`NOTIFY`). Verified with `TicketFlow.Integrations.WalFlow.Tests` (5 passed on PostgreSQL 18 with `wal_level=logical`).
