# TicketFlow.Stores.Migrations.Postgres

Explicit PostgreSQL migration protocol for feature-owned TicketFlow stores (mirrors EnvelopeFlow's protocol).

- `PostgresMigrationCatalog`: a feature's immutable, append-only, contiguous history of `PostgresMigration(version, id, sql)`.
  Each step's identity and exact SQL bytes form a SHA-256 checksum. Never edit an applied migration; append a new step.
- `PostgresMigrationRunner` over an application-owned `NpgsqlDataSource`: `InspectAsync`/`PlanAsync` are read-only;
  `ApplyAsync` takes a transaction advisory lock, revalidates the journal (`tf_store_migrations` in `current_schema()`), applies
  pending steps and records them in one transaction. Unknown future versions, gaps, checksum changes and downgrades fail with
  `MigrationProtocolException`. Lock wait defaults to 5 s and the operation to 30 s. Construction and DI never migrate.
- `PostgresSchemaBaseline` / `PostgresSchemaRequirements` let a feature adopt an existing unversioned schema without running
  historical SQL.
- `PostgresBoundedKeys.Version1Sql` defines `tf_key_v1(VARIADIC text[])`, a frozen JSON-framed UTF-8 SHA-256 key function.
  Features key and index fixed-size generated `bytea` columns instead of raw composite identifiers, then compare the raw columns
  for exact identity.

Migration SQL is trusted deployment code. Do not log SQL, connection strings or stored payloads.
