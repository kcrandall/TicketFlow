# TicketFlow.Surveys.Abstractions

Provider-neutral model and contracts for TicketFlow satisfaction surveys:

- `SurveyDefinition`: CSAT (1–5) or NPS (0–10), workspace-wide or bound to one inbox, ≤ 32 localized `SurveyQuestion`s and a
  `SurveyTriggerPolicy` (conversation resolved, delay ≤ 7 days, per-contact cooldown ≤ 365 days, invitation lifetime 1 hour–30 days).
- `SurveyInvitation`: bound to scope + survey + conversation + contact (+ inbox/team/agent at resolution), states
  Pending/Sending (leased claim)/Sent/Responded/Failed/Cancelled. `SurveyToken` (`tfs1.` + 256 bits, base64url; `Derive` makes it
  deterministic per invitation under an application secret so retries resend the same link) is never stored; stores keep only
  `SurveyTokenHash` (SHA-256).
- `SurveyResponse` (score + optional comment ≤ 2000 chars), `SurveyResponseQuery` and `SurveyAggregateQuery` (≤ 366 days, buckets
  day/week/month, grouping by inbox/team/agent, ≤ 1000 rows) with `SurveyAggregateRow.CsatPercent` / `NpsScore`.
- Unions: `SurveyResult<T>`, `SurveyWriteResult<T>`, `InvitationCreateResult`, `ResponseRecordResult`, `SurveyResponseResult`.
- `ISurveyStore` (atomic idempotent invitations with cooldown, single response, Access-filtered lists and aggregates).
- Services: `ISurveyScheduler` (consumes `ConversationResolvedNotification`), `ISurveyDispatcher`, `ISurveyInvitationSender` (the
  delivery seam the EnvelopeFlow adapter implements), `ISurveyResponder` (anonymous token path), `ISurveyService` (authorized).
- `SurveyPermissions` (`survey:read`, `survey:manage`, reuses `report:view`), `SurveyResources`, `SurveyFieldPaths.Comment`.

Behavior lives in `TicketFlow.Surveys`; durable storage in `TicketFlow.Surveys.Stores.Postgres`.
