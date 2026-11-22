# Workspaces, inboxes, teams and business calendars

The support structure that access grants, routing (E8) and SLAs (E9) target. Three packages under `src/Workspaces`:

| Package | Contents |
| --- | --- |
| `TicketFlow.Workspaces.Abstractions` | `WorkspaceSettings`, `Inbox`, `Team`, `BusinessCalendar` and `BusinessCalendarCalculator`, `BusinessCalendarCodec`, result unions, store and service contracts, `ITeamMembershipResolver`, `InboxAgentGrants`, `WorkspaceResources`, `WorkspacePermissions`. |
| `TicketFlow.Workspaces` | `WorkspaceSettingsService`, `InboxService`, `TeamService`, `BusinessCalendarService`, `StoreTeamMembershipResolver`, `AddWorkspaces()`, development in-memory stores (`AddInMemoryWorkspaceStores()`). |
| `TicketFlow.Workspaces.Stores.Postgres` | `PostgresWorkspaceStore` (all four store contracts), `PostgresWorkspaceMigrations`, `AddPostgresWorkspaceStores(dataSource)`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource);
await new PostgresAccessStore(dataSource).InitializeAsync();
await new PostgresWorkspaceStore(dataSource).InitializeAsync();
```

## Model

- **Workspace settings**: one revisioned record per `TicketScope` — name (≤ 128), default locale (BCP 47 style, ≤ 35), time zone
  (IANA or Windows identifier; services require it to resolve on the host) and ≤ 64 feature toggles keyed by lowercase token.
- **Inbox**: `InboxId`, name, `ChannelBinding` (opaque `ChannelKind` token + channel configuration reference owned by channel
  packages, E6), optional `BusinessCalendarId` (must exist in the workspace), opaque `SlaPolicyId` (E9) and `RoutingPolicyId`
  (E8), enabled flag, greeting/away messages (≤ 1024, line breaks allowed; customer-facing content, never logged).
- **Team**: `TeamId`, name, description, ≤ 1000 members (`UserId`). Every membership change increments the team revision. Stores
  cap each user at `WorkspaceLimits.MaximumTeamsPerUser` (= `UserPrincipal.MaximumTeamMemberships`, 256) teams per workspace,
  atomically, so resolved principal snapshots always fit.
- **Business calendar**: weekly `BusinessHours` spans per weekday (whole minutes, `[start, end)`, ≤ 8 per day, sorted, overlaps
  rejected, touching spans merged) in a time zone, plus ≤ 1024 `CalendarDateOverride`s (empty = holiday, otherwise the only open
  spans that local date). Overnight shifts are two spans on consecutive days.

Capacity per workspace: 1000 inboxes, 1000 teams, 256 calendars. Lists page by identifier (≤ 200 per page, keyset `After`).

## Business-time calculator

`BusinessCalendarCalculator` (in Abstractions, so SLA packages depend only on contracts): `IsOpen`, `NextOpen`, `NextClose`,
`AddBusinessTime`, `BusinessTimeBetween` and `GetOpenIntervals`. Semantics are wall-clock: an instant is open when its local time in
the calendar zone falls in that local date's spans. Local times skipped by spring-forward are never open; local times repeated by
fall-back are open in both passes, so business time is real elapsed time (a 00:00–24:00 day lasts 23 or 25 hours). Skipped local
dates (Samoa 2011-12-30) are never open; transitions at local midnight move time between dates correctly. Results are UTC.
`AddBusinessTime(start, d)` returns the earliest `t` with `BusinessTimeBetween(start, t) == d` (verified as an inverse over
randomized instants in four zones). Searches and spans are bounded by `MaximumHorizon` (3660 days) and instants by 1900–2300;
`NextOpen`/`AddBusinessTime` return `null` when nothing is found in the horizon. Offset transitions are located by sampling the zone
at most one day apart and bisecting to the tick (zones that change and revert within one day are unsupported; none exist).

## Authorization

All services authorize through Access before touching a store; `WorkspaceRefusal.Forbidden` precedes `NotFound`.

| Operation | Permission and resource |
| --- | --- |
| Settings read / create / update | `workspace:read` / `workspace:manage` on the workspace |
| Calendar read, list / write | `workspace:read` / `calendar:manage` on the workspace |
| Inbox get | `inbox:read` on `WorkspaceResources.Inbox(scope, id)` |
| Inbox list | `IAccessQueryPlanner` for `inbox:read`; the store applies the `AccessFilter` in its query |
| Inbox create, delete / update | `inbox:manage` at workspace level / on the inbox (inbox-scoped grants allowed) |
| Inbox agents add / remove | `inbox:manage` on the inbox, then `IAccessAdministrator.CreateGrantAsync` / `RevokeGrantAsync` |
| Team get / list | `team:read` on `WorkspaceResources.Team(scope, id)` / planner for `team:read` |
| Team create, delete / update, membership | `team:manage` at workspace level / on the team |

`workspace:read`, `inbox:read`, `team:read` and `calendar:manage` were added to the built-in Access catalog (the `*:manage`
permissions already existed). Every built-in role holds the three read permissions; Admin also holds `calendar:manage` (but, as
before, not `workspace:manage`). `AddWorkspaces()` registers identical definitions, which is idempotent.

**Inbox membership is an Access grant**, not a parallel ACL: `AddAgentAsync` creates an inbox-scoped grant (default role Agent) with
the deterministic identifier `InboxAgentGrants.For(workspace, inbox, agent, role)` (`tf-inbox-agent-v1-` + SHA-256 hex), so the
administrator's authority, escalation and audit rules apply and adding twice is a conflict.

**Team membership cannot escalate**: adding members (create with members, update, `AddMemberAsync`) to a team that is the subject of
allow grants requires the actor to hold every permission of each such grant's role on its target (tenant-scoped team grants are
checked at workspace level). Removing members needs only `team:manage`.

**Principals**: `ITeamMembershipResolver` is a trusted, unauthorized path for building principals:
`await resolver.ResolveUserPrincipalAsync(scope, userId)` returns a `UserPrincipal` with the current team snapshot, so team-subject
grants and `TeamInPrincipalTeamsCondition` apply. Snapshots do not change when membership later changes.

Workspace records define no maskable fields; an `AccessMasked` decision permits the read.

## Stores

| Contract | Key | Guarantees |
| --- | --- | --- |
| `IWorkspaceSettingsStore` | `TicketScope` | Create at revision 1 (conflict if present); compare-and-set update. |
| `IInboxStore` | scope + `InboxId` | Compare-and-set update/delete; ≤ 1000 per scope atomically; `ListAsync(scope, filter, page)` returns exactly the inboxes where `AccessFilterEvaluator.Matches(filter, WorkspaceResources.Inbox(...))`. |
| `ITeamStore` | scope + `TeamId` | Update replaces name, description and members; ≤ 1000 teams per scope and ≤ 256 teams per user atomically; `ListTeamsForUserAsync`; filtered summaries. |
| `IBusinessCalendarStore` | scope + `BusinessCalendarId` | Compare-and-set; ≤ 256 per scope. |

Inbox and team resources carry no labels or attributes (unknown), so label/attribute conditions fail closed in both point checks and
lists. The shared contract fixture `tests/Workspaces/TicketFlow.Workspaces.StoreContracts` (including randomized list/point-check
parity over real access plans) runs against the in-memory stores and PostgreSQL.

PostgreSQL: tables `tf_workspace_settings`, `tf_workspace_inboxes`, `tf_workspace_teams`, `tf_workspace_team_members` (per-user
index) and `tf_workspace_calendars`, created only by `InitializeAsync` through the append-only `ticketflow.workspaces` migration
catalog; `tf_key_v1` keys plus raw-column comparison; `format_version` on every row; capacity and the per-user team limit checked
under workspace advisory locks; list filters translated with `PostgresAccessFilterTranslator` (untranslatable → no rows) and ordered
with `COLLATE "C"`; calendar schedules stored as canonical `BusinessCalendarCodec` text; undecodable rows throw.

## Known limits

- Deleting an inbox or team does not revoke grants that name it; recreating the same identifier revives them. Inbox agents cannot
  be listed until the grant store gains a by-scope query.
- Deleting a calendar does not check inbox references; consumers must treat a missing calendar as a configuration error.
- The team escalation check evaluates the actor with all its grants (including break-glass), unlike the administrator, which uses
  standard grants only.
