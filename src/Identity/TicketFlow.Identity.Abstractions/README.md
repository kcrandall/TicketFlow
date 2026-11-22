# TicketFlow.Identity.Abstractions

Contracts for staff (agent) external identities in TicketFlow.

- `StaffIdentityRef(provider, subject)`: an external sign-in identity (lowercase provider token, exact opaque subject; redacted in
  `ToString`). `StaffIdentityLink` maps it to a TicketFlow `UserId`; many identities may map to one user (≤ 32 per user and scope).
- `StaffProfile(user, displayName?, avatarRef?, notificationEmail?)`: optional presentation and notification details keyed by user.
  Routing capacities, skills and presence stay in `TicketFlow.Routing`.
- `StaffIdentityPermissions.Manage` (`staff-identity:manage`, sensitive) and `StaffFieldPaths.NotificationEmail` (`staff.email`).
- `IStaffIdentityService` (authorized, audited management), `IStaffIdentityResolver` (trusted subject → `UserId` resolution for
  principal resolvers), `IStaffIdentityStore` (scope-confined, atomic uniqueness and capacity, audit in the same transaction).
- Union results: `StaffIdentityResult<T>`, `StaffIdentityWriteResult`, `StaffProfileWriteResult`.

See `docs/identity-linking.md`.
