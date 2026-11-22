# TicketFlow.Surveys.Stores.Postgres

Durable PostgreSQL storage for TicketFlow surveys. `PostgresSurveyStore` implements `ISurveyStore` over an application-owned
`NpgsqlDataSource`.

- Explicit schema initialization: `await store.InitializeAsync()` applies the append-only, checksummed
  `PostgresSurveyMigrations.Catalog` (component `ticketflow.surveys`) in the data source's current schema.
- Every key includes the full workspace scope via fixed-size `tf_key_v1` keys; raw columns are compared too.
- Invitations: unique per (survey, conversation); creates for one (survey, contact) serialize on a transaction-scoped advisory lock
  so the cooldown cannot be raced; only the 32-byte SHA-256 token hash is stored (unique index); a partial index serves due reads.
- Responses: one per invitation (primary key), written in the same transaction that moves the invitation to Responded.
- Response lists (keyset on responded-at, invitation) and aggregates (UTC `date_trunc` buckets, grouping by inbox/team/agent,
  `count(*) FILTER` sentiment counts, ≤ 1000 rows) translate the `AccessFilter` with `PostgresAccessFilterTranslator` over the
  response's inbox, team and agent (as assignee); labels and attributes are unknown, and untranslatable filters return no rows.

```csharp
await using var dataSource = NpgsqlDataSource.Create(connectionString);
services.AddTicketFlow().AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddSurveys().AddPostgresSurveyStores(dataSource);
await new PostgresSurveyStore(dataSource).InitializeAsync();
```
