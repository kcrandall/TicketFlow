# TicketFlow.Integrations.Surveys

Connects TicketFlow conversations (E5) to surveys (E12).

- `ConversationResolvedSurveyHandler`: an `IConversationOutboxHandler` (consumer `surveys.resolved`) that turns every resolved
  `StatusChanged` event into a `ConversationResolvedNotification` (inbox, team, assigned agent, contact locale) for
  `ISurveyScheduler`. Built only from trusted store reads; idempotent through the scheduler's per-conversation duplicate check.

```csharp
services.AddTicketFlow()
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddSurveys().AddPostgresSurveyStores(dataSource)
    .AddConversationSurveys();
```
