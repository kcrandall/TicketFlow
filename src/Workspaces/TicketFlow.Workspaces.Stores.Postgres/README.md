# TicketFlow.Workspaces.Stores.Postgres

Durable PostgreSQL storage for TicketFlow workspaces. `PostgresWorkspaceStore` implements `IWorkspaceSettingsStore`,
`IInboxStore`, `ITeamStore` and `IBusinessCalendarStore` over an application-owned `NpgsqlDataSource`.

- Explicit schema initialization: `await store.InitializeAsync()` applies the append-only, checksummed
  `PostgresWorkspaceMigrations.Catalog` (component `ticketflow.workspaces`) through `TicketFlow.Stores.Migrations.Postgres` in the
  data source's current schema. Nothing is created implicitly.
- Every key includes the full workspace scope; primary keys and indexes use fixed-size `tf_key_v1` SHA-256 keys and raw columns
  are compared for exact identity. Team members live in `tf_workspace_team_members` with a per-user index, so
  `ListTeamsForUserAsync` (and principal team snapshots) is one indexed query.
- Updates and deletes are compare-and-set on `revision`; creates start at revision 1. Per-workspace capacity and the per-user team
  limit (256) are checked under transaction-scoped advisory locks. Channel bindings are unique per workspace (migration v2 index;
  reuse is `WorkspaceWriteRejection.ChannelBindingInUse`).
- Inbox and team lists translate the caller's `AccessFilter` with `PostgresAccessFilterTranslator` inside the query; an
  untranslatable filter returns no rows. Ordering uses `COLLATE "C"`.
- Rows carry `format_version`; calendar schedules use the canonical `BusinessCalendarCodec` text. Undecodable rows throw.

```csharp
await using var dataSource = NpgsqlDataSource.Create(connectionString);
services.AddTicketFlow().AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource);
await new PostgresWorkspaceStore(dataSource).InitializeAsync();
```
