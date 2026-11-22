# TicketFlow.Workspaces

Authorized administration of TicketFlow's support structure.

- `WorkspaceSettingsService` (`workspace:read`, `workspace:manage`), `BusinessCalendarService` (`workspace:read`,
  `calendar:manage`), `InboxService` and `TeamService` authorize every read and write through `IAccessEvaluator` before touching a
  store. Inbox and team lists use `IAccessQueryPlanner` and push the `AccessFilter` into the store.
- Inbox create/delete need workspace-level `inbox:manage`; updates accept inbox-scoped grants. Team create/delete need
  workspace-level `team:manage`; updates and membership changes accept team-scoped grants.
- Inbox agents are inbox-scoped Access grants (`InboxAgentGrants`) created and revoked through `IAccessAdministrator`, which
  authorizes, prevents escalation and audits — there is no parallel inbox ACL.
- Adding members to a team that holds allow grants requires the actor to hold every permission those grants confer, so team
  membership never escalates privileges.
- `StoreTeamMembershipResolver` implements `ITeamMembershipResolver`; `resolver.ResolveUserPrincipalAsync(scope, user)` builds a
  `UserPrincipal` with the current team snapshot.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddInMemoryAccessStores()
    .AddWorkspaces()
    .AddInMemoryWorkspaceStores(); // development only
```

Registrations use TryAdd; services are scoped. The in-memory stores are bounded, process-local development examples with no
durability; use `TicketFlow.Workspaces.Stores.Postgres` or an application-owned implementation in production.
