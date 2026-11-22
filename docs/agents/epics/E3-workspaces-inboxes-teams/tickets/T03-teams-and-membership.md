# T03 — Teams, membership and principal resolution

Epic: [E3 — Workspaces, inboxes and teams](../PLAN.md)

Status: **Completed**

## Goal

Teams (id, name, description, bounded members) with revisioned membership, Access-filtered lists, `ITeamMembershipResolver`
and a helper that builds a `UserPrincipal` with the user's current team snapshot.

## Dependencies

E3 T01.

## Acceptance criteria

- A user belongs to at most `UserPrincipal.MaximumTeamMemberships` teams per workspace, enforced atomically by stores.
- Adding members to a team that holds allow grants requires the actor to hold every permission those grants confer (no
  escalation through team membership).
- Resolver output builds a principal whose team grants apply in the evaluator.

## Verification

- Focused MSTest projects; store contract fixture against the in-memory store.

## Risks and boundaries

Principal snapshots are taken at resolution time; membership changes apply on the next resolution.

## Results

- `Team` (≤ 1000 members), `StoredTeam`, `TeamSummary`, `ITeamStore` (atomic ≤ 256 teams per user, `ListTeamsForUserAsync`),
  `TeamService` (create/delete workspace-level, update/membership team-scoped, revision-fenced, no-op membership changes keep the
  revision), `TeamGrantGuard` (adding members requires holding every permission of the team's allow grants on their targets).
- `ITeamMembershipResolver`, `StoreTeamMembershipResolver` and `ResolveUserPrincipalAsync` build `UserPrincipal` team snapshots.
- Tests: team store contract (membership limit including an 8-way race), list parity, resolver → evaluator team grants,
  escalation refusals (including pre-existing grants on a new team id), team-scoped managers.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with E4's concurrent Contacts work in the tree).
- `dotnet test tests/Workspaces/TicketFlow.Workspaces.Tests -c Release` — Passed 42/42.
- `TICKETFLOW_REQUIRE_POSTGRES=1 TICKETFLOW_TEST_POSTGRES=<disposable postgres:18 on 127.0.0.1:55743> dotnet test tests/Workspaces/TicketFlow.Workspaces.Stores.Postgres.Tests -c Release` — Passed 11/11 (without PostgreSQL: 11 inconclusive).
- `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` — Passed 95/95; `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against the same container — Passed 15/15; `tests/TicketFlow.Tests` — Passed 6/6.
- `dotnet pack` of the three Workspaces projects — packages and symbols created with package validation.
- `bash scripts/verify-package-metadata.sh` currently stops at E4's in-progress `src/Contacts` projects (not yet in the solution); the Workspaces entries were added to `TicketFlow.slnx` and `scripts/nuget-projects.tsv` in dependency order.

Post-ticket parallel reviews have not been run yet.
