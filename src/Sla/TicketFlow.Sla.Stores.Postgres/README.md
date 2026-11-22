# TicketFlow.Sla.Stores.Postgres

Durable PostgreSQL storage for TicketFlow SLAs: `PostgresSlaStore` implements `ISlaPolicyStore` and `ISlaTrackerStore`.

```csharp
services.AddTicketFlow().AddSla().AddPostgresSlaStores(dataSource);
await new PostgresSlaStore(dataSource).InitializeAsync();   // explicit schema creation
```

- Tables `tf_sla_policies`, `tf_sla_trackers` and `tf_sla_timers`, created only by `InitializeAsync` through the append-only
  `ticketflow.sla` migration catalog; `tf_key_v1` keys plus raw-column comparison; `format_version` on every row.
- Tracker writes are revision-fenced and set the timer row in the same transaction. Timers are claimed with
  `FOR UPDATE SKIP LOCKED` over an indexed `due_at` plus a lease and a fence; lease times come from the store's `TimeProvider`.
- Risk lists run the translated Access filter (`PostgresAccessFilterTranslator`) in the keyset query over the mirrored
  conversation team, inbox, assignee, labels and attribute condition values.
