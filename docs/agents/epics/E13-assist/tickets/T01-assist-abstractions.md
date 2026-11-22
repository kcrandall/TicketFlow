# T01 — Assist abstractions: model contract, features, unions, permissions and store contract

Epic: [E13 — Assist](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Assist.Abstractions` on `Microsoft.Extensions.AI` (`IChatClient`): a model registry, a chat client factory and a
provider contract (option mapping for system prompt caching, JSON output schema, token limit, thinking level) with provider failures
as `AssistProviderException`; feature requests and results for ConversationSummary, ReplyDraft
(citations), Triage, SentimentAndIntent, Translate and ToneRewrite returned as `AssistResult<T> = AssistSucceeded<T> | AssistRefused
| AssistProviderFailed`; `assist:use` / `assist:manage`; workspace settings (models, quotas, rate limits) and the `IAssistStore`
contract for revisioned settings and durable usage records; versioned timeline payload codec for `assist.*` extension events.

## Dependencies

E2, E5 (conversation timeline and `ExtensionEvent`), E12 (`ArticleId`, `IHelpCenterSearch`).

## Acceptance criteria

- Every closed outcome is a C# `union` (feature result, admin result, usage reservation, store write result); persisted shapes use
  explicit versioned discriminators (settings JSON `{"v":1,...}`, numeric feature/state columns).
- Inputs are bounded and validated (`Utf16.IsValid`, lengths, token limits, quota ranges); request/result records redact content in
  `ToString()`.
- Timeline payloads carry feature, model, usage and article identifiers only — never prompts, customer content or output text.

## Verification

- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release` (model/codec tests).

## Risks and boundaries

No provider types leak into abstractions. Persisted unions are forbidden; codecs own the shape.

## Results

Implemented (refactored per user direction onto `Microsoft.Extensions.AI` instead of a bespoke model interface):

- `TicketFlow.Assist.Abstractions`: `IAssistChatClientFactory`, `IAssistModelRegistry`, `AssistModelDefinition`, `IAssistChatProvider`,
  `AssistChatRequest`, `AssistThinkingLevel`, `AssistProviderException`/`AssistProviderFailure`, `AssistUsage.From(UsageDetails)`;
  feature requests/results; `AssistResult<T>` and `AssistAdminResult<T>` unions; `AssistPermissions` (`assist:use`,
  `assist:manage` sensitive); `AssistSettings`, `AssistUsageRecord`, `AssistReservationResult`, `AssistWriteResult<T>`,
  `IAssistStore`; `AssistJsonCodec` (settings `{"v":1,...}`, timeline payload version 1) and `AssistTimelineKinds`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release` — 24 passed, 0 failed.
- `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release` — 17 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release` — 6 passed, 0 failed.
- `dotnet pack` of the four Assist packages succeeded; `bash scripts/verify-package-metadata.sh` currently stops on an unrelated
  in-progress E10 project (`src/Automation/TicketFlow.Automation` not yet in the solution).

Post-ticket parallel reviews have not been run yet.
