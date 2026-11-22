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
