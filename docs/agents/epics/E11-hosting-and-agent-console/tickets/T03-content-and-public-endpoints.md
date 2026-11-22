# T03 — Automation, help center, surveys, assist and public endpoints

Epic: [E11 — Hosting and agent console](../PLAN.md)

Status: **Completed**

## Goal

`MapTicketFlowAutomation()` (rules with dry-run and replay, executions, macros and macro runs, canned responses with rendering),
`MapTicketFlowHelpCenter()` (portals, collections, articles with lifecycle, revisions, preview, feedback counts, agent search),
`MapTicketFlowSurveys()` (survey definitions, responses), `MapTicketFlowAssist()` (summary, draft, triage, sentiment, translate,
tone rewrite, settings, usage) and the anonymous `MapTicketFlowPublicPortal()` / `MapTicketFlowPublicSurveys()` groups (scope from
the partition resolver plus route workspace; tokens travel in request bodies, never URLs). `MapTicketFlowEndpoints()` maps every group.

## Dependencies

E11 T01.

## Acceptance criteria

- Anonymous groups carry an optional rate-limiting policy (`TicketFlowEndpointOptions.AnonymousRateLimitingPolicy`) and never
  require a principal.
- Dry-run and replay are side-effect free (service guarantees) and exposed as POST.
- Assist provider failures map to 502/503 with `Retry-After`; quota and rate limits to 429.

## Verification

- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release`.

## Risks and boundaries

Automation rules, macros, canned responses, SLA/routing policies and assist settings use their features' versioned codec JSON as
the request/response definition shape.

## Results

Implemented automation (rules, dry run, replay, executions, macros and runs, canned responses with shortcut lookup and rendering), help
center administration (portals, collections, articles with publish/unpublish/archive/restore/rollback, revisions, preview, feedback,
agent search), surveys (definitions, responses, aggregates), assist (six features with 429/502/503 and `Retry-After` mapping, settings,
usage), the anonymous public portal and survey groups (tokens in bodies) and `MapTicketFlowEndpoints()`. Tests:
`CannedResponsesUseCodecDefinitionsAndRenderPerConversation`, `HelpCenterPublishingAndAnonymousPortal`,
`PublicSurveyTokensTravelInBodiesAndUnknownTokensAreNotFound`, `PublicEndpointsRequireAResolvedPartition`,
`AssistRefusalsAndProviderFailuresMapToRetryableStatuses`.

Verification (2026-11-22):

- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release` — 18 passed, 0 failed.
