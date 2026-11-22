# T03 — Rule engine, outbox handler, loop prevention and execution log

Epic: [E10 — Automation and macros](../PLAN.md)

Status: **Completed**

## Goal

`AutomationConditionEvaluator` (three-valued), `AutomationActionExecutor` (actions through `IConversationService` as the acting
principal; webhooks through the E6 signing and SSRF guard; SLA escalation mapping; survey trigger seam) and
`AutomationOutboxHandler` (consumer `automation`) that evaluates rules in priority order as the automation API client.

## Dependencies

E10 T02, E5 outbox, E6 `TicketFlow.Channels.Api`, E9 events.

## Acceptance criteria

- Actions never exceed the automation API client's grants (refusals are recorded per action).
- Idempotent per (rule, triggering event): redelivery and crashes never duplicate an action (deterministic message ids, resumable
  execution records).
- Loop prevention: automation-caused events carry cause metadata (cause index plus an `automation.applied` marker); rules ignore
  them unless opted in; chains stop at a maximum depth and a per-conversation rate limit.

## Verification

- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` (engine, loop, idempotency, permission, webhook tests).

## Risks and boundaries

Conditions see only what the automation client may read (masked fields are unknown).

## Results

Implemented `AutomationConditionEvaluator`, `AutomationContextBuilder`, `AutomationActionExecutor`, `AutomationRunner`, `AutomationOutboxHandler` and `SignedAutomationWebhookSender` (E6 signing + SSRF guard). Tests: logging/cause index, grant bounding, no-read client, redelivery and crash resume, opt-in, depth limit, unknown cause, rate limit, priority/stop, SLA escalation, signed webhook.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E7/E13 work in the tree).
- `dotnet test tests/Automation/TicketFlow.Automation.Tests -c Release` — 35 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55710;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Automation/TicketFlow.Automation.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e10`) — 10 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — Validated 47 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
