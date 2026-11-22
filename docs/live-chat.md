# Live chat

Website live chat for TicketFlow: a widget configuration per inbox, anonymous or verified visitor sessions, visitor messages
through the E6 inbound pipeline, agent replies pushed in realtime and stored for fetch-on-reconnect, typing, read receipts,
attachments and help-center deflection.

| Package | Contents |
| --- | --- |
| `TicketFlow.Channels.LiveChat` | `LiveChatWidget` model and `ILiveChatWidgetService`, visitor sessions and `VisitorSessionTokens`, `LiveChatOrigin`, `VisitorIdentityHmac`, `ILiveChatIdentitySecretResolver`, `IVisitorTokenValidator`, `ILiveChatVisitorService`, `ILiveChatAgentService`, `LiveChatChannel` (`ITicketChannel`), `IAttachmentStore`, store contracts, `LiveChatCodec`, `InMemoryLiveChatStore`, `InMemoryAttachmentStore`, `AddLiveChat`, `AddInMemoryLiveChatStores`. |
| `TicketFlow.Channels.LiveChat.Stores.Postgres` | `PostgresLiveChatStore` (`ticketflow.livechat` migrations), `AddPostgresLiveChatStores`. |
| `TicketFlow.Realtime.SignalR` | `VisitorRealtimeHub` and `MapLiveChatAttachmentUpload` (see [realtime.md](realtime.md)). |
| `clients/widget` | The TypeScript web widget (shadow DOM, `@microsoft/signalr`). |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddChannels(options => options.WorkerPrincipal = scope => new ApiClientPrincipal(scope, new ApiClientId("channels")))
    .AddPostgresChannelStores(dataSource)
    .AddRealtime()
    .AddLiveChat()
    .AddPostgresLiveChatStores(dataSource)
    .AddTicketFlowSignalR(options => options.VisitorPartition = new AccessPartition("acme", "support", "prod", "web"));
services.AddSingleton<IAttachmentStore, MyBlobAttachmentStore>();                    // optional: attachments
services.AddSingleton<ILiveChatIdentitySecretResolver, MyInboxSecrets>();            // optional: HMAC identity
services.AddSingleton<IVisitorTokenValidator, MyTokenValidator>();                   // optional: signed tokens (IdentityFlow adapter in E11)
await new PostgresLiveChatStore(dataSource).InitializeAsync();
app.MapTicketFlowRealtime();
app.MapLiveChatAttachmentUpload();
```

## Setup

1. Create an inbox bound to `ChannelBinding(new ChannelKind("livechat"), "cfg-chat")`.
2. Create its channel endpoint with `IChannelEndpointService.CreateAsync(..., new ChannelEndpoint(livechat, "cfg-chat", inbox, "wk_live_123"))`.
   The endpoint **address is the public widget key**: it is unique per partition and resolves the workspace scope for visitors.
3. Create the widget configuration with `ILiveChatWidgetService.CreateAsync` (`livechat:manage` on the inbox; the inbox must be bound
   to the widget's configuration id).
4. Grant the channel worker API client `conversation:create`, `conversation:ingest`, `contact:read` and `contact:create` (as for all
   channels) — visitor messages are recorded as that principal.

`livechat:read` and `livechat:manage` (sensitive) are registered by `AddLiveChat()`; only Owner holds them by default. Widget
operations are authorized on `WorkspaceResources.Inbox`, so inbox-scoped grants apply (both inboxes when moving a widget).

## Widget configuration

`LiveChatWidget(configurationId, inbox, allowedOrigins, themeRef?, prechatFields?, greetingMessage?, awayMessage?, deflection?, attachments?, identity?)`:

- **Allowed origins** (1-32): absolute `http(s)://host[:port]` origins, normalized (lowercase IDN host, default ports omitted) and
  matched exactly — scheme, host and port. Paths, queries, fragments and user info are rejected.
- **Theme ref**: an opaque reference (≤ 256) passed to the widget; hosts map it to styles.
- **Pre-chat fields** (≤ 16): `name` (becomes the contact display name), `email` (validated and normalized), `text`; required flag,
  maximum length (≤ 512) and a `Sensitive` flag. At most one name and one email field.
- **Greeting / away message** override the inbox's. The widget state is **business-hours aware**: when the inbox has a business
  calendar and it is closed now (`BusinessCalendarCalculator.IsOpen`), `Online` is false and the away message is returned.
