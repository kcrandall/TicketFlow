# TicketFlow.Reporting.Stores.Postgres

Durable PostgreSQL storage for TicketFlow reporting: `PostgresReportingStore` implements `IReportingStore`.

```csharp
services.AddTicketFlow().AddReporting().AddPostgresReportingStores(dataSource);
await new PostgresReportingStore(dataSource).InitializeAsync();   // explicit schema creation
```

- Tables `tf_report_conversations` (projection state), `tf_report_facts` (immutable facts) and `tf_report_rollups` (15-minute cells
  with histogram bins), created only by `InitializeAsync` through the append-only `ticketflow.reporting` migration catalog;
  `tf_key_v1` keys plus raw-column comparison; `format_version` on every row.
- A projection is one transaction: the revision-fenced state row, `INSERT … ON CONFLICT DO NOTHING RETURNING` facts and one ordered
  rollup upsert for exactly the inserted facts, so each fact is counted once. Rollup rebuilds take a scope advisory lock that
  projections share.
- Aggregates bucket cells with `width_bucket` over caller-supplied boundaries and run the translated Access filter
  (`PostgresAccessFilterTranslator`) in the query; unknown labels and attributes map to NULL columns, matching
  `AccessFilterEvaluator` three-valued semantics.
