# TicketFlow.Assist.Stores.Postgres

Durable PostgreSQL `IAssistStore` for TicketFlow Assist: revisioned workspace settings (versioned JSON), atomic per-workspace token
quota and per-minute rate-limit reservations under a transaction-scoped advisory lock, exactly-once usage settlement, usage
summaries and bounded purge. Usage rows carry identifiers and token counts only — never prompts or outputs.

```csharp
using TicketFlow.Assist.Stores.Postgres;

builder.AddAssist().AddPostgresAssistStores(dataSource);
await new PostgresAssistStore(dataSource).InitializeAsync();   // applies PostgresAssistMigrations.Catalog (component ticketflow.assist)
```
