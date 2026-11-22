# T06 — Survey resolution bridge and documentation

Epic: [E6 — Channels and EnvelopeFlow integration](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Integrations.Surveys`: an E5 `IConversationOutboxHandler` turning resolved status changes into Surveys
`ConversationResolvedNotification`s (inbox, team, agent, contact locale). `docs/channels.md`.

## Dependencies

E5, E12, E6 T03.

## Acceptance criteria

- Resolving a conversation schedules a survey invitation once; replays are idempotent; other status changes are ignored.

## Verification

- `dotnet test tests/Integrations/TicketFlow.Integrations.Surveys.Tests -c Release`.

## Risks and boundaries

The notification is built only from trusted store reads.

## Results

Implemented `TicketFlow.Integrations.Surveys` (`ConversationResolvedSurveyHandler`, consumer `surveys.resolved`, `AddConversationSurveys`) and
wrote `docs/channels.md`. Tests: `ConversationResolvedSurveyHandlerTests` (3: one invitation per resolved conversation with agent/inbox
dimensions, contact locale and non-resolved changes ignored, replayed entries idempotent).

- `dotnet test tests/Integrations/TicketFlow.Integrations.Surveys.Tests -c Release` — 3 passed, 0 failed.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
