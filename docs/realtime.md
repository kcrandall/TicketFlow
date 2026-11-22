# Realtime

Realtime fan-out pushes live updates to agents (conversation changes, presence, reply locks, typing, read receipts, agent status,
assignments, queue changes) and to live-chat visitors (agent replies, typing, read receipts). It is transport-neutral; SignalR is
an optional hosting package.

| Package | Contents |
| --- | --- |
| `TicketFlow.Realtime.Abstractions` | `RealtimeTopic` union (`ConversationTopic`, `InboxQueueTopic`, `AgentTopic`, `VisitorSessionTopic`), `RealtimeSubscriber` union (`AgentSubscriber`, `VisitorSubscriber`), `RealtimeEvent` union, `RealtimePublication`, `IRealtimePublisher`, `IRealtimeBus`, `IRealtimeConnection`, `IRealtimeConnectionLifetime`, `IRealtimeSubscriptions`, `IRealtimeAgentActions`, `IVisitorSessionDirectory`, `VisitorSessionId`, `RealtimeAgentStatus`, `RealtimeActivity`. No routing dependency: status and activity are realtime-owned enums. |
| `TicketFlow.Realtime` | `RealtimeSubscriptionRegistry`, `InProcessRealtimeBus`, `BusRealtimePublisher`, `RealtimeOutboxHandler` (consumer `realtime`), `RealtimeRoutingEventObserver` (the only routing-aware part, mapping routing enums), `RealtimeAgentActions`, `AddRealtime`. |
| `TicketFlow.Realtime.SignalR` | `AgentRealtimeHub`, `VisitorRealtimeHub`, `MapTicketFlowRealtime`, `MapLiveChatAttachmentUpload`, `IRealtimePrincipalResolver`, `IVisitorPartitionResolver`, `RealtimeWireFormat`, `AddTicketFlowSignalR`. The only package with a `Microsoft.AspNetCore.App` framework reference. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddConversations().AddRouting()
    .AddRealtime()                       // registers the realtime outbox consumer and routing observer
    .AddTicketFlowSignalR(options => options.ResolvePrincipal = MyPrincipals.ResolveAsync);
