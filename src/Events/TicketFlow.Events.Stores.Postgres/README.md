# TicketFlow.Events.Stores.Postgres

Durable PostgreSQL storage for the TicketFlow integration event stream: `PostgresEventStore` implements `IEventLog` and
`IEventSubscriptionStore`.

```csharp
services.AddTicketFlow().AddEvents().AddPostgresEventStores(dataSource);
await new PostgresEventStore(dataSource).InitializeAsync();   // explicit schema creation
```

- Tables `tf_event_heads` (per-workspace head sequence), `tf_events` (the log; unique `(scope_key, event_id)`, retention index on
  `recorded_at`), `tf_event_checkpoints` (follower positions), `tf_event_subscriptions` (definition plus delivery cursor, attempts,
  fence and lease) and `tf_event_dead_letters`, created only by `InitializeAsync` through the append-only `ticketflow.events`
  migration catalog; `tf_key_v1` keys plus raw-column comparison; `format_version` on every row.
- Appends lock the scope head row and insert with gapless sequences (concurrent appenders serialize per workspace); delivery claims use
  `FOR UPDATE SKIP LOCKED` over the partial `(available_at) WHERE enabled` index and are fenced; payloads are stored as the
  versioned `IntegrationEventCodec` JSON (identifiers and metadata only, never bodies).
