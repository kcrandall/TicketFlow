# TicketFlow.Channels.LiveChat

The live-chat channel for TicketFlow.

- `LiveChatWidget` per inbox (allowed origins, theme ref, pre-chat fields, greeting/away overrides, help-center deflection,
  attachment policy, identity policy) administered through `ILiveChatWidgetService` (`livechat:read` / `livechat:manage` on the
  inbox). Visitors reach a widget through the inbox's channel endpoint, whose address is the public widget key.
- Visitor sessions: random ids and 256-bit secret tokens (`tfvs1.{session}.{secret}`), SHA-256 hashes at rest, expiry, rotation with
  a short grace window, constant-time verification, exact Origin checks.
- Identity: HMAC-SHA256 of the user id with an application-resolved inbox secret (`ILiveChatIdentitySecretResolver`) or a signed token
  (`IVisitorTokenValidator`); verified visitors become verified `ExternalUser` contact identities, others claimed `WidgetVisitor`.
- `ILiveChatVisitorService`: messages through the E6 inbound pipeline as the channel worker, fetch-on-reconnect with sequence cursors,
  typing, read receipts, attachments through the application's `IAttachmentStore`, deflection through `IHelpCenterSearch`.
- `LiveChatChannel` (`ITicketChannel`, kind `livechat`) pushes dispatched agent replies to the visitor's realtime session topic.
- `ILiveChatAgentService.GetVisitorAsync`: masked visitor context for agents.
- `InMemoryLiveChatStore` and `InMemoryAttachmentStore` are development examples.

```csharp
services.AddTicketFlow()
    .AddChannels(options => options.WorkerPrincipal = scope => new ApiClientPrincipal(scope, new ApiClientId("channels")))
    .AddRealtime()
    .AddLiveChat(options => options.SessionLifetime = TimeSpan.FromDays(7))
    .AddPostgresLiveChatStores(dataSource);   // or .AddInMemoryLiveChatStores()
services.AddSingleton<IAttachmentStore, MyBlobAttachmentStore>();
```

See `docs/live-chat.md`.
