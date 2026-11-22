# TicketFlow.Assist.Abstractions

Provider-neutral assistant contracts for TicketFlow, built on `Microsoft.Extensions.AI` (`IChatClient`):

- `IAssistChatClientFactory`, `IAssistModelRegistry`, `AssistModelDefinition` and `IAssistChatProvider` — a model registry plus a
  factory that wraps provider clients in a telemetry/option-mapping pipeline.
- `IAssistService` features — conversation summary, help-center-grounded reply draft with citations, triage (labels, team, priority
  with confidence), sentiment and intent, translation and tone rewrite — returning
  `AssistResult<T> = AssistSucceeded<T> | AssistRefused | AssistProviderFailed`.
- `AssistPermissions` (`assist:use`, `assist:manage`), workspace `AssistSettings` (models, daily token quota, per-minute limit),
  `IAssistStore` (revisioned settings, atomic quota reservations, durable usage records) and `AssistJsonCodec`.

The assistant only suggests; applying a suggestion goes through the normal conversation commands. See `docs/assist.md`.
