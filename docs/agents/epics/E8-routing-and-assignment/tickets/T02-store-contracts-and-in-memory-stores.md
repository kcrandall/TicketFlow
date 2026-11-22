# T02 — Routing store contracts and in-memory stores

Epic: [E8 — Routing and assignment](../PLAN.md)

Status: **Completed**

## Goal

Store contracts `IRoutingPolicyStore`, `IAgentAvailabilityStore`, `IRoutingAssignmentStore` (atomic select-and-reserve with
round-robin cursor and capacity-checked load), `IRoutingQueueStore` (Access-filtered queue ordered by waiting-since, leased claims
of due entries) and `IConversationPresenceStore` (viewer/composer presence and fenced reply-lock leases), development in-memory
implementations and the shared `TicketFlow.Routing.StoreContracts` fixture.

## Dependencies

E8 T01.

## Acceptance criteria

- Every operation confined to one `TicketScope` (sweeps are store-wide, entries carry scope); revision-fenced policy/profile writes.
- Reservation is atomic: at most one live reservation per conversation, agent load (reserved + assigned per channel kind) never
  exceeds the candidate capacity, cursor advanced in the same step.
- Queue lists apply the `AccessFilter` inside the bounded query; claims never hand one entry to two live leases.

## Verification

- `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release` (contract suite against in-memory stores).

## Risks and boundaries

In-memory stores are bounded process-local examples without durability.

## Results

Store contracts `IRoutingPolicyStore`, `IAgentAvailabilityStore`, `IRoutingAssignmentStore`, `IRoutingQueueStore`, `IConversationPresenceStore` (revision-guarded projection writes, atomic select-and-reserve with `RoutingReservationRequest.Select` as reference), `InMemoryRoutingStore` and the shared `TicketFlow.Routing.StoreContracts` fixture (policies, agents, assignments, reservation races, queue with filter parity, presence/locks).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E6/E9 work in the tree).
- `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release` — 41 passed, 0 failed (model/codec 18, in-memory store contracts 6, services 5, engine 10, concurrency 2).
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55608;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Routing/TicketFlow.Routing.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 11 passed, 0 failed (6 store contracts across two data sources, migration/bounded keys, undecodable rows, 3 engine scenarios); both suites re-run 5 times without failures.
- Regression after the Access built-in additions: `tests/Access/TicketFlow.Access.Tests` (99 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (50 passed), `tests/Conversations/TicketFlow.Conversations.Tests` (36 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E6's in-progress `src/Channels/TicketFlow.Channels` project not yet in the solution (not an E8 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.

### Review fixes (2026-11-22)

- P1: `RoutingOutboxHandler` reacts only to `Started`/`Deleted` changes and batches with an incoming message or a status, assignment,
  team, label or attribute change; notes, extensions, outgoing replies, other metadata and delivery updates are skipped
  (`OutboxHandlerSkipsChangesThatCannotAffectRouting`, `ExtensionAppendsDoNotTouchRoutingState`).
- P1/P3: team, skill and channel-capacity filters moved into `IAgentAvailabilityStore.ListAvailableAsync(scope, AvailableAgentQuery)`
  (in-memory and PostgreSQL `jsonb`), the engine pages through every match and windows > 1000 eligible agents after the round-robin
  cursor (`IRoutingAssignmentStore.GetCursorAsync`); principals are resolved in batches by `IRoutingTeamMembershipResolver`
  (`StoreRoutingTeamMembershipResolver`); the plan cache stays per run and the option is renamed `RunEligibilityPlanLifetime`
  (`RevokedOrRemovedAgentsAreNeverChosenInTheNextRun`, `PoolsLargerThanOnePageStayEligible`, `AvailableAgentFilterContract`).
- P2: queue claims are taken in `(next_attempt_at, conversation_key)` order backed by the new append-only migration 2 index
  `tf_routing_queue_due_v2` and re-sorted by waiting time (queue contract ordering check).
- P2: `PostgresRoutingStore` uses the database clock for every lease, reservation, presence and expiry instant unless an application
  `TimeProvider` is supplied (`DatabaseClockGovernsLeases`; `AddPostgresRoutingStores(..., useApplicationClock)`).
- Tests: `StaleQueueSnapshotAsync` and `TeamChangeBetweenReserveAndAssignAsync` in `ConcurrencyScenarios`, run against both stores.

Verification after the fixes (2026-11-22): `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release` — 48 passed;
`TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55618;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Routing/TicketFlow.Routing.Stores.Postgres.Tests -c Release`
(isolated `postgres:18`) — 15 passed; both suites re-run 3 times without failures.
