# TicketFlow.Surveys

CSAT and NPS survey behavior for TicketFlow.

- `SurveyScheduler` (`ISurveyScheduler`): consumes `ConversationResolvedNotification` (trusted, internal input that the E5
  conversation outbox will feed) and creates pending invitations — inbox survey before workspace survey per kind, delay applied,
  idempotent per (survey, conversation), per-contact cooldown enforced atomically by the store.
- `SurveyDispatcher` (`ISurveyDispatcher`): claims due invitations atomically with a lease (`SurveyOptions.SendLease`; concurrent
  dispatchers never claim the same invitation), derives each invitation's token with HMAC-SHA256 from `SurveyOptions.TokenKey` (the
  same token on every retry; only its SHA-256 hash is stored), then calls `ISurveyInvitationSender` with the stable `DeliveryKey`.
  Failures retry with exponential backoff up to `SurveyOptions.MaximumSendAttempts`, then fail; a dispatcher that stops mid-send leaves
  the lease to expire and the invitation becomes due again; invitations of disabled surveys are cancelled.
- `SurveyResponder` (`ISurveyResponder`): anonymous token path — strict parsing, scope-confined hash lookup, expiry, score range and
  bounded comment checks, single response with idempotent replay.
- `SurveyService` (`ISurveyService`): definitions (`survey:read` / `survey:manage` on the survey's inbox or workspace), invitation and
  response reads (`survey:read`, `survey.comment` field masking), response lists and aggregates through `IAccessQueryPlanner`
  filters (`survey:read`, `report:view`).
- `InMemorySurveyStore`: bounded, process-local development store.

```csharp
using TicketFlow.Surveys;

services.AddSingleton<ISurveyInvitationSender, MyEnvelopeFlowSender>();
services.AddTicketFlow()
    .AddAccessControl().AddInMemoryAccessStores()
    .AddSurveys(options => options.TokenKey = stableSecret) // ≥ 32 bytes, shared by all dispatcher hosts
    .AddInMemorySurveyStores(); // development only
```
