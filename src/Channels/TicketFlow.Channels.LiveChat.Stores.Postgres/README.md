# TicketFlow.Channels.LiveChat.Stores.Postgres

Durable PostgreSQL live-chat stores: `PostgresLiveChatStore` implements `ILiveChatWidgetStore` and `ILiveChatSessionStore`.

```csharp
services.AddTicketFlow().AddLiveChat().AddPostgresLiveChatStores(dataSource);
await new PostgresLiveChatStore(dataSource).InitializeAsync();   // explicit schema creation
```

Tables (`ticketflow.livechat` migration catalog): `tf_livechat_widgets` (versioned JSON configuration), `tf_livechat_sessions`
(token hashes only; session ids unique per partition) and `tf_livechat_links` (session ↔ conversation links with read cursors,
at most 256 per session under an advisory lock, cascading with their session). Keys are `tf_key_v1` hashes with raw-column
comparison, every row has a `format_version`, and all SQL is parameterized. `PruneExpiredAsync` deletes expired sessions.
