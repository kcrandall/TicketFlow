# TicketFlow.Conversations

The TicketFlow conversation service.

- `ConversationService` (`IConversationService`): start, incoming message (idempotent per inbox by external id; reopens pending,
  snoozed and resolved conversations), reply (Pending outgoing message plus an outbox change for channel dispatch), note,
  assign/unassign, team, status/snooze, priority, labels, attributes, participants, delivery status (idempotent and monotonic),
  extension events and delete. Each returns `ConversationCommandResult = Applied | NotFound | Conflict | Refused`. Reads: get,
  timeline (notes only with `note:read`) and filtered keyset lists, all masked server-side.
- Every read and mutation authorizes through `IAccessEvaluator`; lists put `IAccessQueryPlanner` filters into the store query.
  Existence and revision fences are checked before authorization decisions; team/label/attribute changes also authorize the
  resulting conversation. Commands without an expected revision retry concurrent-write conflicts.
- `ConversationOutboxDispatcher` runs `IConversationOutboxHandler`s (claim, handle, acknowledge, exponential retry, dead-letter).
- `ConversationContactMergeFollower` re-points conversations on contact merges and restores them on unmerge, idempotently; it is
  registered as an `IContactChangeObserver` and offers `CatchUpAsync` over the durable contact change outbox.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddInMemoryAccessStores()            // development only
    .AddWorkspaces().AddInMemoryWorkspaceStores()
    .AddContacts().AddInMemoryContactStores()
    .AddConversations(options => options.MaximumWriteAttempts = 16)
    .AddInMemoryConversationStores();                       // development only
```

Registrations use TryAdd and preserve application services; the services are scoped. The in-memory stores are bounded,
process-local development examples with no durability; use `TicketFlow.Conversations.Stores.Postgres` or an application-owned
implementation in production.
