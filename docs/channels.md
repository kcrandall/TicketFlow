# Channels and EnvelopeFlow integration

Channels connect conversations (E5) to the outside world: inbound messages become timeline messages, agent replies are dispatched
from the conversation outbox, provider receipts update delivery state and channel rules (reply windows, attachment support) are
enforced. Seven packages ship in E6:

| Package | Contents |
| --- | --- |
| `TicketFlow.Channels.Abstractions` | `ITicketChannel`, `ChannelCapabilities`, `IChannelRegistry`, `InboundChannelMessage`, `OutboundChannelMessage`, the `ChannelSendResult` union, `ChannelDeliveryReport`, reply-window evaluation, service contracts with union results, `ChannelPermissions`, four channel state store contracts, `ChannelText` helpers. |
| `TicketFlow.Channels` | `InboundChannelPipeline`, `ChannelOutboundDispatcher`, `ChannelDeliveryIngestor`, `ChannelReplyService`, `ChannelEndpointService`, `ChannelRegistry`, `InMemoryChannelStore`, DI (`AddChannels`, `AddInMemoryChannelStores`). |
| `TicketFlow.Channels.Stores.Postgres` | `PostgresChannelStore` (all four store contracts), `PostgresChannelMigrations` (`ticketflow.channels`), `AddPostgresChannelStores`. |
| `TicketFlow.Channels.Api` | The generic API channel: application-called inbound (`ApiChannelInbound`), HMAC-signed outbound webhooks (`ApiChannel`, `ApiWebhookSignature`, `ApiWebhookReplayGuard`), SSRF policy (`ApiWebhookDestinationPolicy`, `ApiWebhookHttp`), `AddApiChannel`. |
| `TicketFlow.Integrations.EnvelopeFlow` | EnvelopeFlow adapter: `EnvelopeFlowInboundEventHandler` (`IInboundEventHandler`), email/SMS/WhatsApp/Slack channels, destination encoding for provider binding delegates. References only `TicketFlow.Channels.Abstractions` and EnvelopeFlow abstraction packages at the pinned central version `0.1.0-alpha.3.1` (nuget.org). |
| `TicketFlow.Integrations.EnvelopeFlow.Surveys` | `EnvelopeFlowSurveyInvitationSender` and `AddEnvelopeFlowSurveySender`, kept separate so the adapter does not force a Surveys dependency. |
| `TicketFlow.Integrations.Surveys` | `ConversationResolvedSurveyHandler`: an outbox handler that turns resolved conversations into E12 `ConversationResolvedNotification`s (`AddConversationSurveys`). |

The survey bridge lives in its own small package rather than in `TicketFlow.Channels` so channel users do not take a Surveys
dependency and survey users do not need channels.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddChannels(options => options.WorkerPrincipal = scope => new ApiClientPrincipal(scope, new ApiClientId("channels")))
    .AddPostgresChannelStores(dataSource)
    .AddEnvelopeFlowIntegration()
    .AddEnvelopeFlowEmailChannel()                    // needs IEmailTransport + IEnvelopeFlowSenderDirectory
    .AddEnvelopeFlowWhatsAppChannel()                 // needs IWhatsAppTransport
    .AddSurveys().AddPostgresSurveyStores(dataSource)              // + TicketFlow.Integrations.EnvelopeFlow.Surveys
    .AddConversationSurveys()
    .AddEnvelopeFlowSurveySender(options => options.LinkFactory = message => new Uri($"https://support.example.com/s/{message.Token.Value}"));
