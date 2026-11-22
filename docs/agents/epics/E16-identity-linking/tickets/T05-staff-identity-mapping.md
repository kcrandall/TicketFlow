# T05 — Staff identity mapping and agent profiles

Epic: [E16 — External identities and account linking](../PLAN.md)

Status: **Completed**

## Goal

New `TicketFlow.Identity.Abstractions`, `TicketFlow.Identity` and `TicketFlow.Identity.Stores.Postgres`: (provider, subject) →
`UserId` many-to-one, `staff-identity:manage`, audit, `IStaffIdentityResolver` for principal resolvers, optional `StaffProfile`.

## Dependencies

E2, E3 (`ITeamMembershipResolver`).

## Acceptance criteria

- Workspace-scoped, atomic (provider, subject) uniqueness, ≤ 32 identities per user, idempotent relink, unlink fenced on the user,
  audit in the store transaction.
- Link/unlink require `staff-identity:manage` plus `CheckCanConferAsync` over the target user's workspace, team and tenant grants
  (no escalation; tenant grants need tenant-scoped authority).
- Profiles: self-service with `workspace:read`, others with `staff-identity:manage`; reads need `team:read`; `staff.email` masked by
  field policies except for the user. Routing profiles are referenced, not duplicated.
- Shared store contract on in-memory and PostgreSQL (independent clients, concurrent links pick one user).

## Verification

- `dotnet test tests/Identity/TicketFlow.Identity.Tests -c Release`
- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Identity/TicketFlow.Identity.Stores.Postgres.Tests -c Release`

## Risks and boundaries

`TicketFlow.Integrations.IdentityFlow` is not edited; the resolver integration point is documented in docs/identity-linking.md.

## Results

- Packages, READMEs, `AddStaffIdentity`, `AddInMemoryStaffIdentityStores`, `AddPostgresStaffIdentityStores`, `ticketflow.identity`
  migration 1; solution and `scripts/nuget-projects.tsv` entries.
- `TicketFlow.Identity.Tests` — 5 passed (model, in-memory contract, many-to-one + resolver + audit + self-list, escalation incl. team
  and tenant grants, profiles and masking). `TicketFlow.Identity.Stores.Postgres.Tests` — 4 passed against `postgres:18`.