app.MapTicketFlowRealtime().Agent.RequireAuthorization();
await scope.ServiceProvider.GetRequiredService<ConversationOutboxDispatcher>().RegisterAsync();   // registers "realtime"
```

## Topics and subscription authorization

| Topic | Who may subscribe | Delivered |
| --- | --- | --- |
| `ConversationTopic(scope, conversation)` | Agents with `conversation:read` on the conversation (NotFound before authorization). | Changes, presence, reply locks, assignments, queue changes, typing, read receipts of that conversation. |
| `InboxQueueTopic(scope, inbox)` | Agents with `inbox:read` on the inbox. | Changes and queue events of conversations in the inbox **that match the subscriber's planned `conversation:read` filter** (`IAccessQueryPlanner` + `AccessFilterEvaluator`, re-planned every `QueueFilterLifetime`, default 30 s). Team- or condition-scoped agents see only their conversations. |
| `AgentTopic(scope, user)` | Only that user (a `UserPrincipal` with the same id). | Own status changes, routed assignments, changes of conversations currently assigned to the user. |
| `VisitorSessionTopic(scope, session)` | Only the visitor holding that session (authenticated by the live-chat package). | Visitor-facing events of the session. |

Subscriptions are bounded (`MaximumSubscriptionsPerConnection`, default 256; `MaximumConnections` per node, default 100 000) and
must be in the subscriber's scope.

## Delivery pipeline and filtering

- **Publishers never wait for delivery.** `RealtimeSubscriptionRegistry.DeliverAsync` (the bus handler) only queues the publication
  in a bounded queue (`MaximumPendingPublications`, default 10 000, oldest dropped) drained by one background fan-out worker per node.
  The outbox handler, routing observer and agent actions therefore return immediately.
- **One conversation read per change.** The worker reads each conversation once per change (the outbox handler publishes the same
  change to three topics; later publications of that revision reuse the snapshot) plus one read in the outbox handler for the
  assignee.
- **In-memory decisions per principal.** Agent subscribers are grouped by principal. Each principal has an
  `IAccessDecisionContext` (`IAccessEvaluator.LoadDecisionContextAsync` for `conversation:read` and `note:read`) cached on the node and
  reloaded after `DecisionContextLifetime` (default 30 s); decisions are made in memory against the current conversation snapshot.
  So a revoked or denied grant stops deliveries on existing subscriptions within that lifetime (new subscriptions are point-checked
  immediately). A context that fails is reloaded once in the current scope; failures fail closed.
- **Queue topics** match the subscriber's planned `conversation:read` filter. Whenever the filter is re-planned (every
  `QueueFilterLifetime`, default 30 s) `inbox:read` is re-authorized with a point check; when it was revoked the subscription is
  removed and nothing is delivered.
- **Bounded per-connection sending.** Filtered events go to a per-connection queue (`ConnectionQueueCapacity`, default 256, oldest
  dropped — payloads are identifiers, clients refetch) drained by a per-connection sender with `SendTimeout` (default 10 s). A slow,
  stalled or throwing client affects only its own queue. `DroppedCount` reports drops.
- **Visitor session end.** A delivered `VisitorSessionEndedEvent` disconnects each connection of that session after sending it and
  closes the transport when the connection implements `IRealtimeConnectionLifetime` (the SignalR connection aborts).

Filtering rules:

- Agent deliveries of conversation events need `conversation:read` (or the queue filter) on the conversation as stored now, so
  reassignment away from a team-scoped agent and label/attribute conditions apply to live connections.
- Note summaries in change notifications are removed unless the subscriber holds `note:read`; a change that only added notes is
  dropped for them.
- A deleted conversation can no longer be authorized: its `Deleted` notice reaches only subscribers already granted the conversation
  topic (they knew it existed); queue and agent topics drop it.
- Agents never receive `VisitorMessageEvent` or `VisitorSessionEndedEvent`. Visitors receive only their own session's messages and
  session events plus typing/read receipts from the support side, with the agent's identity replaced by an anonymous `SupportActor`;
  the visitor's own typing is not echoed.
- Payloads are never logged.

### Payload choice: notifications with identifiers, then refetch

Agent-facing events carry identifiers only: conversation, inbox, revision, change kind, event summaries (sequence, kind, message
id, direction), delivery state, presence/lock user ids. They never carry bodies, attribute values, channel thread ids or other
maskable fields. Clients refetch details through the Access-checked services (`IConversationService.GetAsync` /
`ReadTimelineAsync`), which apply masking per principal. This keeps one masking implementation (the conversation service) instead
of re-masking in the realtime layer, and means a masked field can never leak through realtime even if policies change between
publish and delivery. The cost is one read per notification on the client.

The only content-bearing event is `VisitorMessageEvent` (a dispatched agent reply or the visitor's own message), delivered only to
the owning visitor session; it contains only what that visitor is entitled to see (their conversation's public messages — notes are
never dispatched to channels).

## Sources

- `RealtimeOutboxHandler` (consumer `realtime`): each conversation outbox entry becomes a `ConversationChangedEvent` on the
  conversation topic, the inbox queue topic and the current assignee's agent topic. Redelivery publishes again (clients deduplicate by
  revision).
- `RealtimeRoutingEventObserver` (`IRoutingEventObserver`, E8): `AgentStatusChanged` → agent topic; `ConversationPresenceChanged`,
  `ReplyLockChanged` → conversation topic; `ConversationRouted` → agent and conversation topics; `ConversationOverflowed` and
  `QueueWaitExceeded` → conversation topic and the affected inbox queue. Routing swallows observer failures.
- `IRealtimeAgentActions`: agent typing (`conversation:reply`) and read receipts (`conversation:read`, sequence within the
  conversation) on the conversation topic and, via `IVisitorSessionDirectory` (implemented by live chat), the linked visitor sessions.
  Ephemeral: nothing is stored.
- Live chat publishes visitor messages, visitor typing and visitor read receipts (see [live-chat.md](live-chat.md)).

## SignalR hosting

- **Agent hub** (`/ticketflow/realtime/agent` by default): the host authenticates the HTTP connection; `IRealtimePrincipalResolver`
  (or `TicketFlowSignalROptions.ResolvePrincipal`) maps the `ClaimsPrincipal` to a `PrincipalRef` once per connection, otherwise the
  connection is aborted. **Cross-site WebSocket hijacking:** browsers attach cookies to cross-site WebSocket handshakes and CORS does
  not apply to WebSockets, so cookie-authenticated hosts must set `TicketFlowSignalROptions.AgentAllowedOrigins` (connections with a
  foreign `Origin` are aborted in `OnConnectedAsync`; `RequireAgentOrigin` also rejects missing origins), use `SameSite=Lax` or
  `Strict` auth cookies, and keep CORS for the hub limited to the console origins (`AllowCredentials` only for those). Bearer-token
  hosts are not exposed to this attack.
- **Typing throttle** (both hubs): at most one typing event per connection and conversation per `TypingThrottleInterval` (default
  2 s); one stop after a start always passes. Throttled calls succeed without publishing. Methods: `Subscribe(kind, id)` with kind `conversation`, `queue` or `agent`, `Unsubscribe`, `Typing`,
  `MarkRead`. The team snapshot in a `UserPrincipal` is taken at connect time; reconnect to refresh it.
- **Visitor hub** (`/ticketflow/realtime/visitor`): see [live-chat.md](live-chat.md).
- Events arrive as `tf.event` with `{"v":1,"type":...,"topic":...,"data":{...}}`. Types: `conversation.changed`,
  `presence.changed`, `replylock.changed`, `agent.status`, `conversation.assigned`, `queue.changed`, `typing`, `read`, `message`,
  `session.ended`. Enum values are kebab-case tokens. Topics: `conversation:{id}`, `queue:{inbox}`, `agent:{user}`, `visitor` (session
  ids are never sent).
- Hub results are `{ok, error, data}` with error tokens such as `forbidden`, `not-found`, `invalid`, `unauthenticated`,
  `origin-not-allowed`, `limit-reached`.

## Scale-out (documented, not shipped)

The subscription registry is per node. Two pieces are needed for several nodes:

1. **Publications to every node.** Replace `IRealtimeBus` with an implementation over a shared transport — Redis pub/sub, a broker,
   or change capture (see the WalFlow follow-up, E7 T08). It must serialize `RealtimePublication`s with a versioned envelope
   (identifiers only, plus the visitor message body for visitor topics) and deliver each publication to every node's registry; each
   node then authorizes and filters for its own connections. The outbox handler runs once per entry (one claiming worker), so it
   must publish to the shared bus, not only in-process.
2. **Hub calls to any node.** Sticky sessions are required for SignalR transports other than WebSockets. A SignalR backplane such as
   `Microsoft.AspNetCore.SignalR.StackExchangeRedis` (`services.AddSignalR().AddStackExchangeRedis(...)`) is optional: TicketFlow
   delivers to connections through the node that holds them, so a backplane matters only for host-side `IHubContext` broadcasts.

Without these, run one realtime node (other nodes may still run the outbox dispatcher only if they share the node's bus).

## Not yet implemented

- A cross-node `IRealtimeBus` implementation and a WalFlow change-capture source (E7 T08 follow-up).
- Supervisor views of other agents' status topics, workspace-wide presence rosters and persisted agent read state.
- Re-resolving agent principals (team snapshots) during long-lived connections.
