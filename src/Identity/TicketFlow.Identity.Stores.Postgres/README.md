# TicketFlow.Identity.Stores.Postgres

Durable PostgreSQL `IStaffIdentityStore` for TicketFlow staff identities: `tf_staff_identities` (primary key
`tf_key_v1(scope…, provider, subject)`, per-user index), `tf_staff_profiles` and `tf_staff_identity_audit`. Per-user capacity is
checked under a transaction-scoped advisory lock; audit rows commit with the change; every statement is parameterized.

```csharp
services.AddTicketFlow().AddStaffIdentity().AddPostgresStaffIdentityStores(dataSource);
await new PostgresStaffIdentityStore(dataSource).InitializeAsync();   // explicit, append-only migrations (ticketflow.identity)
```
