# TicketFlow.Conversations.Stores.Postgres

Optional durable PostgreSQL storage for TicketFlow conversations.

- `PostgresConversationStore` implements `IConversationStore`, `IConversationOutbox` and `IConversationAttributeDefinitionStore`
  over an application-owned `NpgsqlDataSource`.
- A conversation write runs in one transaction: the revision- and timeline-head-fenced snapshot row, timeline event rows (gapless
  per-conversation sequence, primary key `(conversation_key, sequence)`), message rows (unique external id per inbox through a
  partial unique index, so concurrent duplicate inbound messages admit one winner) and the outbox entry with one pending delivery
  per registered consumer.
- Lists are keyset-paged over `(scope_key, [inbox|status|assignee|team|contact], last_activity_at DESC, conversation_id DESC)`
  indexes (labels via GIN) with the Access filter translated by `PostgresAccessFilterTranslator` in the same statement
  (untranslatable filters deny all).
- Outbox claims use `FOR UPDATE SKIP LOCKED` with fences, leases and per-conversation ordering; acknowledgements and releases are
  fenced and require a live lease.
- Keys use fixed-size `tf_key_v1` hashes; rows carry `format_version` and numeric discriminators; payloads use
  `ConversationJsonCodec` versioned JSON.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddConversations()
    .AddPostgresConversationStores(dataSource);
await new PostgresConversationStore(dataSource).InitializeAsync();   // explicit schema creation
```

`InitializeAsync` applies the append-only, checksummed `PostgresConversationMigrations.Catalog` with the shared migration runner.
