# TicketFlow.Workspaces.Abstractions

Provider-neutral model and contracts for TicketFlow's support structure:

- `WorkspaceSettings` (name, default locale, time zone, ≤ 64 feature toggles), revisioned per workspace scope.
- `Inbox`: channel binding (`ChannelKind` + opaque configuration reference), business-calendar reference, opaque `SlaPolicyId` and
  `RoutingPolicyId`, enabled flag and bounded greeting/away messages.
- `Team` with ≤ 1000 members; stores cap every user at 256 teams per workspace so principal snapshots always fit.
- `BusinessCalendar` (weekly hours per weekday in an IANA/Windows zone plus holiday/date overrides) and
  `BusinessCalendarCalculator` (`IsOpen`, `NextOpen`, `NextClose`, `AddBusinessTime`, `BusinessTimeBetween`, `GetOpenIntervals`)
  with wall-clock DST semantics; `BusinessCalendarCodec` is the versioned persisted schedule format.
- Unions `WorkspaceResult<T>` (`WorkspaceSucceeded<T>`, `WorkspaceRefused`) and `WorkspaceWriteResult<T>`. Switch over every case
  without a discard arm.
- Store contracts `IWorkspaceSettingsStore`, `IInboxStore`, `ITeamStore`, `IBusinessCalendarStore` (scope-confined,
  revision-fenced, atomic capacity; lists apply an `AccessFilter` over `WorkspaceResources`), services `IWorkspaceSettingsService`,
  `IInboxService`, `ITeamService`, `IBusinessCalendarService`, and `ITeamMembershipResolver` with `ResolveUserPrincipalAsync`.
- `WorkspacePermissions`: `workspace:read|manage`, `inbox:read|manage`, `team:read|manage`, `calendar:manage` (built into the
  Access catalog). `InboxAgentGrants` derives deterministic inbox-scoped grant identifiers for inbox agents.

Behavior and development stores live in `TicketFlow.Workspaces`; durable storage in `TicketFlow.Workspaces.Stores.Postgres`.
