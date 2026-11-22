# TicketFlow.Channels

The TicketFlow channel runtime.

- `InboundChannelPipeline` (`IInboundChannelPipeline`): endpoint → inbox (binding and enabled checks) → contact resolve-or-create
  from the sender evidence → conversation by reference chain (In-Reply-To/References) or channel thread, same contact only, or a
  new conversation → idempotent `AppendIncomingAsync` → channel state (recipient, last incoming time, references).
- `ChannelOutboundDispatcher`: an `IConversationOutboxHandler` (consumer `channels.dispatch`) that sends outgoing replies through
  the registered `ITicketChannel`, records dispatch attempts, retries retryable rejections with the outbox backoff, fails messages
  after `MaximumSendAttempts`, never retries unknown outcomes and mirrors results into message delivery state.
- `ChannelDeliveryIngestor`, `ChannelReplyService` (refuses replies outside the reply window with `ReplyWindowClosed`) and
  `ChannelEndpointService` (`channel:manage` / `channel:read`).
- Development `InMemoryChannelStore` for the four channel state store contracts.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddInMemoryAccessStores()
    .AddWorkspaces().AddInMemoryWorkspaceStores()
    .AddContacts().AddInMemoryContactStores()
    .AddConversations().AddInMemoryConversationStores()
    .AddChannels(options => options.WorkerPrincipal = scope => new ApiClientPrincipal(scope, new ApiClientId("channels")))
    .AddInMemoryChannelStores();                      // development only
```

The worker principal must hold grants (for example a custom role with `conversation:create`, `conversation:ingest`,
`contact:read`, `contact:create`); automation and system principals are denied by Access. No hosted worker is included: hosts run
`ConversationOutboxDispatcher.RunOnceAsync` in their loop.
