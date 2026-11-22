# Integration event stream

TicketFlow publishes a public, versioned stream of integration events — CloudEvents 1.0 compatible — so applications, analytics and
message brokers can react to support activity without polling feature APIs. Events are written once per source change into a durable,
gapless per-workspace log and fanned out at least once to Access-checked subscriptions.

| Package | Contents |
| --- | --- |
| `TicketFlow.Events.Abstractions` | `IntegrationEvent`, the `IntegrationEventData` union of typed v1 payloads, `IntegrationEventTypes`, `EventPrivacy`, `IntegrationEventCodec` (persisted shape and CloudEvents rendering), subscriptions, `EventPermissions`, `IEventSink`, `IEventSubscriber`, `IEventPublisher`, `IEventSubscriptionService`, `IEventLog`, `IEventSubscriptionStore`. |
| `TicketFlow.Events` | `EventProjector` (outbox consumer `events`), `EventContactFollower`, `EventSurveyFollower`, `EventRoutingObserver`, `EventSubscriptionService`, `EventDeliveryWorker`, `EventContentHydrator`, `InProcessEventSink`, `EventRetentionWorker`, DI (`AddEvents`, `AddEventSubscriber<T>`, `AddInMemoryEventStores`). |
| `TicketFlow.Events.Stores.Postgres` | `PostgresEventStore`, `PostgresEventMigrations`, `AddPostgresEventStores`. |
| `TicketFlow.Events.Webhooks` | `WebhookEventSink` (`webhook`), `IEventWebhookKeyResolver`, `InMemoryEventWebhookKeyRing`, `AddEventWebhooks`. |
| `TicketFlow.Integrations.PostHog` | `PostHogEventSink` (`posthog`), `AddPostHogEvents`. |
| `TicketFlow.Integrations.WalFlow.Sinks` | `WalFlowEventSink` (`walflow`) over WalFlow `ISink` packages, `CloudEventSinkPayloadTransformer`, `AddWalFlowEventSinks`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddEvents(options => options.SlaWorkerClientId = new ApiClientId("sla-worker"))
    .AddPostgresEventStores(dataSource)
    .AddEventWebhooks(options => options.AllowedHosts.Add("hooks.example.com"))
    .AddEventSubscriber<MyAuditSubscriber>();                          // optional in-process subscriber
