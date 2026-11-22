# T02 — Access- and masking-aware context builder with prompt-injection hygiene

Epic: [E13 — Assist](../PLAN.md)

Status: **Completed**

## Goal

`AssistContextBuilder` builds prompts only from data the requesting principal can see: the masked conversation view and timeline
through `IConversationService` (notes only when the timeline read included them, i.e. the principal holds `note:read`), the masked
contact through `IContactService` (skipped without `contact:read`), and help center snippets through `IHelpCenterSearch` with the
principal (falling back to public content when `helpcenter:read` is missing). Masked values become placeholders; customer content
is escaped and delimited as untrusted data; size is bounded with a newest-first truncation strategy.

## Dependencies

E13 T01.

## Acceptance criteria

- Masked attribute values, masked contact identities and masked channel identifiers never appear in a model request (asserted on
  captured provider request bodies).
- Notes are absent without `note:read`; principals without `conversation:read` or `assist:use` get `Forbidden` before any model call.
- Citations only reference articles returned by the principal's own search; unknown citation keys from the model are dropped.
- Context is bounded (`MaximumContextCharacters`, per-message truncation, oldest events dropped first with an omission marker).

## Verification

- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release`.

## Risks and boundaries

Message bodies are customer content and may themselves contain personal data the principal is allowed to see; masking applies to
masked fields, not to free text. The model output is a suggestion only and never executes actions.

## Results

Implemented `AssistContextBuilder` (conversation view + `assist:use` on the conversation resource, newest-first timeline with
notes only when included, masked contact, help center search with public fallback, `[masked]` placeholders, XML-escaped delimited
sections, character budget with truncation markers). Tests: `ContextTests` (masking against scripted transcript and decoded Claude
HTTP body, notes, authorization before any model call, team-scoped `assist:use`, citations, escaping, bounds).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
- `dotnet test tests/Assist/TicketFlow.Assist.Tests -c Release` — 24 passed, 0 failed.
- `dotnet test tests/Assist/TicketFlow.Assist.Providers.Claude.Tests -c Release` — 17 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Assist/TicketFlow.Assist.Stores.Postgres.Tests -c Release` — 6 passed, 0 failed.
- `dotnet pack` of the four Assist packages succeeded; `bash scripts/verify-package-metadata.sh` currently stops on an unrelated
  in-progress E10 project (`src/Automation/TicketFlow.Automation` not yet in the solution).

Post-ticket parallel reviews have not been run yet.
