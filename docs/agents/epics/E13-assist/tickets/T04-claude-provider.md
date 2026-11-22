# T04 — Claude provider over the official Anthropic .NET SDK

Epic: [E13 — Assist](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Assist.Providers.Claude`: `ClaudeAssistProvider` implements `IAssistChatProvider` with the official `Anthropic` NuGet SDK and its `IChatClient` adapter
(Messages API): cached system prompt (`cache_control: ephemeral`), structured outputs (`output_config.format` JSON schema), effort,
refusal handling, an HTTP retry handler with exponential backoff for 429/5xx/529 honoring `retry-after`, per-attempt and total
timeouts, and error mapping to `AssistProviderException`. The API key comes from options or `IConfiguration`; content is never logged.

## Dependencies

E13 T01.

## Acceptance criteria

- Tests use a fake `HttpMessageHandler` only (never the real API) and assert the request JSON shape and headers.
- 400/401/403/404/413 map to non-retryable failures; 429/5xx/529/timeouts/network failures retry and then map to retryable
  failures; `stop_reason: refusal` maps to the content-filter finish reason (`AssistRefusal.ModelRefused`); `max_tokens` maps to the length finish reason (`OutputTruncated`, final).
- No API key is hard-coded; a missing key fails fast at first use with a non-sensitive error.

## Verification

- `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release`.

## Risks and boundaries

The SDK's own retries are disabled so backoff is deterministic and testable through `TimeProvider`.

## Results

Implemented `ClaudeAssistProvider` over the official `Anthropic` SDK 13.6.0 (`AsIChatClient`), `ClaudeChatClient` (lazy key,
total timeout, error mapping), `ClaudeRetryHandler`, `ClaudeAssistOptions`, `AddClaudeAssist()` (Claude 5 models, haiku for
classification, sonnet for generation). Tests: `ClaudeProviderTests` (request JSON shape and headers, cache/effort toggles, refusal and
truncation, 400/401/403/404/413 final without retry, 429/529/500 retried with backoff, retry-after(-ms), long retry-after and
x-should-retry false, network/attempt timeouts, caller cancellation, key from options/configuration, option validation).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release` — 24 passed, 0 failed.
- `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release` — 17 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release` — 6 passed, 0 failed.
- `dotnet pack` of the four Assist packages succeeded; `bash scripts/verify-package-metadata.sh` currently stops on an unrelated
  in-progress E10 project (`src/Automation/TicketFlow.Automation` not yet in the solution).


Review fixes (2026-11-22, after commit 1742cba):

- P1: `AssistChatClientFactory` (singleton, `IDisposable`) caches pipelines per (model, feature, thinking level) and provider clients
  per provider model, and disposes both with the container; hosts can supply a keyed `IChatClient` per provider
  (`AssistChatClients.ServiceKey`) and add middleware through `AssistOptions.Pipeline`.
- P2 accounting: `AssistProviderException.TokensMayHaveBeenConsumed`; reservations are released only when nothing was provably
  consumed (client not built, not configured, provider error status) and every ambiguous outcome or missing usage is settled at the
  estimate (`AssistUsageRecord.Estimated`, `AssistUsageTotal.EstimatedTokens`); `IAssistStore.SweepReservationsAsync` +
  `AssistMaintenance.RunOnceAsync` (with `AssistOptions.ReservationTimeout`) settle abandoned reservations.
- P2 quota: PostgreSQL migration 2 adds `tf_assist_usage_days` (backfilled) and the `estimated` column; reservations charge the day
  counter with a conditional UPSERT and read only the last minute's rows; settlement adjusts the counter by actual − reserved.
- P3: registration extensions moved to `TicketFlow.Assist`, `TicketFlow.Assist.Providers.Claude` and `TicketFlow.Assist.Stores.Postgres`.
- Regression tests: pipeline reuse and disposal, keyed host client and middleware, timeout/network/cancellation after send and null
  usage charged at the estimate, provably-unsent releases, maintenance sweep, contract tests for estimated settlement, sweep and mixed
  concurrent reserve/settle, day-counter authority and v1→v2 backfill on PostgreSQL, Claude `TokensMayHaveBeenConsumed` mapping.
- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release` — 28 passed; `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release` —
  17 passed; `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release` — 9 passed (isolated
  `postgres:18` container on port 55917).

Post-ticket parallel reviews ran; the findings above are fixed.
