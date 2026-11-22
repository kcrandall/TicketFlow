# T04 — Survey model, invitations, store contract and in-memory store

Epic: [E12 — Help center and surveys](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Surveys.Abstractions`: CSAT (1–5) and NPS (0–10) survey definitions per workspace or inbox with trigger policy
(conversation resolved, delay, per-contact cooldown, invitation lifetime) and per-locale question text; invitations bound to
conversation + contact + scope with hashed unguessable tokens; responses; aggregate queries; `ISurveyStore` contract; in-memory
store; shared `TicketFlow.Surveys.StoreContracts` fixture; `SurveyPermissions` (`survey:read|manage`, reuses `report:view`).

## Dependencies

E2, E3.

## Acceptance criteria

- Invitation creation is atomic and idempotent per (survey, conversation) and refuses within the per-contact cooldown.
- Only the SHA-256 hash of a token is stored; tokens are 256-bit random values.
- One response per invitation, recorded atomically; aggregates are bounded (≤ 366 days, ≤ 1000 rows) and Access-filtered.

## Verification

- `dotnet test tests/Surveys/TicketFlow.Surveys.Tests -c Release`.

## Risks and boundaries

In-memory stores are bounded, process-local development examples with no durability.

## Results

Implemented:

- `TicketFlow.Surveys.Abstractions`: `SurveyDefinition` (CSAT/NPS, inbox or workspace, localized questions, `SurveyTriggerPolicy`),
  `SurveyInvitation` (states, attempts, token hash, expiry), `SurveyToken` (256-bit, `tfs1.` base64url) / `SurveyTokenHash` (SHA-256),
  `SurveyResponse`, `SurveyResponseQuery`, `SurveyAggregateQuery`/`SurveyAggregateRow` (CSAT %, NPS), unions, `ISurveyStore`,
  `SurveyPermissions` (`survey:read|manage`, reuses `report:view`), `SurveyResources`, `SurveyFieldPaths.Comment`, `SurveyJsonCodec`.
- `InMemorySurveyStore`; shared fixture `tests/Surveys/TicketFlow.Surveys.StoreContracts` (`SurveyStoreContract`,
  `AccessFilteredResponseParity` with randomized grants).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E5 projects).
- `bash scripts/verify-package-metadata.sh` — Validated 22 package projects and central versions.
- `dotnet test tests/Surveys/TicketFlow.Surveys.Tests -c Release` — 24 passed, 0 failed.

Post-ticket parallel reviews have not been run yet.
