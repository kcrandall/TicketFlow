# T01 — Routing model, policy codec and permissions

Epic: [E8 — Routing and assignment](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Routing.Abstractions`: `RoutingPolicy` (strategy union `RoundRobin | LeastBusy | SkillsBased | StickyOwner | Manual`,
optional pool team, overflow rules to a team or another inbox queue, maximum wait, offline reassignment), agent availability
(`AgentStatus` Online/Busy/Away/Offline, presence expiry, capacity per channel kind, bounded skills with levels), queue entries,
conversation presence (viewing/composing) and soft reply locks, routing events for realtime, result unions, `RoutingPolicyCodec`
with explicit versioned discriminators and the permission set.

## Dependencies

E2 (Access), E3 (`RoutingPolicyId`, `ChannelKind`, teams), E5 (conversations, outbox).

## Acceptance criteria

- Bounded, validated inputs (tokens, identifiers, ≤ 8 overflow rules ascending, ≤ 32 skills, ≤ 32 capacities, ≤ 32 skill rules).
- Policies persist as `{"v":1,...}` JSON with `"k"` discriminators; decoding rejects unknown versions, kinds and properties.
- `routing:manage`, `agent-status:update` and `agent-status:manage` are Access built-ins (Agent holds `agent-status:update`;
  Supervisor also holds the other two; Admin and Owner hold all). Queue reads reuse `conversation:read`.

## Verification

- `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release` (model and codec tests).

## Risks and boundaries

Access built-in additions are minimal and additive; `AddRouting()` registers identical definitions (idempotent).

## Results

Implemented in `TicketFlow.Routing.Abstractions`: `RoutingPolicy` with the `RoutingStrategy` union, `SkillRule`/`SkillMatch` union, `OverflowRule`/`OverflowTarget` union, `OfflineReassignment`; `AgentStatus`, `AgentPresence`, `AgentRoutingSettings`, `AgentSkill`; queue, assignment, presence and reply-lock models; `RoutingPolicyCodec` and `AgentSettingsCodec` (strict versioned JSON); `RoutingPermissions`, `RoutingResources`; result unions and routing events.

Access change (minimal, additive): `routing:manage`, `agent-status:update`, `agent-status:manage` added to `AccessPermissions`; Agent holds `agent-status:update`; Supervisor additionally holds `routing:manage` and `agent-status:manage`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E6/E9 work in the tree).
- `dotnet test tests/Routing/TicketFlow.Routing.Tests -c Release` — 41 passed, 0 failed (model/codec 18, in-memory store contracts 6, services 5, engine 10, concurrency 2).
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55608;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Routing/TicketFlow.Routing.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 11 passed, 0 failed (6 store contracts across two data sources, migration/bounded keys, undecodable rows, 3 engine scenarios); both suites re-run 5 times without failures.
- Regression after the Access built-in additions: `tests/Access/TicketFlow.Access.Tests` (99 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (50 passed), `tests/Conversations/TicketFlow.Conversations.Tests` (36 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E6's in-progress `src/Channels/TicketFlow.Channels` project not yet in the solution (not an E8 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
