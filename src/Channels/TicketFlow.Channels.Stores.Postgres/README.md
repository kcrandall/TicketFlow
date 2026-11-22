# TicketFlow.Channels.Stores.Postgres

Durable PostgreSQL channel state for TicketFlow: `PostgresChannelStore` implements `IChannelEndpointStore`,
`IChannelConversationStateStore`, `IChannelThreadStore` and `IChannelDispatchStore`.

```csharp
services.AddTicketFlow()
    .AddChannels(options => options.WorkerPrincipal = scope => new ApiClientPrincipal(scope, new ApiClientId("channels")))
    .AddPostgresChannelStores(dataSource);
await new PostgresChannelStore(dataSource).InitializeAsync();   // explicit schema creation
```

Tables (`ticketflow.channels` migration catalog): `tf_channel_endpoints` (partial unique index on the partition/kind/address
key), `tf_channel_conversation_states`, `tf_channel_references` (unique per scope, inbox and reference; at most 128 per
conversation, pruned under an advisory lock) and `tf_channel_dispatches`. Keys are `tf_key_v1` hashes with raw-column comparison,
every row has a `format_version`, and all SQL is parameterized.
