# T01 — Automation model, codecs, templates, schedules and permissions

Epic: [E10 — Automation and macros](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Automation.Abstractions` with `AutomationRule` (the `AutomationTrigger` union, the closed `AutomationCondition` AST,
ordered `AutomationAction` union, priority and loop options), `AutomationSchedule` (five-field cron with an IANA/Windows time zone),
`Macro` (personal or shared ordered actions), `CannedResponse` (shortcut plus per-locale templates), the safe `AutomationTemplate`
language, execution records with the `AutomationActionOutcome` union, versioned codecs and `AutomationPermissions`.

## Dependencies

E2 (Access), E3 (calendars, inboxes), E4 (contacts), E5 (conversations), E9 (`SlaAction`, `SlaEventCodec`).

## Acceptance criteria

- Bounded, validated model: ≤ 256 rules per workspace, ≤ 16 actions per rule/macro, condition depth ≤ 8 and ≤ 64 nodes, regex
  patterns ≤ 512 characters with a 1–1000 ms timeout, templates ≤ 16 KiB with ≤ 64 variables and output ≤ 64 KiB.
- Codecs round-trip every value with explicit `"v"` and discriminators and reject unknown versions, kinds and properties.
- Cron schedules compute the next fire instant in the rule's time zone with documented DST behavior (gap → transition instant,
  overlap → first occurrence).
- Templates substitute only whitelisted variables, never execute code, and render masked or missing values as the fallback.

## Verification

- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` (model, codec, schedule and template tests).

## Risks and boundaries

Webhook destinations are referenced by opaque ids resolved by the application; rules never store secrets or URLs.

## Results

Implemented the model (`AutomationRule`, trigger union, closed condition AST, action union, `AutomationSchedule`, `Macro`, `CannedResponse`, `AutomationTemplate`, `AutomationEvent`, `AutomationExecution` + outcome union), `AutomationCodec`/`AutomationEventCodec` and `AutomationPermissions`. Tests: `AutomationModelTests` (codec round trips and strictness, bounds, Kleene evaluation and masking, regex timeout, business hours, template safety/bounds/escaping, cron parsing, DST gap/overlap).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E7/E13 work in the tree).
- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 35 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55710;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10`) — 10 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — Validated 47 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
