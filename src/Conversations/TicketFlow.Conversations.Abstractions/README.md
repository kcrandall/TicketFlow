# TicketFlow.Conversations.Abstractions

Contracts for TicketFlow conversations and their append-only timelines.

- `Conversation`: inbox, contact, `ConversationStatus` union (`Open | Pending | Snoozed(until) | Resolved | Closed`), priority,
  assignee, team, labels, typed custom attributes (`ConversationAttributeValue` union, definitions with a `Sensitive` flag),
  participants, an opaque `ChannelThreadRef` (correlation only), activity and SLA markers (`WaitingSince`, `FirstReplyAt`), the
  timeline head and contact-merge redirects. `StoredConversation` adds scope and revision.
- Timeline: `TimelineEvent` (gapless per-conversation sequence, id, time, actor) carrying the `ConversationEvent` union —
  `MessageEvent` (direction, sender, ≤ 64 KiB body, content type, attachment refs, external id, delivery state), `NoteEvent`,
  `AssignmentChanged`, `TeamChanged`, `StatusChanged`, `PriorityChanged`, `LabelsChanged`, `AttributeChanged`,
  `ParticipantChanged`, `ContactChanged`, `SystemEvent` and the `ExtensionEvent(kind, payloadVersion, payload)` extension point.
- `ConversationJsonCodec`: versioned persisted JSON with explicit discriminators; unions are never serialized directly.
- `ConversationPermissions`, `ConversationFieldPaths` (`conversation.channel`, `conversation.attribute.<key>`) and
  `ConversationAccess.Resource` (scope, id, team, inbox, assignee, labels, attribute condition values).
- `IConversationService` (union `ConversationCommandResult = Applied | NotFound | Conflict | Refused`), masked `ConversationView`
  and `TimelineEventView`, `IConversationAttributeDefinitionService`.
- Store contracts: `IConversationStore` (atomic revision-fenced `ConversationWrite` with timeline events, message rows and outbox
  change; inbound idempotency; fenced delivery updates; filtered keyset lists), `IConversationOutbox` (multi-consumer leased
  claims with fences, ack, retry, dead-letter, per-conversation ordering) and `IConversationAttributeDefinitionStore`.
