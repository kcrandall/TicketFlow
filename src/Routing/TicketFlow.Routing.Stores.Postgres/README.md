# TicketFlow.Routing.Stores.Postgres

Optional durable PostgreSQL storage for TicketFlow routing.

- `PostgresRoutingStore` implements `IRoutingPolicyStore`, `IAgentAvailabilityStore`, `IRoutingAssignmentStore`,
  `IRoutingQueueStore` and `IConversationPresenceStore` over an application-owned `NpgsqlDataSource`.
- Reservations run in one transaction: the round-robin cursor row is locked (`FOR UPDATE`), then one load anchor row per candidate
  agent and channel kind in key order (deadlock-free), loads are counted from the assignment rows, the reference selection picks the
  agent and the reservation is upserted only when no live reservation or newer assignment exists. Capacity therefore holds across
  clients.
- Queue lists translate the Access filter with `PostgresAccessFilterTranslator` in the same keyset statement (untranslatable filters
  deny all). Queue and reassignment claims use `FOR UPDATE SKIP LOCKED` with fences from one sequence; presence expiry sweeps too.
- Keys use fixed-size `tf_key_v1` hashes; rows carry `format_version`; policies and agent settings use the versioned
  `RoutingPolicyCodec`/`AgentSettingsCodec` JSON.

```csharp
services.AddTicketFlow()
    .AddRouting()
    .AddPostgresRoutingStores(dataSource);
await new PostgresRoutingStore(dataSource).InitializeAsync();   // explicit schema creation
```

`InitializeAsync` applies the append-only, checksummed `PostgresRoutingMigrations.Catalog` with the shared migration runner. Lease,
presence and expiry times come from the store's `TimeProvider`, so clients need synchronized clocks.
