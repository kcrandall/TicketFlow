# TicketFlow.Routing.Abstractions

Contracts for TicketFlow routing and assignment.

- `RoutingPolicy` (referenced by an inbox's `RoutingPolicyId`): the `RoutingStrategy` union (`RoundRobin | LeastBusy | SkillsBased |
  StickyOwner(window, fallback) | Manual`), an optional pool team, ascending `OverflowRule`s to a team or another inbox queue, a
  maximum wait and the `OfflineReassignment` behavior. `RoutingPolicyCodec` persists policies as versioned JSON with explicit
  discriminators.
- Agent availability: `AgentStatus` (Online/Busy/Away/Offline), `AgentPresence` with heartbeat expiry, `AgentRoutingSettings`
  (capacity per channel kind, bounded skills with levels 1-10).
- Queue (`RoutingQueueEntry`, Access resource snapshot, waiting-since order), assignment/load projection (`RoutingAssignment`,
  atomic `RoutingReservationRequest` with the reference selection), conversation presence and soft `ReplyLock`s.
- Store contracts: `IRoutingPolicyStore`, `IAgentAvailabilityStore`, `IRoutingAssignmentStore`, `IRoutingQueueStore`,
  `IConversationPresenceStore`.
- Services: `IRoutingPolicyService`, `IAgentAvailabilityService`, `IConversationCollisionService`, `IRoutingQueueService`,
  `IRoutingEngine` (union `RoutingOutcome = Assigned | Queued | Settled`), routing events (`RoutingEvent` union) for
  `IRoutingEventObserver`s, `RoutingPermissions` and `RoutingResources`.
