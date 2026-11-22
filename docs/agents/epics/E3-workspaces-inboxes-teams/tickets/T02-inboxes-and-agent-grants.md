# T02 — Inboxes and inbox agent grants

Epic: [E3 — Workspaces, inboxes and teams](../PLAN.md)

Status: **Completed**

## Goal

Inbox definitions (id, name, opaque `ChannelKind` + channel configuration reference, business-calendar reference, opaque SLA policy
and routing references, enabled flag, bounded greeting/away messages) with an authorized service, Access-filtered lists, and inbox
agent membership modeled as inbox-scoped Access grants written through `IAccessAdministrator`.

## Dependencies

E3 T01.

## Acceptance criteria

- Inbox list uses `IAccessQueryPlanner` (`inbox:read`) and the store applies the filter; results equal point checks.
- Create/delete need workspace-level `inbox:manage`; update accepts an inbox-scoped grant.
- Calendar references must exist in the same workspace.
- Adding/removing an inbox agent creates/revokes a deterministic inbox-scoped grant via `IAccessAdministrator` (no parallel ACL).

## Verification

- Focused MSTest projects; store contract fixture against the in-memory store.

## Risks and boundaries

Channel kinds and configuration references stay opaque until E6; SLA/routing references stay opaque until E8/E9.

## Results

- `Inbox` (`ChannelBinding` = `ChannelKind` token + opaque configuration id, `BusinessCalendarId?`, `SlaPolicyId?`,
  `RoutingPolicyId?`, enabled, greeting/away ≤ 1024 with line breaks), `IInboxStore` (filtered keyset lists), `InboxService`.
- Lists plan `inbox:read` and push the `AccessFilter` into the store; create/delete need workspace-level `inbox:manage`, update
  accepts inbox-scoped grants; calendar references must exist.
- `AddAgentAsync` / `RemoveAgentAsync` write deterministic inbox-scoped grants (`InboxAgentGrants.For`, SHA-256 identifiers)
  through `IAccessAdministrator` (authority, escalation and audit apply).
- Tests: inbox store contract + list filter fixture, randomized list/point-check parity (5 seeds), service tests for filtering,
  scoped management, calendar references and agent grants.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (with E4's concurrent Contacts work in the tree).
- `dotnet test tests/Workspaces/TicketFlow.Workspaces.Tests -c Release` — Passed 42/42.
- `TICKETFLOW_REQUIRE_POSTGRES=1 TICKETFLOW_TEST_POSTGRES=<disposable postgres:18 on 127.0.0.1:55743> dotnet test tests/Workspaces/TicketFlow.Workspaces.Stores.Postgres.Tests -c Release` — Passed 11/11 (without PostgreSQL: 11 inconclusive).
- `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` — Passed 95/95; `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against the same container — Passed 15/15; `tests/TicketFlow.Tests` — Passed 6/6.
- `dotnet pack` of the three Workspaces projects — packages and symbols created with package validation.
- `bash scripts/verify-package-metadata.sh` currently stops at E4's in-progress `src/Contacts` projects (not yet in the solution); the Workspaces entries were added to `TicketFlow.slnx` and `scripts/nuget-projects.tsv` in dependency order.

Post-ticket parallel reviews have not been run yet.