- **Deflection**: a public help-center portal and locale; `SearchArticlesAsync` calls `IHelpCenterSearch.SearchPublicAsync`
  (public portals only) with at most `MaximumResults` (1-10) hits.
- **Attachments**: `LiveChatAttachmentPolicy(maximumBytes ≤ 100 MiB, allowed media types)`; `null` disables uploads.
- **Identity**: `LiveChatIdentityPolicy(namespace, allowHmac, allowSignedToken, requireVerified)`.

Persisted as versioned JSON (`LiveChatCodec`, `{"v":1,...}`, unknown properties rejected).

## Visitor sessions

- `StartSessionAsync(partition, widgetKey, origin)` creates a session: a random id (`vs_` + 128 bits), an anonymous visitor id
  (`v_` + 128 bits) and a token `tfvs1.{sessionId}.{secret}` with a 256-bit secret. **Only SHA-256 of the secret is stored.** The
  token is returned once.
- `AuthenticateAsync(partition, token, origin)`: the token is parsed, the session loaded partition-wide, and the hash compared with
  `CryptographicOperations.FixedTimeEquals` (also for unknown sessions, against a placeholder, so timing does not reveal ids).
  Expired (`SessionLifetime`, default 30 days) and revoked sessions fail; the Origin must match the widget.
- **Rotation**: once the token is older than `TokenRotationInterval` (default 1 day) authentication reports `RotationRecommended`;
  `RotateTokenAsync` issues a new secret, extends the session and keeps the previous hash valid for `TokenRotationGrace` (default
  2 minutes, half-open) for concurrent tabs. The SignalR visitor hub rotates automatically on connect. When the reloaded session shows
  that another tab rotated within `TokenRotationInterval`, rotation is skipped and `VisitorTokenRotated.Token` is `null` (keep the
  current token), so concurrent tabs do not rotate twice.
- `EndSessionAsync` revokes the session (tokens stop working) and pushes `session.ended`; every connection of the session receives it
  and is then disconnected and closed by the realtime registry. Sessions revoked directly in the store (no event) are refused on
  their next call, and revoked or expired sessions receive no pushes: `LiveChatChannel` and `LiveChatSessionDirectory` skip
  non-live sessions.
- Every visitor operation reloads the session and refuses revoked or expired sessions. `VisitorSessionContext` can only be obtained
  from the service. Missing Origins are refused unless `LiveChatOptions.AllowMissingOrigin` (non-browser clients).
- `ILiveChatSessionStore.PruneExpiredAsync` deletes expired sessions and their links (no hosted worker until E11).

## Identity verification

- **HMAC** (like common widgets): the site's backend computes `hex(HMAC-SHA256(inboxSecret, UTF-8(userId)))`; the visitor submits
  user id and signature. The secret (32-256 bytes) comes from the application's `ILiveChatIdentitySecretResolver` per configuration;
  TicketFlow never stores it. Comparison is constant time.
- **Signed token**: `IVisitorTokenValidator.ValidateAsync(scope, configurationId, token)` returns `VisitorTokenAccepted(namespace,
  subject, displayName?)` or `VisitorTokenRejected` (IdentityFlow adapter in E11).
- A verified session records `VerifiedVisitorIdentity(namespace, value, method, verifiedAt)`; later messages carry a **verified
  `ExternalUser` contact identity** (namespace = the widget's identity namespace for HMAC, the validator's for tokens). Unverified
  visitors are a **claimed `WidgetVisitor` identity** (the visitor id). A session cannot be re-verified as a different identity.
  When an `IContactLinkingService` is registered (E16), identification links the anonymous visitor contact to the verified account
  (merging it into an existing account contact so earlier conversations follow); see [identity-linking.md](identity-linking.md).

## Messages

`SendMessageAsync(context, VisitorMessageDraft(clientMessageId, text?, attachmentRefs?))`:

1. Refuses when the inbox is disabled (`Unavailable`), verification is required but missing (`IdentityRequired`) or required pre-chat
   answers are missing (`PrechatRequired`).
2. Attachment refs must resolve through `IAttachmentStore.FindAsync(scope, session, ref)` — only the session's own uploads (≤ 10).
3. Builds an `InboundChannelMessage`: external id `lc:{session}:{clientMessageId}` (idempotent retries), sender evidence as above,
   thread id = visitor id, display name from pre-chat or the token, and runs `IInboundChannelPipeline` as the channel worker. The
   pipeline resolves the contact, correlates by thread (same contact, not closed) or starts a conversation, and records the message.
4. Links the conversation to the session (≤ 256 links per session) and echoes the message to the session's other connections.

