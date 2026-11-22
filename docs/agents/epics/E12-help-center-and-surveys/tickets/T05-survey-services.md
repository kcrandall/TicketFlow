# T05 — Survey scheduling, dispatch, responses and aggregates

Epic: [E12 — Help center and surveys](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Surveys`: `SurveyScheduler` (consumes `ConversationResolvedNotification`), `SurveyDispatcher` (issues tokens and calls
`ISurveyInvitationSender`), `SurveyResponder` (anonymous token path) and the authorized `ISurveyService` (definitions, responses,
aggregates).

## Dependencies

E12 T04.

## Acceptance criteria

- Scheduling respects enabled surveys, inbox specificity, delay and cooldown; duplicate notifications create nothing new.
- Dispatch records the token hash before sending; sender failures return the invitation to pending with bounded attempts.
- Responses validate score ranges, expiry and single use; duplicates are idempotent.
- Definitions require `survey:manage`/`survey:read`; responses use `survey:read` with planner filters and comment masking;
  aggregates use `report:view` filters.

## Verification

- `dotnet test tests/Surveys/TicketFlow.Surveys.Tests -c Release`.

## Risks and boundaries

Delivery is at-most-once if the process stops between recording the token and calling the sender.

## Results

Implemented:

- `SurveyScheduler` (`ConversationResolvedNotification` input; inbox survey wins per kind; delay; idempotent; cooldown),
  `SurveyDispatcher` (token issue → hash recorded → `ISurveyInvitationSender`; backoff retries; Failed after
  `SurveyOptions.MaximumSendAttempts`; cancels disabled surveys), `SurveyResponder` (strict token parsing, scope-confined lookup,
  expiry, score/comment validation, idempotent single response), `SurveyService` (definitions on inbox/workspace resources, masked
  response reads, planner-filtered lists and `report:view` aggregates), `AddSurveys()` / `AddInMemorySurveyStores()`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E5 projects).
- `bash scripts/verify-package-metadata.sh` — Validated 22 package projects and central versions.
- `dotnet test tests/Surveys/TicketFlow.Surveys.Tests -c Release` — 24 passed, 0 failed (flow, retry/fail, cancellation,
  authorization, masking, aggregates, model, composition).

Post-ticket parallel reviews have not been run yet.


### Review fixes (2026-11-22, after commit 18d5fce)

- Dispatch now claims before sending: `ISurveyStore.ClaimDueInvitationsAsync` moves due invitations to the new `Sending` state with a
  lease (`SurveyOptions.SendLease`) and one more attempt; concurrent dispatchers never claim the same invitation. Tests:
  `ConcurrentDispatchersSendEachInvitationOnce`, `SurveyStoreContract.VerifyClaimsAsync`.
- Stable tokens: tokens are derived with HMAC-SHA256 from `SurveyOptions.TokenKey` per (scope, invitation) (`SurveyToken.Derive`); the
  hash, issue time and expiry are recorded on first issue and kept across retries, so a link already delivered (provider accepted,
  sender threw) keeps working; `SurveyInvitationMessage.DeliveryKey` (`tfs-<guid>`) is stable for provider idempotency. Only hashes are
  stored. Tests: `AcceptedThenFailingSendKeepsTheDeliveredLinkValid`, `ResponsesAreAcceptedWhileARetryIsPending`,
  `DerivedTokensAreStablePerInvitationAndKeyed`.
- Crash window closed: a dispatcher stopping after the claim (before or after the sender call) leaves the lease to expire, after which the
  invitation is due again with the same token. Test: `DispatcherStoppingMidSendLeavesTheInvitationDueAfterTheLease`.
- Refusal semantics aligned with `ConversationService.ListAsync`: aggregates with a deny-all plan return an empty result (as response
  lists already did) instead of `Forbidden`; response pages check store scope and decide masking per row with
  `IAccessEvaluator.LoadDecisionContextAsync` (one access snapshot per page).
- `AddSurveys()` / `AddInMemorySurveyStores()` moved to namespace `TicketFlow.Surveys`.
- Outside this epic: `EnvelopeFlowSurveyInvitationSender` (E6) now passes `message.DeliveryKey` as the idempotency key instead of
  `tfs-<guid>-<attempts>`.

Verification (2026-11-22): `dotnet build TicketFlow.slnx -c Release` — 0 warnings, 0 errors; `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Tests -c Release` — 68 passed; `dotnet test tests/Surveys/TicketFlow.Surveys.Tests -c Release` — 30 passed; `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Stores.Postgres.Tests -c Release` — 11 passed and `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Surveys/TicketFlow.Surveys.Stores.Postgres.Tests -c Release` — 13 passed (isolated `postgres:18` on port 55613); `dotnet test tests/Integrations/TicketFlow.Integrations.Surveys.Tests -c Release` — 3 passed; `dotnet test tests/Integrations/TicketFlow.Integrations.EnvelopeFlow.Tests -c Release` — 9 passed.
