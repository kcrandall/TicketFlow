# TicketFlow.Integrations.WalFlow

WalFlow PostgreSQL change-data-capture sink and cross-node `IRealtimeBus` for TicketFlow realtime fan-out.

```csharp
services.AddTicketFlow()
    .AddRealtime()
    .AddWalFlowRealtime(options => options.PostgresNotifyChannel = "ticketflow_realtime")
    .UsePostgresRealtimeBackplane();
```

- `WalFlowRealtimeSink` implements `WalFlow.Abstractions.Sinks.ISink`: filters `tf_conversation_outbox` inserts (`c`/`r`), decodes rows via the published `ConversationOutboxRowCodec` contract (tolerant of snapshot CLR types and streaming strings/`JsonElement`s), deduplicates idempotently by `entry_id`, and publishes identifier-only `ConversationChangedEvent`s to `IRealtimePublisher`.
- `RealtimePublicationEnvelope` serializes and validates every `RealtimeTopic` and `RealtimeEvent` using an explicit versioned v1 JSON discriminator envelope (`{"v":1,...}`) — C# unions are never serialized directly.
- `CrossNodeRealtimeBus` implements `IRealtimeBus` over any `IRealtimeBackplane` (including `PostgresRealtimeBackplane` using `LISTEN`/`NOTIFY`, or `InMemoryRealtimeBackplane` for tests), stamping each publication with its origin node id so nodes never double-deliver their own events.
