# Conversations and timeline

Conversations are revisioned snapshots projected from an append-only timeline. They are the central domain later features build
on: channels (E6) dispatch outgoing messages and report delivery, live chat (E7) fans out changes, routing (E8) assigns, SLAs (E9)
read the waiting markers, automation (E10) and assist (E13) append extension events, reporting (E14) consumes the outbox. Three
packages live under `src/Conversations`:

| Package | Contents |
| --- | --- |
| `TicketFlow.Conversations.Abstractions` | `Conversation`, the `ConversationStatus` union, typed attributes and definitions, `TimelineEvent` with the `ConversationEvent` union, `ConversationJsonCodec`, permissions, field paths, the Access resource, service contracts and union results, store and outbox contracts. |
| `TicketFlow.Conversations` | `ConversationService`, `ConversationAttributeDefinitionService`, `ConversationContactMergeFollower`, `ConversationOutboxDispatcher`, DI (`AddConversations`, `AddInMemoryConversationStores`) and development in-memory stores. |
| `TicketFlow.Conversations.Stores.Postgres` | `PostgresConversationStore` (store, outbox and attribute definitions), `PostgresConversationMigrations`, `AddPostgresConversationStores`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)
    .AddConversations(options => options.MaximumWriteAttempts = 16)
    .AddPostgresConversationStores(dataSource);
