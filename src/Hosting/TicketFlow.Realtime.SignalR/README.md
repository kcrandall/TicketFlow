# TicketFlow.Realtime.SignalR

Optional ASP.NET Core SignalR hosting for TicketFlow realtime (the only TicketFlow package with a `Microsoft.AspNetCore.App`
framework reference).

- `AgentRealtimeHub`: the host authenticates connections; `IRealtimePrincipalResolver` maps the user to a `PrincipalRef` once per
  connection. `Subscribe("conversation"|"queue"|"agent", id)`, `Unsubscribe`, `Typing`, `MarkRead`.
- `VisitorRealtimeHub`: `Connect(widgetKey, token?)` authenticates the session token (or starts a session) against the request
  Origin; then `SubmitPrechat`, `Identify`, `SendMessage`, `ListConversations`, `FetchMessages` (reconnect with cursor), `Typing`,
  `MarkRead`, `SearchArticles`, `EndSession`. Tokens travel in hub calls, never in URLs.
- `MapLiveChatAttachmentUpload`: `POST` raw file bodies with `Authorization: Bearer {token}`.
- Events arrive as `tf.event` with a versioned `RealtimeWireEvent` (`{"v":1,"type":...,"topic":...,"data":{...}}`).

```csharp
services.AddTicketFlow()
    .AddRealtime().AddLiveChat()
    .AddTicketFlowSignalR(options => options.VisitorPartition = new AccessPartition("tenant", "project", "prod", "web"));
services.AddSingleton<IRealtimePrincipalResolver, MyPrincipalResolver>();
app.MapTicketFlowRealtime().Agent.RequireAuthorization();
app.MapLiveChatAttachmentUpload();
```

Scale-out: use a SignalR backplane (for example `Microsoft.AspNetCore.SignalR.StackExchangeRedis`) so hub calls reach any node,
and replace `IRealtimeBus` so publications reach every node's registry. Neither is shipped; see docs/realtime.md.
