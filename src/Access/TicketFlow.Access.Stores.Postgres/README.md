# TicketFlow.Access.Stores.Postgres

Durable PostgreSQL storage for TicketFlow access control. `PostgresAccessStore` implements `IAccessRoleStore`,
`IAccessGrantStore`, `IAccessFieldPolicyStore` and `IAccessAuditSink` over an application-owned `NpgsqlDataSource`.

- Explicit schema initialization: `await store.InitializeAsync()` applies the append-only, checksummed
  `PostgresAccessMigrations.Catalog` through `TicketFlow.Stores.Migrations.Postgres` in the data source's current schema. Nothing
  is created implicitly.
- Every key includes the full partition or workspace scope. Primary keys and indexes use fixed-size `tf_key_v1` SHA-256 keys
  (identifiers can be 256 UTF-16 units each, beyond PostgreSQL's B-tree tuple limit as raw composite text); raw columns are also
  compared for exact identity.
- Updates and deletes are compare-and-set on `revision`; creates start at revision 1. Per-scope and per-subject capacity limits
  are checked under transaction-scoped advisory locks.
- Grants persist explicit numeric discriminators (subject kind, scope level, effect, kind) and conditions use the versioned
  `AccessConditionCodec` JSON. Rows of unknown format versions fail closed.
- `PostgresAccessFilterTranslator` turns an `AccessFilter` into a parameterized predicate over your table's scope, team, inbox,
  assignee, label and attribute columns with the same three-valued semantics. Untranslatable filters return
  `PostgresAccessFilterUntranslatable`; `OrDenyAll()` substitutes `FALSE`, never allow-all.

```csharp
await using var dataSource = NpgsqlDataSource.Create(connectionString);
services.AddTicketFlow().AddAccessControl().AddPostgresAccessStores(dataSource);
await new PostgresAccessStore(dataSource).InitializeAsync();
```