services.AddSingleton<IEventWebhookKeyResolver, MyWebhookKeys>();      // keys from your secret store
await new PostgresEventStore(dataSource).InitializeAsync();           // explicit schema creation
```

## Envelope

Every event renders as a CloudEvents structured-mode JSON object:

| Attribute | Value |
| --- | --- |
| `specversion` | `1.0` |
| `id` | Deterministic UUID (version 8) derived from the source (timeline event, outbox change, contact change, survey invitation, routing event) and the type, so redelivery and replay never produce a new id. Consumers deduplicate on it. |
| `source` | `EventsOptions.Source` (default `/ticketflow`). |
| `type` | `ticketflow.<area>.<name>.v<version>`, for example `ticketflow.conversation.created.v1`. |
| `time` | When the source change happened (UTC, microseconds). |
| `subject` | `conversations/<id>` or `contacts/<id>` (identifier URI-escaped). |
| `datacontenttype`, `dataschema` | `application/json`, `urn:ticketflow:schema:<type>`. |
| Extensions | `sequence` (the gapless workspace sequence, decimal string), `tenantid`, `projectid`, `environmentid`, `appid`, `workspaceid`, `privacy` (`ids-only`, `metadata`, `content`), `actorkind`/`actorid` when known. |
| `data` | The typed payload at the subscription's privacy level. |

Existing versions only gain documented optional fields; breaking changes introduce a new `.vN` type. Persisted payloads use
`IntegrationEventCodec`: a compact `{"v":1,…}` object keyed by the stored event type (the explicit discriminator); decoding re-validates
values and rejects unknown versions, types and properties. Unions are never serialized. Enum-like values are lowercase tokens owned by
the event contract (`open|pending|snoozed|resolved|closed`, `none|low|medium|high|urgent`, `plain-text|markdown|html`,
`first-response|next-response|resolution`, …), durations are integer microseconds.

## Event types (v1)

| Type | Source | Identifiers (`IdsOnly`) | Metadata adds |
| --- | --- | --- | --- |
| `conversation.created` | outbox `Started` | conversation, inbox, contact, assignee, team (initial values) | status, priority, labels, channel |
| `conversation.status-changed` | `StatusChanged` | conversation, inbox | previous, current, snoozedUntil |
| `conversation.assigned` | `AssignmentChanged` | conversation, inbox, previous, current | — |
| `conversation.team-changed` | `TeamChanged` | conversation, inbox, previous, current | — |
| `conversation.priority-changed` | `PriorityChanged` | conversation, inbox | previous, current |
| `conversation.labels-changed` | `LabelsChanged` | conversation, inbox | added, removed |
| `conversation.message-received` | incoming `MessageEvent` | conversation, inbox, message, contact | timelineSequence, contentType, attachmentCount |
| `conversation.message-sent` | outgoing `MessageEvent` | conversation, inbox, message, sender | timelineSequence, contentType, attachmentCount |
| `conversation.note-added` | `NoteEvent` | conversation, inbox, note, author | — (note bodies are never exported) |
| `conversation.resolved` / `conversation.reopened` | `StatusChanged` crossing resolved/closed | conversation, inbox | status / previous, current |
| `conversation.deleted` | outbox `Deleted` | conversation, inbox | — |
| `contact.created` / `updated` / `deleted` | contact change log | contact | — |
| `contact.merged` / `contact.unmerged` | contact change log | contact, target, merge | — |
| `contact.identity-linked` | `IEventPublisher` (see below) | contact | identityKind, verified |
| `sla.applied` / `warning` / `breached` / `met` | `sla.*` extension events **appended by the SLA worker client** | conversation, inbox, policy | key, metric, cycle, targets, elapsed, breached, at, dueAt |
| `routing.assigned` / `overflowed` / `queue-wait-exceeded` | `IRoutingEventObserver` | conversation, inbox, agent / target team or inbox | revision |
| `survey.responded` | survey responses | conversation, contact, survey, invitation, inbox, team, agent | kind, score, respondedAt |
| `channel.delivery-status` | outbox `DeliveryUpdated` | conversation, inbox, message | state |

`sla.*` timeline events appended by any other actor (forgeries) are ignored, exactly as in reporting. Attribute, participant, contact
re-pointing and system timeline events are not exported.

## Privacy levels

Subscriptions choose a level; the default is `IdsOnly`.

- **`IdsOnly`**: identifiers only. Consumers fetch details through Access-checked APIs.
- **`Metadata`**: identifiers plus non-identifying state (statuses, priorities, labels, channel kinds, counts, SLA metrics, scores).
- **`Content`**: metadata plus `data.content`, hydrated **at delivery time** as the subscription principal, once per batch: message bodies
  and attachment metadata through `IConversationService.ReadTimelineAsync` (one timeline range read per conversation), contact display
  name and identities through `IContactService.GetAsync` (once per distinct contact; on contact events as the content itself, on conversation
  events as `content.contact` for the conversation's contact), and survey comments through `ISurveyService.GetResponseAsync`
  (`survey:read` and the `survey.comment` field policy). The owning feature applies its Access decision and masking, so masked values (for
  example `conversation.channel` external ids, `contact.email` identities or survey comments) are removed and listed in
  `content.maskedFields`. Notes are never hydrated. Requires `events:content` (sensitive) both for the manager configuring the subscription
  and for the subscription principal at delivery.

The event log never stores bodies, comments, addresses or attribute values — only identifiers and metadata — so retention of the log does
not retain customer content.

## Access

| Permission | Sensitive | Use |
| --- | --- | --- |
| `events:read` | no | Read subscriptions; the subscription principal must hold it to receive any event. |
| `events:manage` | yes | Create, update, delete and replay subscriptions; read dead letters. |
| `events:content` | yes | Configure and receive `Content` subscriptions (audited once per delivery batch). |

Registered by `AddEvents()`; only Owner holds them until custom roles grant them. Service calls authorize on the workspace **before**
revealing whether a subscription exists or which revision it has. Every subscription names an application **API client principal**.
Creating or updating a subscription requires that the actor could confer every live allow grant of that principal
(`IAccessAdministrator.CheckCanConferAsync`): `events:manage` alone cannot route data through a principal that can read more than the
actor (confused deputy). The delivery engine loads one `IAccessDecisionContext` per batch and decides each event for that principal:
`conversation:read` on the event's conversation resource (team, inbox and assignee when projected; labels unknown, so label conditions
fail closed), else `contact:read` on the contact, else only `events:read`. Undecidable or denied events are skipped (the cursor moves
past them); a principal without `events:read` (or `events:content` for content) blocks the subscription with backoff until fixed.

## Subscriptions and delivery

`EventSubscription(id, name, sink, filter, principal, privacy, enabled, batchSize)`; at most 64 per workspace. The filter has 1-32 type
patterns (`*`, `ticketflow.conversation.*`, or an exact type) and optional inbox and team lists (events without a listed inbox/team,
such as contact events, are excluded). New subscriptions start at the head (`EventStartAtLatest`), at the oldest retained event
(`EventStartAtEarliest`) or after a sequence.

`EventDeliveryWorker.RunOnceAsync` claims due subscriptions (leased and fenced), reads up to `MaximumScan` events after the cursor,
keeps matching and authorized events up to the batch size, renders and hydrates them, and makes one sink call:

- **At least once**: the cursor advances only after the sink accepted the batch. `EventDelivery.DeliveryId` covers scope, subscription,
  subscription revision, replay generation and the batch's sequence range: stable across retries, new after a replay or an update.
- **Fairness**: claims are ordered by claim time and completion resets it to now, so busy subscriptions rotate even when more are due
  than `DeliveryClaimLimit`; a caught-up subscription waits `IdlePollInterval` (default 2 s) before it is scanned again.
- **Ordering**: a subscription receives events strictly in workspace sequence order, so each conversation's events arrive in timeline
  order. A failing batch blocks later events of that subscription until it succeeds or is dead-lettered.
- **Retries**: exponential backoff from `InitialRetryDelay` to `MaximumRetryDelay` (sinks may request a later `RetryAfter`).
- **Dead letters**: after `MaximumAttempts` (or a permanent sink failure) the batch's events are recorded as `EventDeadLetter`s
  (sequence, event id, type, attempts, failure code — never payloads) and the cursor advances. An event whose content cannot be hydrated or
  rendered fails alone (events before it are delivered first), so a poison event is dead-lettered by itself.
- **Oversized batches**: a sink returns `EventSinkSplit` (the webhook sink for bodies over 1 MiB or a 413, PostHog for a 413) and the
  engine retries immediately with the first half; a single event that is still too large is dead-lettered (`payload-too-large`).
- **Suspension**: configuration failures — sink kind not registered or target not allowed for the scope, principal without `events:read`
  (for example revoked) or without `events:content` — suspend the subscription after `MaximumAttempts` (`EventDeliveryState.Suspended`,
  `LastError` names the cause). Updating or replaying it resumes delivery. Store or Access outages keep retrying with backoff.
- **Retention gaps**: events pruned before a subscription delivered them are recorded as `LastError = events-pruned-<from>-<to>`.
- **Replay**: `IEventSubscriptionService.ReplayAsync(actor, id, afterSequence)` rewinds the cursor, resets attempts, clears dead letters
  after that position and fences out an in-flight lease.

Sink exception details are never recorded (they may contain customer data); only short failure codes are stored.

### Sinks

| Kind | Package | Target |
| --- | --- | --- |
| `in-process` | `TicketFlow.Events` | A registered `IEventSubscriber` name. Throwing fails the delivery. |
| `webhook` | `TicketFlow.Events.Webhooks` | An https URL (SSRF guarded). One POST per delivery (`application/cloudevents+json` or `application/cloudevents-batch+json`), signed with the E6 `ApiWebhookSignature` v1 scheme; keys come from `IEventWebhookKeyResolver`, and during rotation the previous key signs `TicketFlow-Webhook-Signature-Previous`. 408/425/429/5xx retry, other statuses are permanent. |
| `posthog` | `TicketFlow.Integrations.PostHog` | A configured project name. PostHog `/batch/` capture with `uuid` = event id, `distinct_id` = contact id (or an unmasked `external-user` identity from `Content` subscriptions), `$groups` = workspace. |
| `walflow` | `TicketFlow.Integrations.WalFlow.Sinks` | A registered WalFlow `SinkRegistration` name (Kafka, RabbitMQ, SQS, SNS, Azure Service Bus, Google Pub/Sub, Kinesis, Redis Streams packages). Debezium-compatible `op = "c"` records keyed by conversation/contact id, `after` = the CloudEvent; `CloudEventSinkPayloadTransformer` publishes the CloudEvent as the body. Delivery is only as durable as the sink declares (`RequireDurableSinks`). |

Custom sinks implement `IEventSink` (`Kind`, `MaximumBatchSize`, `IsValidTarget(scope, target)`, `DeliverAsync` returning
`EventSinkResult = EventSinkDelivered | EventSinkFailed(code, permanent, retryAfter) | EventSinkSplit`). Operator-configured named sinks
(PostHog projects, WalFlow sinks) require at least one `EventScopeRule` (tenant plus optional project/environment/app/workspace) and refuse
other scopes both when a subscription is saved and at delivery.

PostHog `distinct_id`s are namespaced — `contact:<id>`, `external:<issuer>:<value>` (`ContactExternalId` strategy, from unmasked
`external-user` identities in `Content` subscriptions) and `workspace:<id>` for events without a contact. Conversation events (messages,
SLA, routing, delivery status) use the conversation's contact, so all of a contact's events share one id within a project; use one privacy
level per project.

## Projection sources

- **`EventProjector`** is an `IConversationOutboxHandler` (consumer `events`; registered with the dispatcher like other consumers, so it
  receives changes committed after registration). It reads each change's timeline events through the trusted `IConversationStore` and
  appends their events in timeline order; `conversation.created` reconstructs the initial assignee, team, priority, status and labels
  from later timeline changes (bounded by `MaximumReplayEvents`). Changes projected after the conversation was deleted yield only
  delivery and delete events.
- **`EventContactFollower.CatchUpAsync(scope, limit)`** follows `IContactStore.ReadChangesAsync` with a compare-and-set checkpoint
  (`contacts`) in the event log store.
- **`EventSurveyFollower.CatchUpAsync(scope)`** reads survey responses in time windows (`SurveySlice`, re-reading `SurveyOverlap`
  behind its checkpoint `surveys`) and starts at the current time on its first run.
- **`EventRoutingObserver`** maps routing assignments, overflows and queue-wait breaches. Routing observers are best-effort in-process
  notifications (an event is lost if the process fails before the append); the outbox still yields the durable
  `conversation.assigned`/`team-changed` events. E8 raises no "queued" event, so `routing.queued` is not part of v1.
- **`IEventPublisher`** appends drafts from host features (trusted; idempotent by event id) — for example identity linking publishing
  `ContactIdentityLinkedData`.

## Store contracts

| Contract | Guarantees |
| --- | --- |
| `IEventLog` | Scope-confined; atomic appends idempotent per (scope, event id) with gapless sequences in draft order, concurrent appenders serialize per workspace; ascending reads; store-wide retention prune (heads never move back, sequences are never reused); compare-and-set follower checkpoints. |
| `IEventSubscriptionStore` | Scope-confined, revision-fenced subscriptions (≤ 64 per workspace, checked atomically); store-wide leased claims of due subscriptions with pending events (fence per claim, no shared live lease); fenced completion, failure and dead-lettering (cursor never moves back); replay; dead-letter listing and retention. |

`tests/Events/TicketFlow.Events.StoreContracts` verifies both against the in-memory store and PostgreSQL (two independent data sources),
including concurrent appenders and claimers. `PostgresEventStore` locks the scope head row for appends (unique `(scope_key, event_id)`
index), claims with `FOR UPDATE SKIP LOCKED` over a partial `(available_at) WHERE enabled AND NOT suspended` index, uses `tf_key_v1` keys,
explicit `format_version` columns and parameterized SQL. Migration 2 adds the replay generation and suspension columns, sets
`fillfactor = 50` on head rows (HOT updates under append contention) and replaces the `recorded_at` B-tree with a BRIN index; retention
therefore prunes rows before the cutoff in no particular order (delivery records any gap it meets). Lease times come from the store's `TimeProvider`. The in-memory store is a bounded,
process-local development example.

## Hosting

No hosted service is included. Hosts (E11 worker hosting) run, in their loops:

- `ConversationOutboxDispatcher.RunOnceAsync` (drives `EventProjector`),
- `EventDeliveryWorker.RunOnceAsync`,
- `EventContactFollower.CatchUpAsync(scope, limit)` and `EventSurveyFollower.CatchUpAsync(scope)` per workspace,
- `EventRetentionWorker.RunOnceAsync` (`Retention` and `DeadLetterRetention`, default 30 days).

## Not yet implemented

- Appending several outbox entries per transaction: the E5 dispatcher hands handlers one entry at a time, so the projector appends once
  per change (the contact and survey followers append in batches). A batch handler contract in Conversations would remove that
  per-change head-row round trip.
- A contact get-many API: hydration deduplicates contacts per batch but reads each through `IContactService.GetAsync`.

- A built-in source for `contact.identity-linked` (the contact change log has no linking kind yet; the linking feature or host publishes
  it through `IEventPublisher`).
- A Segment-compatible sink (same shape as the PostHog sink).
- Per-conversation parallel delivery within one subscription (delivery is totally ordered per subscription).
- A pull API (HTTP) over the event log for consumers without a push sink.
