# Surveys

CSAT and NPS satisfaction surveys ship as three packages:

| Package | Contents |
| --- | --- |
| `TicketFlow.Surveys.Abstractions` | Definitions, trigger policies, invitations, tokens, responses, reporting queries, unions, `ISurveyStore`, service and delivery contracts, `SurveyPermissions`. |
| `TicketFlow.Surveys` | `SurveyScheduler`, `SurveyDispatcher`, `SurveyResponder`, `SurveyService`, `InMemorySurveyStore`, `SurveyOptions`, DI. |
| `TicketFlow.Surveys.Stores.Postgres` | `PostgresSurveyStore`, `PostgresSurveyMigrations` (component `ticketflow.surveys`). |

```csharp
using TicketFlow.Surveys;
using TicketFlow.Surveys.Stores.Postgres;

services.AddSingleton<ISurveyInvitationSender, MyEnvelopeFlowSender>();   // supplied by the E6/EnvelopeFlow adapter
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddSurveys(options => options.TokenKey = secrets.SurveyTokenKey)   // stable ≥ 32-byte secret shared by all dispatcher hosts
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
  resolution (the reporting and access dimensions) and the locale. States: Pending → Sending (claimed, with a lease) → Sent →
  Responded, or back to Pending for a retry, or Failed / Cancelled.
- **Tokens** — `SurveyToken` is `tfs1.` + 43 base64url characters (256 bits). Dispatch derives each invitation's token
  deterministically: `HMAC-SHA256(SurveyOptions.TokenKey, ["tf-survey-token-v1", scope…, invitation])`. Stores persist only
  `SurveyTokenHash` (SHA-256, unique) and look tokens up within the scope; the raw token is never stored, and the database alone cannot
  reproduce it. Every retry re-derives the **same** token, so a link that already reached the contact stays valid. The token (hash,
  issue time, expiry = first issue + lifetime) is recorded on first issue and kept across retries; responses are accepted from then on
  until expiry unless the invitation was cancelled. Configure a stable `TokenKey` in production: the default is a random per-process
  key, and a retry on another host or after a restart (or a key rotation) must issue a new token, invalidating earlier links.
- **SurveyResponse** — score on the survey's scale plus an optional comment (≤ 2000 characters, line breaks allowed; customer
  content, never logged), with the invitation's dimensions copied. One response per invitation.

## Flow

1. **Schedule** — `ISurveyScheduler.HandleResolvedAsync(ConversationResolvedNotification)`. The notification (scope, conversation,
   contact, resolved-at, optional inbox/team/agent/locale) is a trusted internal input that the E5 conversation outbox will feed; it
   must never be built from end-user requests. For each kind the enabled survey bound to the conversation's inbox wins over the
   enabled workspace survey. Invitations are created Pending with `SendAfter = resolvedAt + delay`; creation is idempotent per
   (survey, conversation) (`Duplicate`) and refused within the contact's cooldown (`CoolingDown`), atomically in the store.
2. **Dispatch** — `ISurveyDispatcher.DispatchDueAsync(scope, limit ≤ 500)`:
   - **Claim**: `ISurveyStore.ClaimDueInvitationsAsync` atomically moves due invitations (Pending with `SendAfter ≤ now`, or Sending
     whose lease expired) to Sending with `LeaseUntil = now + SurveyOptions.SendLease` and one more attempt. PostgreSQL uses
     `UPDATE … WHERE invitation_key IN (SELECT … FOR UPDATE SKIP LOCKED LIMIT n)`, so concurrent dispatchers on any number of hosts
     never claim the same invitation; the in-memory store claims under its lock.
   - **Issue and send**: the dispatcher derives the token, records its hash on first issue (revision-fenced), calls
     `ISurveyInvitationSender.SendAsync` with the token, the localized question, the scale and the stable
     `SurveyInvitationMessage.DeliveryKey` (`tfs-<invitation guid>`), then marks the invitation Sent.
   - **Failures**: a throwing sender returns the invitation to Pending with exponential backoff (`RetryDelay`, capped by
     `MaximumRetryDelay`), or Failed after `MaximumSendAttempts` claims; the token stays valid either way. If the process stops at any
     point after the claim (before or after the sender call), the lease expires and the invitation is due again; the re-send carries
     the same token and delivery key, so delivery is at-least-once and senders should pass the delivery key to the provider as its
     idempotency key (the EnvelopeFlow adapter does). Invitations of disabled or deleted surveys are Cancelled; invitations whose
     response window has passed are Failed. Hosts run the dispatcher per workspace scope; a send must finish within the lease.
3. **Respond** — `ISurveyResponder` (anonymous, host-resolved scope): `GetInvitationAsync(scope, token)` returns the localized
   question for an issued, unexpired token; `RespondAsync(scope, token, score, comment)` validates the token strictly (malformed,
   unknown, other-scope, unissued and cancelled tokens are all `InvalidToken`), expiry, score range and comment, then records the response and
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
| Aggregates | `IAccessQueryPlanner` filter for `report:view`, pushed into the store |

Like every list path (conversations, inboxes, help center), an actor without an applicable grant gets an empty page or empty
aggregate rather than a refusal. Response pages decide masking per row against one `IAccessDecisionContext`
(`IAccessEvaluator.LoadDecisionContextAsync`), without a store round trip per row.

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
independent data sources), including concurrent cooldown, claim and response races, lease expiry, and randomized grant parity
(`AccessFilteredResponseParity`: store lists equal point checks). Schema version 2 (`dispatch-leases`) adds the Sending state, `lease_until` and renames `sent_at` to `issued_at`; version-1 schemas
upgrade in place through `InitializeAsync`. The PostgreSQL store serializes invitation creates per
(survey, contact) on a transaction-scoped advisory lock, locks invitation rows for updates and responses, translates filters with
`PostgresAccessFilterTranslator` (untranslatable filters return no rows) and uses `tf_key_v1` keys with raw-column comparison.
