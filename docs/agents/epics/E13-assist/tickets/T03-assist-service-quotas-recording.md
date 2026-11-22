# T03 — Assist service: features, quotas, usage accounting and timeline recording

Epic: [E13 — Assist](../PLAN.md)

Status: **Completed**

## Goal

`AssistService` (`IAssistService`) implements the six features over `IAssistChatClientFactory` (`IChatClient`) with structured JSON output validation;
`AssistAdministration` (`IAssistAdministration`) manages workspace settings and usage summaries under `assist:manage`; per-workspace
daily token quotas and per-minute request limits are enforced by atomic reservations in `IAssistStore` and settled with actual
usage; summaries, drafts and triage are recorded as `assist.summary`, `assist.draft`, `assist.triage` extension events through
`IConversationService.AppendExtensionAsync`. In-memory development store and `AddAssist()` DI.

## Dependencies

E13 T02.

## Acceptance criteria

- Quota and rate limits refuse before any model call; failed or refused calls release their reservation; durable usage records keep
  feature, model, actor, conversation and token counts only.
- Outputs are parsed and validated (bounded strings, confidences in [0, 1], triage suggestions restricted to caller candidates).
- Union switches are exhaustive without discard arms; tests cover every result case.

## Verification

- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release`.

## Risks and boundaries

Recording requires `conversation:manage` for the recording principal (the actor by default); a recording failure is reported in
the result, not as a feature failure. Reservations left by a crashed process count against the quota until the window ends.

## Results

Implemented `AssistService`, `AssistAdministration`, `AssistChatClientFactory` (option mapping, usage metrics, tool-invocation
telemetry, OpenTelemetry without sensitive data), `AssistModelRegistry`, `AssistTelemetry`, `InMemoryAssistStore`, `AddAssist()` /
`AddInMemoryAssistStores()`. Tests: `ServiceTests`, `ModelAndCompositionTests` (incl. in-memory store contract and tool spans).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release` — 24 passed, 0 failed.
- `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release` — 17 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release` — 6 passed, 0 failed.
- `dotnet pack` of the four Assist packages succeeded; `bash scripts/verify-package-metadata.sh` currently stops on an unrelated
  in-progress E10 project (`src/Automation/TicketFlow.Automation` not yet in the solution).

Post-ticket parallel reviews have not been run yet.
