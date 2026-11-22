# TicketFlow.Routing

Routing and assignment for TicketFlow.

- `RoutingEngine` (`IRoutingEngine`): reconciles a conversation into the routing queue and load projection, then assigns open,
  unassigned conversations by the target inbox's `RoutingPolicy` — round robin, least busy (capacity-aware), skills-based, sticky
  owner with fallback, or manual. Eligibility: effectively online, in the conversation/policy team, required skills, capacity on the
  channel kind and Access `conversation:reply` (one planned filter per agent, cached briefly). Assignment is an atomic store
  reservation (cursor + capacity) followed by `AssignAsync(expectedRevision)` as an API client, so racing workers assign exactly once.
- `RoutingOutboxHandler`: the `routing` consumer of the conversation outbox.
- `RoutingMaintenanceWorker`: retries due queue entries, applies overflow and maximum-wait rules, expires agent presence, returns
  offline agents' conversations to the queue per policy and prunes collision presence.
- Services: `RoutingPolicyService` (`routing:manage`), `AgentAvailabilityService` (`agent-status:update` / `agent-status:manage`),
  `ConversationCollisionService` (viewing/composing presence and soft reply locks), `RoutingQueueService` (Access-filtered queues).

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddInMemoryAccessStores()      // development only
    .AddWorkspaces().AddInMemoryWorkspaceStores()
    .AddContacts().AddInMemoryContactStores()
    .AddConversations().AddInMemoryConversationStores()
    .AddRouting(options => options.EngineClient = new ApiClientId("routing-engine"))
    .AddInMemoryRoutingStores();                       // development only
```

The engine client must hold `conversation:assign` in each workspace. Registrations use TryAdd. The in-memory store is a bounded,
process-local development example without durability; use `TicketFlow.Routing.Stores.Postgres` in production.
