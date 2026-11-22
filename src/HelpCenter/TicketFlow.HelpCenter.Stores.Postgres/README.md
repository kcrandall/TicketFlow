# TicketFlow.HelpCenter.Stores.Postgres

Durable PostgreSQL storage for the TicketFlow help center. `PostgresHelpCenterStore` implements `IHelpCenterStore` and
`IArticleFeedbackStore` over an application-owned `NpgsqlDataSource`.

- Explicit schema initialization: `await store.InitializeAsync()` applies the append-only, checksummed
  `PostgresHelpCenterMigrations.Catalog` (component `ticketflow.helpcenter`) in the data source's current schema.
- Every key includes the full workspace scope; keys and indexes use fixed-size `tf_key_v1` SHA-256 keys and raw columns are compared.
  Structural writes per workspace serialize on a transaction-scoped advisory lock (capacity, references, depth, emptiness);
  updates and deletes are compare-and-set on `revision`.
- Article writes append the immutable revision and rebuild `tf_helpcenter_published` in the same transaction; published slugs are
  unique per (portal, locale) via a unique index.
- Search: a stored weighted `tsvector` per (article, locale) — title A, tags and excerpt B, body plain text C — computed with the
  locale's `regconfig` from `HelpCenterSearchConfiguration` (fallback `simple`), a GIN index, `websearch_to_tsquery`,
  `ts_rank_cd` ranking, bounded `LIMIT`/`OFFSET`, and plain-text `ts_headline` snippets.
- Feedback: one view marker per (article, visitor hash, day) and one vote per (article, visitor hash); counters change in the same
  transaction; markers and counters cascade with the article. `PurgeViewMarkersAsync` removes old view markers in bounded batches.

```csharp
using TicketFlow.HelpCenter.Stores.Postgres;

await using var dataSource = NpgsqlDataSource.Create(connectionString);
services.AddTicketFlow().AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddHelpCenter().AddPostgresHelpCenterStores(dataSource);
await new PostgresHelpCenterStore(dataSource).InitializeAsync();
```
