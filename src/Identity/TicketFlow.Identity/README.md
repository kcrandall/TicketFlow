# TicketFlow.Identity

Staff identity mapping for TicketFlow.

- `StaffIdentityService`: link/unlink require `staff-identity:manage` **and** that the actor could confer every grant the target user
  holds in the workspace (its own grants, its teams' grants via `ITeamMembershipResolver`, tenant grants needing tenant-scoped
  authority) through `IAccessAdministrator.CheckCanConferAsync`, so a mapping never grants more than the actor already has. Users
  list their own identities and edit their own profile; profile reads need `team:read` and mask `staff.email` per field policies.
- `StoreStaffIdentityResolver` (`IStaffIdentityResolver`): trusted, unaudited resolution for principal resolvers.
- `InMemoryStaffIdentityStore`: bounded, process-local development store.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddInMemoryAccessStores()
    .AddWorkspaces().AddInMemoryWorkspaceStores()
    .AddStaffIdentity()
    .AddInMemoryStaffIdentityStores();   // development only
```