Agent replies go through `IChannelReplyService` and the outbox dispatcher as for every channel. `LiveChatChannel.SendAsync`
publishes a `VisitorMessageEvent` to every session linked to the conversation and returns Accepted even when no visitor is connected
(store-and-forward); the message becomes `Sent`. Capabilities: attachments (≤ 10), typing, read receipts; no reply window.

**Fetch on reconnect**: `ReadMessagesAsync(context, conversation, afterSequence, limit)` returns visitor-visible messages (incoming
and outgoing message events, never notes or system events; failed outgoing messages are hidden) after a sequence cursor, with
`NextAfterSequence` for paging. Only conversations linked to the session are readable — anything else is `NotFound`.

**Typing and read receipts**: `SetTypingAsync` publishes visitor typing to the conversation topic; `MarkReadAsync` advances the stored
monotonic read cursor and publishes a read receipt. Agent typing and read receipts come from `IRealtimeAgentActions` and reach the
visitor anonymized. Read receipts are not mirrored into message delivery state.

**Attachments**: `UploadAttachmentAsync(context, fileName, mediaType, stream)` checks the widget policy (media type allow-list,
lowercase, parameters stripped), sanitizes the file name (path segments removed, control characters rejected) and streams the
content to `IAttachmentStore.StoreAsync` through a stream that throws past the size limit (`TooLarge`). Over SignalR hosts, the
upload endpoint takes the raw body with `Authorization: Bearer {token}`. Download serving is the host's (its storage references).

## Agent view

`ILiveChatAgentService.GetVisitorAsync(actor, conversation)` returns the latest linked session's visitor id, verification, identity,
display name and pre-chat answers. It needs `conversation:read`; values are masked from the `contact:read` decision on the
conversation's contact: identity value as `contact.external-id`, email answers as `contact.email`, sensitive answers as
`contact.attribute.<key>` (masked unless an Access field policy for that path unmasks them — fail closed), everything personal when
`contact:read` is denied.

## Visitor hub (SignalR)

`VisitorRealtimeHub` is anonymous until `Connect(widgetKey, token?)` succeeds. The partition comes from `IVisitorPartitionResolver`
(default `TicketFlowSignalROptions.VisitorPartition`), the Origin from the request header. Without a token a new session starts;
with a token it authenticates (an invalid token returns `unauthenticated`, and the widget starts over). The response carries the
issued or rotated token, the visitor id, widget state and conversations. Then: `SubmitPrechat`, `Identify("hmac"|"token", ...)`,
`SendMessage`, `ListConversations`, `FetchMessages`, `Typing`, `MarkRead`, `SearchArticles`, `EndSession`. Tokens are sent in hub
calls, never in URLs. `Typing` is throttled per connection and conversation (see realtime.md). Hosts should rate-limit the visitor hub and upload endpoint (anonymous session creation).

## Widget token storage

The web widget keeps the session token in `localStorage` by default (reloads resume the chat). Script injected into the host page
(XSS) could read it and act as that visitor for the session's lifetime; `tokenStorage: "memory"` keeps it in memory only. See
`clients/widget/README.md`.

## Store contracts

| Contract | Guarantees |
| --- | --- |
| `ILiveChatWidgetStore` | Scope-confined; create at revision 1 (conflict when present); compare-and-set update/delete; ≤ 1000 per scope (atomic). |
| `ILiveChatSessionStore` | Session ids unique per partition; partition-wide `GetAsync` returns the full scope; token hashes only; CAS updates with immutable identity fields; idempotent links, ≤ 256 per session (atomic); monotonic cursors; store-wide expiry prune deleting links. |

`tests/Channels/TicketFlow.Channels.LiveChat.StoreContracts` verifies both against `InMemoryLiveChatStore` and PostgreSQL (two
independent data sources, concurrent links and CAS races). `PostgresLiveChatStore` uses `tf_key_v1` keys with raw-column comparison,
`format_version` columns, advisory locks for widget and link capacity, links cascading with sessions, `SKIP LOCKED` pruning and
parameterized SQL; undecodable rows throw. Instants are stored at microsecond precision.

## Not yet implemented

- Hosted pruning of expired sessions and rate limiting (E11 hosting).
- Mirroring visitor read receipts into outgoing message delivery state; conversation-ended notices to the widget.
- Attachment download authorization helpers (hosts serve their storage references).
- The IdentityFlow `IVisitorTokenValidator` adapter (E11) and the WalFlow change-capture fan-out (E7 T08 follow-up).