await new PostgresChannelStore(dataSource).InitializeAsync();
await scope.ServiceProvider.GetRequiredService<ConversationOutboxDispatcher>().RegisterAsync();   // registers channels.dispatch, surveys.resolved
```

## Principals

Channel work runs as a grant-holding principal, never as an automation or system principal (Access denies those). Workers obtain
it from `IChannelWorkerPrincipalResolver` (Channels.Abstractions); the default resolver reads `ChannelsOptions.WorkerPrincipal`
(an API client per scope) and grant it a custom role with `conversation:create`,
`conversation:ingest`, `contact:read` and `contact:create`. The inbound pipeline runs as the principal that calls it (adapters use
the worker); the dispatcher uses the worker only for delivery updates. A misconfigured worker surfaces as outbox retries
(delivery updates are retried; accepted submissions are never resent).

## Channel contract

- `ITicketChannel`: `Kind` (the inbox `ChannelBinding.Kind`), `Capabilities`, `RecipientKinds` (identity kinds it delivers to, in
  preference order) and `SendAsync(OutboundChannelMessage)`. Channels must not throw for submission outcomes; an exception means
  nothing was submitted.
- `ChannelCapabilities`: `ChannelFeatures` flags (`Attachments`, `RichContent`, `ReplyWindow`, `Threading`, `Typing`,
  `DeliveryReceipts`, `ReadReceipts`, `Templates`), the reply window (exactly with the flag, 1 minute–30 days), maximum attachments
  (exactly with the flag) and maximum body length.
- `ChannelSendResult = ChannelSendAccepted(externalMessageId, threadReference) | ChannelSendRejected(code, retryable, retryAfter) |
  ChannelSendUnknown(code)`. Codes are failure-code tokens (`ChannelText.FailureCode` normalizes provider codes).
- `InboundChannelMessage`: trusted scope (from routing configuration, never callback fields), channel kind and binding
  configuration id, external message id (≤ 1024; the idempotency key per inbox), external thread id, In-Reply-To and ≤ 32
  References, `ChannelSenderEvidence(kind, value, verified, namespace)`, text and/or HTML body, ≤ 32 attachment refs (never bytes),
  subject, sender display name and received-at. Everything is bounded and `ToString` redacts content.
- `ChannelDeliveryReport(scope, channel, configurationId, externalMessageId, Sent|Delivered|Read|Failed, occurredAt, failureCode)`.

## Endpoints

A `ChannelEndpoint(kind, configurationId, inbox, address?)` binds an inbox's `ChannelBinding` to the inbox; the optional address
(`mailgun:acct`, `twilio:AC1:+19995550000`, `slack:T1`) is unique per partition (tenant, project, environment, app) and kind, so
adapters find the endpoint — and therefore the workspace scope — from provider routing data. `IChannelEndpointService` authorizes
`channel:manage` (sensitive; inbox-scoped grants apply, both inboxes when moving) and `channel:read`, and requires the inbox's
binding to match the endpoint. Addresses are partition-wide routing claims over a provider account, so binding or changing one also
asks `IChannelAddressOwnershipValidator`. The default `CrossScopeAddressOwnershipValidator` refuses (`AddressNotOwned`) an address
nested under (`mailgun:acct:x` below `mailgun:acct`) or enclosing (`twilio:AC9` above `twilio:AC9:+1555`) an address bound by
another scope, so a workspace administrator cannot shadow another scope's traffic. It cannot prove account ownership: applications
that let workspace administrators bind addresses should register a validator that checks provider-account ownership. The check
precedes the store write (exact duplicates remain atomically refused by the store). `channel:read` also guards dispatch records (`GetDispatchAsync`). Both permissions are registered by
`AddChannels()`; only Owner holds them by default (Admin's set is the built-in catalog).

## Inbound pipeline

`IInboundChannelPipeline.ReceiveAsync(actor, message)`:

1. Scope check (actor scope = message scope), endpoint by (scope, kind, configuration id), inbox exists, binding matches
   (`ChannelMismatch`) and inbox enabled (`InboxDisabled`).
2. Sender evidence normalized with `ContactIdentityAddress.Normalize` (`InvalidSender`).
3. Redelivery: if the external id is already recorded in the inbox, `conversation:ingest` is checked on its conversation and the
   original message is returned as `Replayed` (no contact resolution). A replay never changes the recorded recipient.
4. Contacts resolve-or-create (`IContactService.ResolveAsync`) with verified or claimed evidence (source = channel kind) and
   `ChannelsOptions.ClaimedIdentityMatching` (default `WhenUnique`); ambiguous evidence and blocked contacts are rejected.
5. Correlation: first the reference chain (normalized In-Reply-To, then References newest first) against stored references of
   the inbox, then the channel thread looked up per contact in channel state (`IChannelConversationStateStore.FindByThreadAsync`,
   so a shared Slack thread with alternating speakers keeps one conversation per contact), then E5 `FindByThreadAsync` for
   conversations without channel state. A match is used only when it belongs to the resolved contact and is not closed — a forged Message-Id from another sender starts a new conversation instead of injecting into someone else's.
6. `AppendIncomingAsync` (reopens pending/snoozed/resolved) or `StartAsync` with the channel thread; both idempotent by external id.
7. Channel state: contact, recipient (latest inbound sender), thread id, subject (reply prefixes stripped), last incoming time
   (provider receive time, monotonic, clamped to now + 2 minutes so a skewed or forged timestamp cannot hold a reply window open)
   and Message-Id, plus the reference chain.

Bodies: text is preferred over HTML; control characters other than tab/CR/LF become spaces, invalid UTF-16 is replaced and bodies
are cut at 64 KiB UTF-8.

## Replies, reply windows and dispatch

`IChannelReplyService.ReplyAsync` checks existence, `conversation:reply`, then channel rules, and returns
`ChannelReplyResult = ConversationApplied | NotFound | Conflict | Refused | ReplyWindowClosed(closedAt)`. The window is measured
from the channel state's last incoming time (falling back to the conversation's) and is half-open; a new customer message reopens
it. `GetReplyWindowAsync` tells composers whether and until when a free-form reply is possible. Attachments beyond the channel's
capability are refused as `Invalid`. Conversations whose inbox kind has no registered channel are not restricted.

`ChannelOutboundDispatcher` (consumer `channels.dispatch`) handles `Started`/`EventsAppended` outbox entries with outgoing messages:

- It reads the message body through trusted store access, picks the recipient (recorded state, else the contact's identities of
  the channel's recipient kinds, verified first), re-checks the window and attachments (refused messages fail with
  `reply-window-closed`, `no-recipient` or `attachments-unsupported`) and builds the thread context (In-Reply-To = last inbound
  Message-Id; References = root plus the newest `MaximumReplyReferences`; subject).
- Dispatch records (`ChannelDispatch`): `Sending` is written before the channel call. If a redelivered entry still finds `Sending`
  the earlier attempt's outcome is unknown and the message fails with `outcome-unknown` (never resent).
- `Accepted` → message `Sent` with the provider id; the email thread reference is stored so customer replies thread.
  Retryable `Rejected` (and channel exceptions) → `RetryScheduled` with `NextAttemptAt = now + Retry-After`, then the handler throws
  `ChannelDispatchRetryException` (a `ConversationOutboxRetryException`), and the outbox dispatcher releases the entry at the later of
  its exponential backoff and the provider's `Retry-After`; a redelivered entry whose attempt is not due yet is released again for
  the remaining time without sending; after `MaximumSendAttempts` (default 5, must stay below the outbox `MaximumAttempts`) the
  message fails (dead-letter). Permanent rejections fail immediately; `Unknown` fails with its code and is never retried.
- Delivery updates are idempotent; terminal dispatch records are re-applied on redelivery, so a failed update converges.

Delivery receipts go through `IChannelDeliveryIngestor.ReportAsync` → `UpdateDeliveryStatusAsync` by inbox and provider id
(monotonic; stale or repeated reports are `Replayed`; concurrent-update conflicts are `DeliveryReportRejection.Conflict`, which the
EnvelopeFlow handler throws on so the receipt is retried).

## API channel

- Inbound: applications call `ApiChannelInbound.ReceiveAsync(actor, configurationId, ApiInboundMessage)` and
  `ReportDeliveryAsync(...)` from their own authenticated endpoints.
- Outbound: `POST` of a versioned JSON payload (`"v":1`, `"type":"message.outgoing"`, conversation/message/contact ids, recipient,
  body, content type, attachment refs, thread, optional template) with headers `TicketFlow-Webhook-Id` (the stable idempotency key),
  `-Timestamp` (Unix seconds), `-Signature` (`v1=` + hex HMAC-SHA256 over `v1\n{timestamp}\n{id}\n{body}`, 32-64 byte key) and
  `-Key-Id`. Receivers verify with `ApiWebhookSignature.Verify` (constant time, 5-minute window, 1-minute skew) or
  `ApiWebhookReplayGuard` (rejects replays of an accepted (id, timestamp) inside the window; fails closed when full) and deduplicate
  ids durably. 2xx = accepted (optional JSON `externalMessageId`), 408/429/5xx = retryable (`Retry-After` honoured), other 4xx =
  permanent, connection failures = retryable, timeouts or failures after writing = unknown. The response body (read after headers)
is bounded by the same timeout; a stalled body only loses the optional external id.
- SSRF: HTTPS only, allowed ports (default 443), no user info or fragment, no `localhost`, optional exact host allow-list, IP literals
  only when public; at connect time the host is resolved once, every answer must be public (loopback, RFC 1918, link-local, CGNAT,
  multicast, documentation, benchmarking, cloud metadata `169.254.169.254`/`168.63.129.16`, non-global IPv6 are blocked) and only
  those addresses are dialled; no redirects, proxies or cookies.
- Destinations and keys come from the application's `IApiChannelDestinationResolver`; TicketFlow never stores keys.

## EnvelopeFlow adapter

- `EnvelopeFlowInboundEventHandler` is the `IInboundEventHandler` behind EnvelopeFlow's `InboundProcessor`. It maps the provider
  to a kind (`ProviderKinds`), finds the endpoint by `{provider}:{account}:{local}` (message `To`, receipt `From`/`To`) then
  `{provider}:{account}` within the event's scope partition and runs as the worker principal.
  - Messages: email threads by In-Reply-To (`References` is not exposed by EnvelopeFlow — gap 2 in
    [envelopeflow-integration.md](envelopeflow-integration.md)); SMS and WhatsApp thread by the sender number; Slack by
    `{channel}:{root ts}` (root from `InReplyTo` or the message id). WhatsApp and Slack senders are recorded as verified
    identities (`VerifiedSenderKinds`), email and SMS as claimed. Attachment references become `envelopeflow:{provider}:{ref}`
    attachment refs (never downloaded).
  - Receipts: Accepted/Sent → Sent, Delivered, Read, Failed/Undelivered → Failed (provider code or `bounce-*`), Deferred ignored.
    Receipts for messages not yet correlated throw `EnvelopeFlowReceiptPendingException` within `ReceiptCorrelationGrace` (default
    10 minutes) so the durable receipt retries, then are dropped. Feedback is ignored.
  - Unknown providers/endpoints and invalid messages are dropped; store rejections and missing grants throw (retry).
- Channels: email (`In-Reply-To`, `References`, `Re:` subject, `EmailSubmissionContext` = idempotency key; provider ids that are
  Message-Ids — SMTP, Mailgun — are recorded as thread references), SMS (sender number or messaging service from
  `IEnvelopeFlowSenderDirectory`), WhatsApp (24-hour window capability, templates for `ChannelTemplate`, optional quoted reply),
  Slack (`ChatMessage.ThreadId` = bare root ts; Teams can use `EnvelopeFlowChatChannel` unthreaded). Retry advice maps to
  retryable rejections; `Unknown` is never retried. Outbound attachments are not sent yet (TicketFlow stores references only).
- WhatsApp/chat destinations: `RecipientBinding(scope, contactId, IdentityReference("ticketflow-contact", contactId))` plus a
  `tfc1.` destination id encoding (workspace, configuration, conversation). Host binding delegates (for example
  `MetaWhatsAppTransport`'s) call `EnvelopeFlowDestinationContextResolver.ResolveAsync(owner, destinationId)` to get the recipient,
  thread and `LastCustomerMessageAtUtc`; foreign scopes and mismatched contacts resolve to `null`.
- Channel registrations are per kind: calling `AddEnvelopeFlowEmailChannel()` (etc.) twice registers one channel.
- `EnvelopeFlowSurveyInvitationSender` (package `TicketFlow.Integrations.EnvelopeFlow.Surveys`) sends the survey link over the conversation's EnvelopeFlow channel to the recorded recipient;
  outside the WhatsApp window it uses `EnvelopeFlowSurveyOptions.WhatsAppTemplate` (one body parameter: the link).

## Survey bridge

`ConversationResolvedSurveyHandler` (consumer `surveys.resolved`) reads each appended `StatusChanged` event; when the new status is
Resolved it builds a `ConversationResolvedNotification` from trusted store reads (inbox, team, assignee, contact locale when it is a
valid survey locale) and calls `ISurveyScheduler.HandleResolvedAsync`. Scheduling is idempotent per (survey, conversation).

## Store contracts

| Contract | Guarantees |
| --- | --- |
| `IChannelEndpointStore` | Scope-confined; create at revision 1 (conflict when present), compare-and-set update/delete; address unique per partition and kind (atomic); ≤ 1000 endpoints per scope; partition-wide `FindByAddressAsync` (full scope) and `ListByAddressPrefixAsync` (≤ 100, ordinal). |
| `IChannelConversationStateStore` | Scope-confined create/compare-and-set (not found when absent); conversation, inbox and channel immutable; `FindByThreadAsync` by inbox, channel, thread and contact (≤ 16). |
| `IChannelThreadStore` | Reference unique per (scope, inbox) — duplicate adds are no-ops (first owner wins); ≤ 128 per conversation, oldest pruned atomically; insertion-ordered lists; prioritized lookup. |
| `IChannelDispatchStore` | Scope-confined create/compare-and-set per (conversation, message) (not found when absent); inbox and channel immutable. |

`ChannelWriteConflict` and `ChannelConflict` always carry the current revision (`long`).

`tests/Channels/TicketFlow.Channels.StoreContracts` verifies these against `InMemoryChannelStore` and PostgreSQL (two independent
data sources, including concurrent creates and concurrent idempotent reference adds). `PostgresChannelStore` uses `tf_key_v1`
keys with raw-column comparison, `format_version` columns, a partial unique address index, advisory locks for endpoint capacity and
reference pruning, and parameterized SQL; undecodable rows throw. The in-memory store is a bounded development example.

## Not yet implemented

- IMAP mailbox channel (`TicketFlow.Channels.Mailbox`, ticket E6 T07): MailKit polling, MIME parsing, UID checkpoints.
- Outbound attachments for EnvelopeFlow channels (needs an application attachment content seam).
- Typing indicators, Teams inbound (EnvelopeFlow is outbound-only for Teams) and email `References` from EnvelopeFlow inbound.
- A hosted worker for the outbox dispatcher and EnvelopeFlow receipt processing (E11).
