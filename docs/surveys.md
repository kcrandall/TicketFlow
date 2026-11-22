# Surveys

CSAT and NPS satisfaction surveys ship as three packages:

| Package | Contents |
| --- | --- |
| `TicketFlow.Surveys.Abstractions` | Definitions, trigger policies, invitations, tokens, responses, reporting queries, unions, `ISurveyStore`, service and delivery contracts, `SurveyPermissions`. |
| `TicketFlow.Surveys` | `SurveyScheduler`, `SurveyDispatcher`, `SurveyResponder`, `SurveyService`, `InMemorySurveyStore`, `SurveyOptions`, DI. |
| `TicketFlow.Surveys.Stores.Postgres` | `PostgresSurveyStore`, `PostgresSurveyMigrations` (component `ticketflow.surveys`). |

```csharp
services.AddSingleton<ISurveyInvitationSender, MyEnvelopeFlowSender>();   // supplied by the E6/EnvelopeFlow adapter
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddSurveys(options => options.MaximumSendAttempts = 5)
    .AddPostgresSurveyStores(dataSource);      // or .AddInMemorySurveyStores() for development
await new PostgresSurveyStore(dataSource).InitializeAsync();
```

## Model

- **SurveyDefinition** — `SurveyKind.Csat` (1–5; 4–5 satisfied) or `SurveyKind.Nps` (0–10; 9–10 promoters, 7–8 passives, 0–6
  detractors), workspace-wide or bound to one inbox (opaque `InboxId`), up to 32 localized `SurveyQuestion`s (question ≤ 512,
  optional comment prompt ≤ 256; one must use the default locale), an enabled flag and a `SurveyTriggerPolicy`: trigger
  `ConversationResolved`, delay 0–7 days, per-contact cooldown 0–365 days, invitation lifetime 1 hour–30 days (whole seconds). At
  most 64 surveys per workspace; a survey with invitations cannot be deleted (disable it instead) and its kind cannot change.
- **SurveyInvitation** — bound to scope + survey + conversation + contact, carrying the conversation's inbox, team and agent at
  resolution (the reporting and access dimensions) and the locale. States: Pending → Sent → Responded, or Failed / Cancelled.
- **Tokens** — `SurveyToken` is `tfs1.` + 43 base64url characters (256 random bits from `RandomNumberGenerator`). Raw tokens exist
  only in memory between issue and delivery and in the respondent's link; stores persist only `SurveyTokenHash` (SHA-256, unique) and
  look tokens up within the scope. Tokens are re-issued on retry, so a failed delivery's token stops working.
- **SurveyResponse** — score on the survey's scale plus an optional comment (≤ 2000 characters, line breaks allowed; customer
  content, never logged), with the invitation's dimensions copied. One response per invitation.

## Flow

1. **Schedule** — `ISurveyScheduler.HandleResolvedAsync(ConversationResolvedNotification)`. The notification (scope, conversation,
   contact, resolved-at, optional inbox/team/agent/locale) is a trusted internal input that the E5 conversation outbox will feed; it
   must never be built from end-user requests. For each kind the enabled survey bound to the conversation's inbox wins over the
   enabled workspace survey. Invitations are created Pending with `SendAfter = resolvedAt + delay`; creation is idempotent per
   (survey, conversation) (`Duplicate`) and refused within the contact's cooldown (`CoolingDown`), atomically in the store.
2. **Dispatch** — `ISurveyDispatcher.DispatchDueAsync(scope, limit ≤ 500)` reads due invitations, issues a token, records its hash
   (state Sent, `ExpiresAt = now + lifetime`) with a revision-fenced update and calls `ISurveyInvitationSender.SendAsync` with the
   token, localized question and scale. If the sender throws, the invitation returns to Pending without a token and retries with
   exponential backoff (`SurveyOptions.RetryDelay`, capped by `MaximumRetryDelay`) until `MaximumSendAttempts`, then Failed.
   Invitations of disabled surveys are Cancelled. Delivery is at-most-once if the process stops between recording the hash and
   sending; concurrent dispatchers are safe (only one wins the fenced update). Hosts run the dispatcher per workspace scope.
3. **Respond** — `ISurveyResponder` (anonymous, host-resolved scope): `GetInvitationAsync(scope, token)` returns the localized
   question for a sent, unexpired token; `RespondAsync(scope, token, score, comment)` validates the token strictly (malformed,
   unknown, other-scope and unsent tokens are all `InvalidToken`), expiry, score range and comment, then records the response and
   marks the invitation Responded in one atomic store write. Replaying the same score and comment returns
   `SurveyResponseDuplicate`; anything different after the first response is `AlreadyResponded`.

## Delivery seam

```csharp
public interface ISurveyInvitationSender
{
    Task SendAsync(SurveyInvitationMessage message, CancellationToken cancellationToken = default);
}
```

`SurveyInvitationMessage` carries the scope, invitation (conversation, contact, inbox, locale), raw token, kind, scale and question.
The adapter resolves the contact's address and template itself, must not log the token or address, and throws on failure. TicketFlow
ships no transport.

## Access and reporting

`AddSurveys()` registers `survey:read` and `survey:manage`; aggregates reuse the built-in `report:view`.

| Operation | Permission and resource |
| --- | --- |
| Read / list definitions | `survey:read` on the survey's inbox (workspace for workspace surveys); lists filter the bounded definition set with the planned filter |
| Create / update / delete definitions | `survey:manage` on the survey's inbox or workspace; moving between inboxes needs both |
| Read invitations and responses | `survey:read` on `SurveyResources.Response` (scope, inbox, team, agent as assignee) |
| List responses | `IAccessQueryPlanner` filter for `survey:read`, pushed into the store |
| Aggregates | `IAccessQueryPlanner` filter for `report:view`, pushed into the store; deny-all is `Forbidden` |

Response labels are unknown and attributes absent, so label/attribute conditions fail closed. A field policy on `survey.comment`
(`SurveyFieldPaths.Comment`) for `survey:read` masks comments in `SurveyResponseView` (`Comment = null`, `CommentMasked`,
`CommentRevealable`).

`SurveyResponseQuery` lists responses newest first (keyset on responded-at, invitation; ≤ 366 days; ≤ 200 per page).
`SurveyAggregateQuery(kind, from, to, bucket, groupBy, survey)` groups by UTC day / ISO week (Monday) / month or the whole range and
optionally by inbox, team or agent, returning at most 1000 rows (`Truncated` when more existed). Each `SurveyAggregateRow` has
responses, positive / neutral / negative counts, score sum, `CsatPercent` (satisfied ÷ responses × 100), `NpsScore`
((promoters − detractors) ÷ responses × 100) and `AverageScore`.

## Store contract

`ISurveyStore` documents scope confinement, revision fencing, capacity, atomic idempotent invitation creation with a race-free
cooldown, fixed invitation identity fields, unique token hashes, the atomic single response and Access-filtered lists and aggregates.
The shared fixture `tests/Surveys/TicketFlow.Surveys.StoreContracts` verifies them against the in-memory store and PostgreSQL (two
independent data sources), including concurrent cooldown and response races and randomized grant parity
(`AccessFilteredResponseParity`: store lists equal point checks). The PostgreSQL store serializes invitation creates per
(survey, contact) on a transaction-scoped advisory lock, locks invitation rows for updates and responses, translates filters with
`PostgresAccessFilterTranslator` (untranslatable filters return no rows) and uses `tf_key_v1` keys with raw-column comparison.
