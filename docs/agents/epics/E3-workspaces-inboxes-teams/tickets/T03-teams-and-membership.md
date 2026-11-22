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

Post-ticket reviews completed; findings fixed as listed below.

### Post-ticket review fixes (2026-11-22)

- P1 team deny bypass: removing members or deleting a team now requires the authority to revoke the team's live deny grants
  (`access:manage` on the target, `access:manage-tenant` for tenant grants); removed members' own deny grants using
  `TeamInPrincipalTeamsCondition` count too. Team delete then revokes every team-subject grant through `IAccessAdministrator`.
- P1 weak guard: `TeamGrantGuard` replaced by `TeamGrantPolicy` over new Access APIs `IAccessAdministrator.CheckCanConferAsync` /
  `CheckCanRevokeAsync` (standard grants only, tenant-scoped authority for tenant grants, one context load, no audit records).
- P2 condition-mediated escalation: added members' own allow grants using `TeamInPrincipalTeamsCondition` are checked as conferrals
  against `AccessResource(scope, team: X)`.
- P3 channel bindings are unique per workspace: in-memory check plus PostgreSQL migration v2 (`binding_key` + unique index), surfaced as
  `WorkspaceWriteRejection.ChannelBindingInUse`.
- Inbox delete revokes its deterministic inbox agent grants (new `IAccessGrantStore.ListForTargetAsync`, Access PostgreSQL migration v2
  `target_key` index) after checking revoke authority.
- PostgreSQL long-identifier coverage (inbox, team, members, `ListTeamsForUserAsync`, calendar, keyset `after` lists) and a v1→v2 upgrade
  test; builder extensions moved to `TicketFlow.Workspaces` / `TicketFlow.Workspaces.Stores.Postgres`; permissions registered once via
  a marker.
- Regression tests: `ReviewRegressionTests` (8), `AdministrativeCheckTests` (4, Access), `VerifyGrantTargetsAsync` and
  `VerifyInboxChannelBindingsAsync` contracts (in-memory and PostgreSQL).
- Verification: `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); Workspaces.Tests 50 passed; Access.Tests 99 passed;
  with `TICKETFLOW_REQUIRE_POSTGRES=1` against a disposable `postgres:18` (127.0.0.1:55743): Workspaces.Stores.Postgres.Tests 13 passed,
  Access.Stores.Postgres.Tests 15 passed; Conversations.Tests 36 passed (consumer of `AddWorkspaces`).

