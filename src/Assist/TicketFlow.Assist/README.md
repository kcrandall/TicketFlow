# TicketFlow.Assist

Assistant service for TicketFlow on `Microsoft.Extensions.AI`:

- `AssistContextBuilder` builds prompts only from what the requesting principal can see (masked conversation and timeline, notes
  only with `note:read`, masked contact, help center snippets through the principal's own search) and delimits/escapes customer
  content as untrusted data.
- `AssistService` (`IAssistService`) — summary, grounded reply draft with citations, triage, sentiment and intent, translation and
  tone rewrite with JSON-schema outputs, atomic per-workspace token quotas and rate limits, durable usage records and
  `assist.summary` / `assist.draft` / `assist.triage` timeline metadata.
- `AssistAdministration` (`IAssistAdministration`, `assist:manage`), `AssistChatClientFactory` (provider client + option mapping,
  usage metrics, tool-invocation telemetry, OpenTelemetry without sensitive data), `AssistModelRegistry`, `InMemoryAssistStore`.

```csharp
using TicketFlow.Assist;

services.AddTicketFlow()
    .AddAccessControl().AddConversations().AddContacts().AddHelpCenter()
    .AddAssist(options => options.DefaultDailyTokenLimit = 1_000_000)
    .AddClaudeAssist()            // TicketFlow.Assist.Providers.Claude
    .AddInMemoryAssistStores();   // or AddPostgresAssistStores(dataSource)
```

See `docs/assist.md`.
