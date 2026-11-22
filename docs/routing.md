# Routing and assignment

Routing decides which agent gets an open, unassigned conversation — exactly once, within capacity and only to agents Access allows
to reply. It also tracks agent availability, unassigned queues and collision presence. Three packages live under `src/Routing`:

| Package | Contents |
| --- | --- |
| `TicketFlow.Routing.Abstractions` | `RoutingPolicy` and the `RoutingStrategy` union, overflow rules, agent availability/capacity/skills, queue entries, the assignment (load) projection and reservation request, presence and reply locks, codecs, result unions, routing events, permissions, store and service contracts. |
| `TicketFlow.Routing` | `RoutingEngine`, `RoutingOutboxHandler` (consumer `routing`), `RoutingMaintenanceWorker`, `RoutingPolicyService`, `AgentAvailabilityService`, `ConversationCollisionService`, `RoutingQueueService`, DI (`AddRouting`, `AddInMemoryRoutingStores`) and the development `InMemoryRoutingStore`. |
| `TicketFlow.Routing.Stores.Postgres` | `PostgresRoutingStore` (all five store contracts), `PostgresRoutingMigrations`, `AddPostgresRoutingStores`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddRouting(options => options.EngineClient = new ApiClientId("routing-engine"))
    .AddPostgresRoutingStores(dataSource);
