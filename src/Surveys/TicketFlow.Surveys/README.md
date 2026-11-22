# TicketFlow.Surveys

CSAT and NPS survey behavior for TicketFlow.

- `SurveyScheduler` (`ISurveyScheduler`): consumes `ConversationResolvedNotification` (trusted, internal input that the E5
  conversation outbox will feed) and creates pending invitations — inbox survey before workspace survey per kind, delay applied,
  idempotent per (survey, conversation), per-contact cooldown enforced atomically by the store.
- `SurveyDispatcher` (`ISurveyDispatcher`): issues a fresh 256-bit `SurveyToken` per due invitation, stores only its SHA-256 hash,
  then calls `ISurveyInvitationSender` (implemented by the EnvelopeFlow adapter; no transport here). Failures retry with exponential
  backoff up to `SurveyOptions.MaximumSendAttempts`, then fail; invitations of disabled surveys are cancelled.
- `SurveyResponder` (`ISurveyResponder`): anonymous token path — strict parsing, scope-confined hash lookup, expiry, score range and
  bounded comment checks, single response with idempotent replay.
- `SurveyService` (`ISurveyService`): definitions (`survey:read` / `survey:manage` on the survey's inbox or workspace), invitation and
  response reads (`survey:read`, `survey.comment` field masking), response lists and aggregates through `IAccessQueryPlanner`
  filters (`survey:read`, `report:view`).
- `InMemorySurveyStore`: bounded, process-local development store.

```csharp
services.AddSingleton<ISurveyInvitationSender, MyEnvelopeFlowSender>();
services.AddTicketFlow()
    .AddAccessControl().AddInMemoryAccessStores()
    .AddSurveys(options => options.MaximumSendAttempts = 3)
    .AddInMemorySurveyStores(); // development only
```