await new PostgresConversationStore(dataSource).InitializeAsync();   // explicit schema creation
```

`ConversationService` depends on `IInboxStore`, `ITeamStore` (E3) and `IContactStore` (E4) to validate references.

## Model

- **Conversation**: id, inbox, contact, status, priority (`None|Low|Medium|High|Urgent`), assignee (`UserId?`), team (`TeamId?`),
  labels (≤ 64, ordinal order), typed custom attributes (≤ 128), participants (≤ 64 users following it), an optional
  `ChannelThreadRef(channel, externalThreadId)` (opaque correlation evidence, never authentication), `CreatedAt`,
  `LastActivityAt` (messages, notes and status changes; monotonic), SLA markers and the timeline head `LastSequence`.
  `StoredConversation` adds the full `TicketScope` and revision (first is 1). Inbox, creation time and channel thread are immutable.
- **Status** is the union `OpenStatus | PendingStatus | SnoozedStatus(Until) | ResolvedStatus | ClosedStatus`, persisted as a
  numeric kind plus the snooze instant. Closed is terminal for messages, notes and status changes (metadata stays editable).
- **SLA markers**: `WaitingSince` is the oldest incoming message not yet answered (set by an incoming message when empty; cleared
  by an outgoing reply, resolve or close), `FirstReplyAt` the first outgoing reply, plus `LastIncomingAt` and `LastOutgoingAt`.
  Policies, calendars and breach evaluation belong to E9.
- **Attributes**: workspace-scoped `ConversationAttributeDefinition(key, type, sensitive, displayName)` (text, number, boolean,
  date, list; type immutable; ≤ 256 per workspace). Values are the `ConversationAttributeValue` union and must match their
  definition.
- **Contact redirects**: `ContactRedirect(merge, previous)` entries (≤ 16) record contact-merge re-pointing so unmerge can reverse
  exactly what the merge did.

Timestamps are UTC with microsecond precision (PostgreSQL `timestamptz`). Identifiers follow `TicketScope.RequireIdentifier`.

## Timeline

`TimelineEvent(conversation, sequence, id, occurredAt, actor, event)` is immutable; sequences are gapless per conversation starting
at 1. `actor` is the acting principal as an `AccessActor` (kind and id). The event is the union:

| Event | Notes |
| --- | --- |
| `MessageEvent` | Direction `Incoming` (sender `ContactParty`) or `Outgoing` (sender `PrincipalParty`); body ≤ 64 KiB UTF-8, well-formed, no control characters except tab/CR/LF (may be empty only with attachments); content type `PlainText|Markdown|Html`; ≤ 32 `AttachmentRef`s (storage reference, file name, media type, size — never bytes); external (provider) message id ≤ 1024; `MessageDelivery` (`Pending → Sent → Delivered → Read`, `Failed(code)` from Pending/Sent, `Received` for incoming). |
| `NoteEvent` | Private note (same body and attachment bounds). Never delivered; visible only with `note:read`. |
| `AssignmentChanged`, `TeamChanged`, `StatusChanged`, `PriorityChanged`, `LabelsChanged`, `AttributeChanged`, `ParticipantChanged` | Previous and current values. |
| `ContactChanged` | Contact re-pointed by a merge (with merge id) or restored by an unmerge. |
| `SystemEvent` | Dotted code plus operator detail (no personal data). |
| `ExtensionEvent(kind, payloadVersion, payload)` | Extension point for later features: a dotted feature-owned kind (`sla.breached`, `automation.rule-applied`, `assist.suggested`), a payload schema version (1-1000) and a JSON object ≤ 16 KiB that the owning feature encodes with its own explicit discriminators. Stored verbatim (compact JSON). |

The timeline entry is immutable, but a message's delivery state and late-arriving provider id live in a mutable message row; reads
return messages with their current delivery state. Delivery updates neither append events nor change the conversation revision.

**Persistence** uses `ConversationJsonCodec`: every envelope is `{"v":1,...}` with explicit discriminators (`"k"` for events —
`message`, `note`, `assignment`, `team`, `status`, `priority`, `labels`, `attribute`, `participant`, `contact`, `system`,
`extension`; `"s"` for statuses, `"t"` for attribute values, `"p"` for parties). Decoding re-validates every value and rejects
unknown versions, discriminators and properties. Unions are never serialized directly. PostgreSQL rows also carry numeric
`kind`/`status`/`direction`/`delivery_state` discriminators and a `format_version`.

## Commands

`IConversationService` returns `ConversationCommandResult = ConversationApplied(conversation, revision, events, replayed) |
ConversationNotFound | ConversationConflict(currentRevision) | ConversationRefused(reason, errors)` (reasons `Forbidden`, `Invalid`,
`Closed`, `Rejected`). Error messages never contain bodies, addresses or values.

| Command | Permission | Behavior |
| --- | --- | --- |
| `StartAsync` | `conversation:create` | Authorized on the would-be conversation before reference checks; inbox must exist and be enabled, contact must exist and not be merged, team must exist, attributes must match definitions. Optional initial incoming message (idempotent by external id). |
| `AppendIncomingAsync` | `conversation:ingest` | Idempotent per (scope, inbox, external id): a duplicate returns `Replayed` with the existing event, also under concurrency (unique index). Reopens pending, snoozed and resolved conversations (`StatusChanged`); refused when closed. |
| `ReplyAsync` | `conversation:reply` | Outgoing message in `Pending` plus an outbox change listing it for channel dispatch (`ConversationChange.OutgoingMessages`). Sets `FirstReplyAt`, clears `WaitingSince`. |
| `AddNoteAsync` | `note:write` | LightAgent can note but not reply. |
| `AssignAsync` / `SetTeamAsync` | `conversation:assign` | `null` unassigns / clears; the team must exist. |
| `ChangeStatusAsync` / `SnoozeAsync` | `conversation:resolve` | Snooze must end in the future. Same status is a no-op. |
| `SetPriorityAsync`, `UpdateLabelsAsync`, `UpdateAttributesAsync`, `UpdateParticipantsAsync`, `AppendExtensionAsync` | `conversation:manage` | Attributes: `null` removes; ≤ 16 changes per call, one `AttributeChanged` each. |
| `UpdateDeliveryStatusAsync` | `conversation:ingest` | Correlated by conversation + message id or by inbox + external id; outgoing messages only. Monotonic and idempotent: stale or repeated reports return `Replayed`. Can record the provider message id once (unique per inbox). |
| `DeleteAsync` | `conversation:delete` (sensitive) | Fenced on the expected revision; removes conversation, timeline and message rows; emits a `Deleted` change. |

Pipeline for every mutation: read → existence (`NotFound`) → revision fence (`Conflict`) → authorize on the current conversation
(`Forbidden`) → decide → for team, label and attribute changes also authorize the resulting conversation (so a team-scoped agent
cannot move a conversation out of reach) → one atomic store write of snapshot, events, message rows and outbox change. Existence
and fences are checked before authorization, like the contact service and access administrator. Commands accept an optional
`expectedRevision`; without it they apply to the current revision and re-run the pipeline when a concurrent writer wins
(`ConversationsOptions.MaximumWriteAttempts`, default 16), so concurrent replies all land with gapless sequences.

Automation and system principals cannot hold grants (Access denies them). Channel ingestion, SLA and automation workers act as
grant-holding API client principals (for example a custom role with `conversation:create` and `conversation:ingest`), or —
for trusted internal followers such as the contact-merge follower — write through the store directly.

## Reads and masking

- `GetAsync` (`conversation:read`) returns a `ConversationView`; `ReadTimelineAsync` (`conversation:read`, 1-200 events per page,
  `NextAfterSequence` cursor) omits notes unless the principal holds `note:read` — the store skips them inside the query and the
  service filters again; `FindByThreadAsync` (`conversation:ingest`) returns the newest non-closed conversation for an inbox and
  channel thread.
- **Masking** follows Access field policies for `conversation:read`: `conversation.channel` covers the channel thread id and
  external message ids (correlation evidence that may contain addresses); `conversation.attribute.<key>` covers attribute values
  in views and in `AttributeChanged` events. A sensitive attribute with no applicable policy, or an attribute without a definition,
  is masked and not revealable (fail closed); a denied read decision masks everything. Masked values are removed, not obfuscated,
  and `MaskedFields` lists them. `ConversationFieldPaths.SensitivePolicy` builds the recommended policy. Conversations reference
  contacts by id only, so no contact PII is copied into conversations; contact details are read (and masked) through E4.
- **Lists** (`ListAsync`, `conversation:read`): criteria inbox, status, assignee or unassigned, team, label, contact; ordered by
  `LastActivityAt` descending then id descending (ordinal); keyset-paged (1-100) with an opaque cursor bound to the query. The
  planned `AccessFilter` is applied inside the store query over `ConversationAccess.Resource` (scope, id, team, inbox, assignee,
  labels and attribute condition values — text verbatim, numbers invariant, booleans `true`/`false`, dates `yyyy-MM-dd`; list
  attributes are unknown). Each returned row also gets a point decision for its masking.

## Outbox

Every write appends a `ConversationChange` (`Started`, `EventsAppended`, `DeliveryUpdated`, `Deleted`; identifiers, revision and
per-event summaries — never bodies) in the same transaction. `IConversationOutbox` is a durable, store-wide, multi-consumer log:

- `RegisterConsumerAsync(name)` (≤ 32 consumers): a consumer receives every entry committed after its registration.
- `ClaimAsync(consumer, limit ≤ 100, lease ≤ 1 h)` leases available deliveries in entry order. A delivery is claimable only when
  the consumer has no earlier pending delivery of the same conversation, so each consumer sees a conversation's changes in order
  (entry sequences are taken under the conversation row lock). Every claim increments the delivery's fence.
- `AcknowledgeAsync` and `ReleaseAsync(retryAt)` succeed only with the current fence while the lease is held; `DeadLetterAsync`
  parks a delivery (unblocking later entries); `PruneAsync(olderThan)` deletes entries no consumer still owes.
- `ConversationOutboxDispatcher` runs registered `IConversationOutboxHandler`s: claim, handle, acknowledge, exponential backoff
  (`InitialRetryDelay` doubling to `MaximumRetryDelay`), dead-letter after `MaximumAttempts`. Handlers must be idempotent. No
  hosted service is included; hosts (E11) call `RunOnceAsync` in their loop.

## Contact merge hook

`ConversationContactMergeFollower` is registered as an `IContactChangeObserver`, so in-process contact merges re-point
conversations immediately; `CatchUpAsync(scope, afterSequence)` replays the durable contact change log (the caller keeps the
checkpoint). On `Merged` every conversation of the source moves to the target with a `ContactChanged` event and a redirect; on
`Unmerged` exactly the conversations re-pointed by that merge return to their previous contact (chains unwind newest first). Both
are idempotent: redirects are keyed by merge id and merges whose record was removed by an unmerge are skipped on replay. Writes use
the system actor `contact-merge`.

## Store contracts

| Contract | Guarantees |
| --- | --- |
| `IConversationStore` | Scope-confined; atomic `ConversationWrite` (≤ 16 events) fenced on revision and timeline head; creates at revision 1; gapless per-conversation sequences; message rows with external id unique per (scope, inbox) (`ConversationDuplicateMessage`); message id unique per conversation; fenced delivery updates; filtered keyset lists; thread lookup; delete. |
| `IConversationOutbox` | As above: per-consumer deliveries, fenced leased claims, per-conversation ordering, ack/release/dead-letter, prune. |
| `IConversationAttributeDefinitionStore` | Compare-and-set; type immutable; ≤ 256 definitions per workspace, checked atomically. |

`tests/Conversations/TicketFlow.Conversations.StoreContracts` verifies these against the in-memory stores and PostgreSQL (two
independent data sources), including concurrent appenders (gapless sequences), concurrent duplicate inbound messages (one winner),
concurrent outbox claimers (no shared live lease) and list/filter parity with `AccessFilterEvaluator`. The in-memory stores are
bounded, process-local development examples with no durability.

`PostgresConversationStore` uses `tf_key_v1` keys (conversation, message, external id, thread), explicit `format_version` columns,
parameterized SQL and `"C"`-collated identifiers. List indexes are `(scope_key, [inbox_id|status|assignee_id|team_id|contact_id],
last_activity_at DESC, conversation_id DESC)` plus GIN on labels; the translated Access filter runs in the same statement over
`tenant_id … workspace_id`, `team_id`, `inbox_id`, `assignee_id`, `labels` and the `access_attributes` jsonb. Outbox claims use
`FOR UPDATE SKIP LOCKED`; lease times come from the store's `TimeProvider`, so clients need synchronized clocks.

## Not yet implemented

- Revealing masked conversation fields (views mark `Revealable`; a reveal API like contacts' is future work).
- Automatic wake-up of expired snoozes (an E9/E10 worker can list `status = Snoozed` and change status; an incoming message
  already reopens).
- A hosted outbox worker and durable checkpoint storage for the merge follower's catch-up position (E11).