await new PostgresRoutingStore(dataSource).InitializeAsync();   // explicit schema creation
// Host loop (E11): ConversationOutboxDispatcher.RunOnceAsync() and RoutingMaintenanceWorker.RunOnceAsync().
```

`AddRouting` registers the `RoutingOutboxHandler` as an `IConversationOutboxHandler`, so `ConversationOutboxDispatcher.RegisterAsync`
registers the `routing` consumer. The engine writes through `IConversationService` as the API client `RoutingOptions.EngineClient`
(default `ticketflow-routing`), which must hold `conversation:assign` in every routed workspace (for example a custom role with only
that permission). Automation and system principals cannot hold grants. Without the grant the engine is refused and conversations
stay queued (`RoutingQueued(Refused)`).

## Policies

An inbox's opaque `RoutingPolicyId` (E3) names a workspace-scoped `RoutingPolicy` (≤ 256 per workspace):

| Field | Meaning |
| --- | --- |
| `Strategy` | Union `RoundRobinStrategy` (rotate in agent-id order after the inbox cursor) \| `LeastBusyStrategy` (lowest load ÷ capacity, ties in rotation order) \| `SkillsBasedStrategy(rules)` (1-32 rules `SkillRule(LabelSkillMatch \| AttributeSkillMatch, skill, minimumLevel)`; every matching rule applies, highest level per skill wins; then least busy) \| `StickyOwnerStrategy(window, fallback)` (prefer the contact's most recent assignee if assigned within the window and still eligible and under capacity; otherwise the non-sticky fallback; a `Manual` fallback offers only the sticky owner) \| `ManualStrategy` (queue only). |
| `Team` | Optional pool team used when the conversation has no team. |
| `Overflow` | ≤ 8 `OverflowRule(after, target)` in strictly ascending wait order; target union `OverflowToTeam(team)` (sets the conversation team, so the pool becomes its members) \| `OverflowToInbox(inbox)` (the entry moves to that inbox's queue and policy; the conversation keeps its immutable inbox, so those agents still need `conversation:reply` on it). Each rule applies once; the stage counter is shared across inbox moves (≤ 8 in total). |
| `MaximumWait` | After this queue wait a `QueueWaitExceeded` event is raised once (routing continues; E9/E10 may act on it). |
| `OnAgentOffline` | `Keep` or `ReturnToQueue`: what happens to an agent's open conversations of this inbox when the agent goes offline. |

No policy on the target inbox means manual. `RoutingPolicyCodec` persists policies as `{"v":1,...}` JSON with `"k"` discriminators
(`round-robin`, `least-busy`, `skills`, `sticky`, `manual`; `label`, `attribute`; `team`, `inbox`); durations are whole seconds;
decoding rejects unknown versions, kinds and properties. Unions are never serialized.

## Agent availability

`AgentRoutingSettings(agent, defaultCapacity, capacities per ChannelKind, skills)`: capacities 0-1000 concurrent open (Open or
Pending) conversations, ≤ 32 per-kind overrides, ≤ 32 skills (`AgentSkill(name token, level 1-10)`). `AgentPresence` holds
`AgentStatus` (`Online`, `Busy`, `Away`, `Offline`) and a presence expiry (`now + AgentPresenceTtl`, default 2 min) that heartbeats
extend. An agent whose presence lapsed is effectively offline immediately; the maintenance worker records it (`AgentStatusChanged`).
Heartbeats never revive a lapsed agent — it must set a status again. Only effectively Online agents receive automatic
assignments. Profiles must be configured (`ConfigureAsync`) before a status can be set; new profiles start Offline. Going offline
(explicitly or by expiry) sets `ReassignmentPending`; coming online or changing capacity makes the workspace's queued conversations
due (`NudgeAsync`).

## Assignment engine

`RoutingEngine.RouteAsync(scope, conversation)` first **reconciles** the projections from the current snapshot (idempotent; every
projection write carries the conversation revision and never moves a row back to an older one):

- deleted → queue entry and load row removed; assigned and Open/Pending → recorded as assigned (load + sticky owner), dequeued;
  assigned but snoozed/resolved/closed → load released; unassigned and Open → queued (`WaitingSince` or now); other statuses →
  dequeued. Freed load nudges the queue.

For an open unassigned conversation it then routes:

1. **Policy** of the queue entry's target inbox.
2. **Pool**: effectively online agents that are members of the conversation team (or else the policy team), hold the required
   skills and have capacity ≥ 1 for the conversation's channel kind (from its own inbox's `ChannelBinding`). These filters run inside
   the store query (`AvailableAgentQuery`; PostgreSQL reads the versioned settings document as `jsonb`) and the engine pages through
   every match — there is no identifier cutoff. When more than 1000 agents remain eligible, the reservation receives the 1000 that
   follow the inbox's round-robin cursor in rotation order (plus the sticky preference), so rotation continues past any page.
3. **Eligibility through Access**: one `IAccessQueryPlanner` plan of `conversation:reply` per agent is evaluated with
   `AccessFilterEvaluator` against `ConversationAccess.Resource(conversation)`. Principals' team snapshots are resolved in batches by
   `IRoutingTeamMembershipResolver` (default `StoreRoutingTeamMembershipResolver`: per agent for ≤ 8 agents, otherwise one read per
   team; replace it together with `ITeamMembershipResolver`). Plans live only in the scoped engine instance — one DI scope, i.e. one
   dispatcher or maintenance run — bounded by `RunEligibilityPlanLifetime` (default 15 s) and are never shared across runs, so a
   revoked grant, team change or removed agent takes effect on the next run at the latest. Plans equal point checks at plan time,
   so denies, conditions and inbox/team scopes apply.
4. **Atomic reservation** (`IRoutingAssignmentStore.ReserveAsync`): refuse when a live reservation or a newer assignment exists;
   count each candidate's load (assigned rows + live reservations on the channel); pick the sticky preference, else round robin or
   least busy (`RoutingReservationRequest.Select` is the reference); write the reservation (lease `ReservationLease`, default 30 s);
   move the inbox cursor — all in one step.
5. **Fenced assignment**: `AssignAsync(engineClient, conversation, agent, expectedRevision)`. Applied → reservation confirmed
   (assigned, sticky owner recorded), dequeued, `ConversationRouted` published. Conflict → reservation released and the whole
   pipeline retried (≤ `MaximumRouteAttempts`, default 4). Refused → released, `RoutingQueued(Refused)`.

Outcomes are the union `RoutingOutcome = RoutingAssigned(agent, revision) | RoutingQueued(reason) | RoutingSettled(reason)`;
queue reasons are `Manual`, `NoEligibleAgent`, `NoCapacity`, `Contended`, `Refused`, `Misconfigured`.

Why exactly once: the revision fence makes two `AssignAsync` calls on the same unassigned revision mutually exclusive, and the
reservation (one live reservation per conversation, revision-guarded against newer assignments) stops a second worker before it
calls the service at all. Capacity: reservations for the same agent and channel serialize on the agent's load anchor, and load
counts live reservations, so concurrent workers cannot overshoot. Manual assignments are recorded without a capacity check (they
may exceed capacity) and count toward load.

**Outbox handler**: `RoutingOutboxHandler` (`routing`) runs the engine only for changes that can affect routing: `Started` and
`Deleted` changes, and appended batches containing an incoming message or a status, assignment, team, label or attribute change
(attributes feed skill rules and Access conditions; the conversation inbox is immutable). Notes, outgoing replies, priority,
participant, contact, system and extension events and delivery updates are acknowledged without touching any routing store.
Handlers are idempotent because the engine reconciles from the snapshot.

**Maintenance worker** (`RoutingMaintenanceWorker.RunOnceAsync`, store-wide, no hosted service included):

1. records lapsed agent presence as Offline;
2. claims pending offline reassignments (leased, fenced) and, for each conversation still assigned to the offline agent whose inbox
   policy says `ReturnToQueue`, unassigns it fenced on its revision and routes it again;
3. claims due queue entries (`ClaimDueAsync`, leased, fenced; taken in `(next_attempt_at, conversation_key)` order backed by
   `tf_routing_queue_due_v2` with `SKIP LOCKED` in PostgreSQL, then processed oldest waiting first), routes them, applies at most one due
   overflow rule per run, raises `QueueWaitExceeded` once and schedules the next attempt (`RetryDelay`, default 30 s, or the next
   overflow/max-wait instant);
4. prunes expired presence and reply locks.

## Collision detection

`IConversationCollisionService` (user principals only; the conversation must exist — NotFound before authorization, as in E5):

- `ReportActivityAsync(Viewing | Composing)` / `LeaveAsync` / `ListActivityAsync` need `conversation:read`. Presence expires after
  `ConversationPresenceTtl` (default 30 s); ≤ 64 live presences per conversation.
- `AcquireReplyLockAsync` / `ReleaseReplyLockAsync` need `conversation:reply`; `GetReplyLockAsync` needs `conversation:read`. A
  live lock held by another user returns `ReplyLockHeld(current)`; re-acquiring one's own lock renews it with a new fence; release
  requires the holder and current fence. Locks are **soft**: `ConversationService.ReplyAsync` does not consult them.

Changes publish `ConversationPresenceChanged` and `ReplyLockChanged` (refreshes that only extend expiry do not).

## Events

`IRoutingEventObserver.OnRoutingEventAsync(RoutingEvent)` receives the union `AgentStatusChanged | ConversationPresenceChanged |
ReplyLockChanged | ConversationRouted | ConversationOverflowed | QueueWaitExceeded` in-process after the change is stored
(identifiers only). Delivery is best-effort: observer failures are swallowed. E7 fans these out; durable consumers use the
conversation outbox.

## Queues

`IRoutingQueueService.ListAsync(actor, RoutingQueueQuery(inbox?, team?, limit ≤ 100, cursor))` lists unassigned open conversations
oldest waiting first (`WaitingSince`, then id; keyset cursor). `inbox` filters the target queue, `team` the conversation team. The
planned `conversation:read` filter is applied inside the store query over the entry's Access resource (scope, inbox, team, no
assignee, labels, attribute condition values) — so a principal sees exactly the queued conversations it could read. The queue is
a projection maintained from the outbox, so it is eventually consistent with conversations.

## Authorization

| Operation | Permission (all workspace-level unless noted) |
| --- | --- |
| Policy get / list | `inbox:read` |
| Policy create / update / delete | `routing:manage` |
| Own status, heartbeat | `agent-status:update` |
| Other agent's status; any agent's capacity and skills | `agent-status:manage` |
| Agent profile get / list | `team:read` |
| Queue list | `conversation:read` (planned filter per conversation) |
| Presence | `conversation:read` on the conversation |
| Reply lock | `conversation:reply` on the conversation |

`routing:manage`, `agent-status:update` and `agent-status:manage` were added to the built-in Access catalog: Agent (and so
Supervisor) holds `agent-status:update`; Supervisor also holds `routing:manage` and `agent-status:manage`; Admin holds every built-in;
Owner holds everything. `AddRouting()` registers identical definitions (idempotent). Agent resources carry no team or inbox, so
team- and inbox-scoped grants do not apply to status changes (a supervisor sets status for inbox-scoped agents).

## Store contracts

| Contract | Guarantees |
| --- | --- |
| `IRoutingPolicyStore` | Scope-confined; create at revision 1; compare-and-set update/delete; ≤ 256 per workspace atomically. |
| `IAgentAvailabilityStore` | Scope-confined; ≤ 10 000 profiles per workspace; CAS settings; status changes bump the revision, heartbeats do not; lapsed presence is effectively offline; store-wide expiry sweep and leased, fenced reassignment claims. |
| `IRoutingAssignmentStore` | Atomic select-and-reserve with cursor and capacity; one live reservation per conversation; revision-guarded confirm/record/clear; load per channel; sticky owners. |
| `IRoutingQueueStore` | Revision-guarded enqueue/remove keeping queue position and overflow progress; Access-filtered keyset lists; store-wide leased, fenced due claims; nudges. |
| `IConversationPresenceStore` | Expiring presence (≤ 64 per conversation, atomic); exclusive, renewable, fenced reply locks; prune. |

`tests/Routing/TicketFlow.Routing.StoreContracts` verifies them against the in-memory store and PostgreSQL (two independent data
sources), including racing reservations (capacity never exceeded, one reservation per conversation), exclusive claims, queue/filter
parity with `AccessFilterEvaluator` and racing lock acquisition. Concurrency scenarios (8 engines racing over 48 conversations:
exactly one assignment each, round-robin spread within one; engines + dispatchers + maintenance racing under capacity: loads end
exactly at capacity) run against both stores.

`PostgresRoutingStore`: append-only `ticketflow.routing` migrations; `tf_key_v1` keys; `format_version` columns; a reservation
transaction locks the cursor row, then one load-anchor row per candidate agent and channel kind in key order (anchor and cursor
rows are created outside the transaction), counts load and upserts the reservation only when no live reservation or newer
assignment exists; queue lists use `PostgresAccessFilterTranslator` (untranslatable → no rows); claims and sweeps use
`FOR UPDATE SKIP LOCKED`; all fences come from one sequence. Migration 2 replaces the due index with
`(next_attempt_at, conversation_key)`. **Clock**: by default every lease, reservation, presence and expiry instant comes from the
database (`clock_timestamp()`, read at the start of each operation), so leases are compared against one authoritative clock
whatever the hosts' drift. `new PostgresRoutingStore(dataSource, timeProvider)` or
`AddPostgresRoutingStores(dataSource, useApplicationClock: true)` switches to the application clock (deterministic tests). Policy
timing (overflow, maximum wait, sticky windows) is evaluated by the engine and worker with the application `TimeProvider` against
store instants, so hosts still need reasonably synchronized clocks.
The in-memory store is a bounded, process-local development example without durability.

## Not yet implemented

- Business-hours-aware routing (skip agents outside their schedule) and SLA-priority ordering of queues (E9).
- A hosted loop for the dispatcher and maintenance worker (E11) and realtime fan-out of routing events (E7).
- Hard reply locks (rejecting replies from non-holders) and per-agent total capacity across channel kinds.
