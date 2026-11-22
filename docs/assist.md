# Assist

AI help that never sees more than the agent can. The assistant produces **suggestions only** — summaries, reply drafts grounded in
the help center, triage, sentiment and intent, translations and tone rewrites. Nothing is applied automatically: agents (or E10
automation acting as its own API-client principal with grants) apply a suggestion through the normal conversation commands.

| Package | Contents |
| --- | --- |
| `TicketFlow.Assist.Abstractions` | `IAssistService`, `IAssistAdministration`, feature requests/results, `AssistResult<T>` unions, `IAssistChatClientFactory`, `IAssistModelRegistry`, `IAssistChatProvider`, `AssistProviderException`, `AssistPermissions`, `AssistSettings`, `IAssistStore`, `AssistJsonCodec`. Built on `Microsoft.Extensions.AI.Abstractions` (`IChatClient`). |
| `TicketFlow.Assist` | `AssistService`, `AssistContextBuilder`, `AssistAdministration`, `AssistChatClientFactory`, `AssistModelRegistry`, `AssistTelemetry`, `InMemoryAssistStore`, `AssistOptions`, `AddAssist()`. |
| `TicketFlow.Assist.Providers.Claude` | `ClaudeAssistProvider` over the official `Anthropic` .NET SDK (13.6.0) and its `IChatClient` adapter, `ClaudeAssistOptions`, `AddClaudeAssist()`. |
| `TicketFlow.Assist.Stores.Postgres` | `PostgresAssistStore`, `PostgresAssistMigrations` (component `ticketflow.assist`), `AddPostgresAssistStores()`. |

Registration extensions live in the feature namespaces (`using TicketFlow.Assist;`, `using TicketFlow.Assist.Providers.Claude;`,
`using TicketFlow.Assist.Stores.Postgres;`), like the other feature packages.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces()   /* ... */
    .AddContacts()     /* ... */
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddHelpCenter().AddPostgresHelpCenterStores(dataSource)
    .AddAssist(options => options.DefaultDailyTokenLimit = 1_000_000)
    .AddClaudeAssist()                         // API key: options, "TicketFlow:Assist:Claude:ApiKey" or "ANTHROPIC_API_KEY"
    .AddPostgresAssistStores(dataSource);      // or .AddInMemoryAssistStores() for development
await new PostgresAssistStore(dataSource).InitializeAsync();
```

## Features

| Method | Default model | Output | Timeline |
| --- | --- | --- | --- |
| `SummarizeAsync` | `claude-sonnet-5-5` | summary, key points, next step | `assist.summary` |
| `DraftReplyAsync` | `claude-sonnet-5-5` | body, citations (`ArticleId`, title, slug, revision), grounded flag | `assist.draft` |
| `TriageAsync` | `claude-haiku-5-5` | labels/team from caller candidates and priority, each with confidence in [0, 1], rationale | `assist.triage` |
| `AnalyzeSentimentAsync` | `claude-haiku-5-5` | sentiment + intent with confidences (intent optionally from a caller list) | — |
| `TranslateAsync` | `claude-sonnet-5-5` | translation of agent text or of a visible message, detected source locale | — |
| `RewriteToneAsync` | `claude-sonnet-5-5` | agent text rewritten in a tone | — |

Every call returns `AssistResult<T> = AssistSucceeded<T>(value, usage, model, usageId, recording) | AssistRefused(reason, errors,
retryAfter) | AssistProviderFailed(failure, retryable, retryAfter)`; switch over every case without a discard arm. Refusals:
`Forbidden`, `NotFound`, `Invalid`, `Disabled`, `QuotaExceeded`, `RateLimited`, `ModelRefused` (the provider's safety system
declined), `Conflict` (administration). Provider failures are `AssistProviderFailure` values; malformed or out-of-bounds model output
is `InvalidResponse` (retryable) and token-limit truncation is `OutputTruncated` (final).

## Context: only what the principal can see

`AssistContextBuilder` builds every prompt with the requesting principal:

1. `IConversationService.GetAsync` — requires `conversation:read`; masking is applied server-side. Then `assist:use` is authorized
   on the conversation's Access resource (team, inbox, assignee, labels and the condition form of **unmasked** attributes), so team or
   inbox grants and conditions apply. Missing conversations and other scopes are `NotFound`; denials are `Forbidden`; both happen
   before any model call or quota use.
2. `IConversationService.ReadTimelineAsync` — the newest `MaximumTimelineEvents` events. Notes appear only when the read included
   them (the principal holds `note:read`); they are rendered as internal notes the draft must not reveal.
3. `IContactService.GetAsync` — omitted without `contact:read`. Identities are excluded unless `IncludeContactIdentities` is set.
4. `IHelpCenterSearch.SearchAsync` with the principal for drafts with `AssistGrounding` (portal, locale, optional query; default
   query is the latest customer message). Without `helpcenter:read` it falls back to `SearchPublicAsync`, i.e. public published
   content only. Snippets are offered as keys `A1..An`; citations returned by the model are mapped back to those snippets and any
   unknown key is dropped, so a draft can only cite articles the principal's own search returned.

Masked values never reach the model: masked conversation and contact attributes, masked identities and masked channel identifiers
are removed by the owning services and rendered as `[masked]`. Assist never reveals fields. Message bodies are customer content
the principal is allowed to see; masking applies to masked fields, not to free text.

**Prompt-injection hygiene.** The system prompt is fixed per feature (and cacheable). Variable data goes into the user message as
`<task>…</task><support_data>…</support_data>`; every value inside is XML-escaped, so customer content cannot close or open
sections, and the system prompt instructs the model to treat everything in `<support_data>` as untrusted data, never to follow
instructions in it and never to guess `[masked]` values. Agent instructions are delimited as `<agent_instructions>`. Outputs are
constrained by a JSON schema (triage labels/teams and sentiment/intent values are `enum`-constrained to the caller's candidates),
then parsed and validated (bounded strings, confidences in [0, 1], candidates only). The model has no tools and never executes
actions.

**Bounds.** `MaximumContextCharacters` (default 60,000) bounds rendered data: the conversation header, contact and snippets come
first, then timeline events newest-first until the budget is spent (older events are dropped and `earlier_events_omitted="true"`
is set); single bodies are cut at `MaximumMessageCharacters` (default 4,000) with `…[truncated]`.

## Models, providers and telemetry

The AI layer follows the `Microsoft.Extensions.AI` pattern: an `IAssistModelRegistry` of `AssistModelDefinition`s (TicketFlow id,
provider name, provider model, capabilities, default thinking level), `AssistOptions.FeatureModels` defaults, and a singleton
`IAssistChatClientFactory`. The base `IChatClient` of a provider is the host's keyed registration
`services.AddKeyedSingleton<IChatClient>(AssistChatClients.ServiceKey("claude"), ...)` when present (the model is chosen per request
through `ChatOptions.ModelId`; the host owns and disposes it), otherwise one client per provider model created by the
`IAssistChatProvider` and owned by the factory. Pipelines are built once per (model, feature, thinking level) — a bounded set — and
reused; the factory is disposed with the container and disposes its pipelines (including the OpenTelemetry meters/sources) and the
provider-created clients. Pipeline order:

1. option mapping — `ModelId`, thinking level → `ChatOptions.Reasoning`, then `IAssistChatProvider.ApplyOptions` for provider
   specifics;
2. host middleware from `AssistOptions.Pipeline` (`(builder, model, request) => builder.Use…(…)`, e.g. caching or rate limiting);
3. usage metrics (`TicketFlow.Assist` meter: requests, input/output tokens, tool calls, duration; tags feature/model/provider/outcome);
4. tool-invocation telemetry (`UseFunctionInvocation` with a span and log scope per tool call — name, call id, success, duration,
   result type; never arguments or results) for callers that pass `AIFunction` tools;
5. OpenTelemetry (`TicketFlow.Assist` activity source) with `EnableSensitiveData = false`.

Workspace settings may override the model per feature; the model must be registered. Other providers implement
`IAssistChatProvider` and surface failures as `AssistProviderException`.

### Claude provider

- Official `Anthropic` SDK 13.6.0 (latest stable release older than two weeks) via `AnthropicClient.AsIChatClient(model)`.
- `ApplyOptions` moves `ChatOptions.Instructions` into a system block with `cache_control: {"type":"ephemeral"}` (disable with
  `CacheSystemPrompt = false`) and maps the thinking level to `output_config.effort`; the SDK adapter maps JSON-schema response
  formats to `output_config.format` and sends adaptive thinking. Short system prompts below the model's minimum cacheable prefix
  are simply not cached.
- Defaults: `claude-haiku-5-5` for triage and sentiment (`ClassificationModel`), `claude-sonnet-5-5` for everything else
  (`GenerationModel`); `claude-opus-5-5` is registered for workspaces that opt in.
- `ClaudeRetryHandler` owns retries (SDK retries are off): 408/409/429/5xx/529, attempt timeouts and network failures, exponential
  backoff with jitter (`InitialBackoff` 1 s doubling to `MaximumBackoff` 30 s, `MaxRetries` 3), honoring `retry-after-ms`,
  `retry-after` (seconds or date; longer than `MaximumRetryAfter` 60 s is not waited for) and `x-should-retry`. `AttemptTimeout`
  (90 s) and `TotalTimeout` (4 min) bound each call. Delays use `TimeProvider`.
- Error mapping: 400/422 `InvalidRequest`, 401 `Authentication`, 403 `PermissionDenied`, 404 `NotFound`, 413 `RequestTooLarge` (all
  final); 429 `RateLimited`, 529/503 `Overloaded`, other 5xx `ServerError`, timeouts `Timeout`, transport `Network` (retryable);
  `stop_reason: refusal` → `ModelRefused`; `max_tokens` → `OutputTruncated`. Exception messages never include provider bodies.
- The API key comes from `ClaudeAssistOptions.ApiKey` or `IConfiguration` (`TicketFlow:Assist:Claude:ApiKey`, `ANTHROPIC_API_KEY`).
  A missing key fails the call with `NotConfigured`; nothing is hard-coded and nothing is logged. Server-side refusal fallbacks are
  not enabled: a refusal is reported to the caller as `ModelRefused`.

## Timeline recording

Summaries, drafts and triage append an `ExtensionEvent` through `IConversationService.AppendExtensionAsync` as the requesting
principal (which therefore needs `conversation:manage`): kinds `assist.summary`, `assist.draft`, `assist.triage`, payload version 1:

```json
{"feature":2,"model":"claude-sonnet-5-5","usage_id":"…","usage":{"in":812,"out":164,"cache_read":0,"cache_write":0},"citations":["refunds"]}
```

Payloads never contain prompts, customer content or output text. `AssistSucceeded.Recording` reports `Recorded`, `Forbidden` (no
`conversation:manage`), `Failed` or `NotApplicable` (other features, or `RecordTimelineEvents = false`); a recording failure never
fails the suggestion.

## Permissions, quotas and usage

- `assist:use` — request suggestions (checked on the conversation). `assist:manage` (sensitive) — read/update workspace settings and
  usage reports. Both are registered by `AddAssist()`; built-in Owner holds them, other roles get them through custom roles.
- `AssistSettings` per workspace (stored, revisioned; defaults from `AssistOptions`): `Enabled`, `DailyTokenLimit` (UTC day),
  `RequestsPerMinute`, per-feature model overrides. `IAssistAdministration.UpdateSettingsAsync(actor, settings, expectedRevision)` —
  0 creates.
- Before each model call the service reserves an estimate (`ceil(prompt characters / 3) + max output tokens`) with
  `IAssistStore.ReserveAsync`, which atomically checks the day's counted tokens and the last minute's requests. Exceeded limits are
  refused with `RetryAfter` before the model is called.
- Settlement rule: actual provider usage (input, output, cache reads, cache writes) when reported. The reservation is released
  (Failed, zero) **only** when the provider provably consumed nothing — the client could not be built, the request was never sent
  (`NotConfigured`) or the provider answered with an error status (`AssistProviderException.TokensMayHaveBeenConsumed == false`).
  Every ambiguous outcome — timeouts, network errors or caller cancellation after sending, unknown exceptions, unreadable success
  responses, or a success without usage — is settled **at the reserved estimate** (`AssistUsageRecord.Estimated`), so quotas are never
  under-charged.
- `AssistMaintenance.RunOnceAsync()` (scoped; host it in a worker such as the E11 worker) settles reservations older than
  `AssistOptions.ReservationTimeout` (default 15 minutes) — left by a crashed process — as Failed at their estimate via
  `IAssistStore.SweepReservationsAsync` (any scope, bounded batches, safe on several nodes).
- Usage records hold feature, model, actor kind/id, conversation id, state and token counts — never prompts or outputs.
  `GetUsageAsync(actor, from, to)` (≤ 92 days) aggregates per feature and model; `PurgeUsageAsync` deletes settled records in
  bounded batches.

## Stores

| Contract | Guarantees |
| --- | --- |
| `IAssistStore` settings | Scope-confined; compare-and-set on revision (0 creates). |
| `IAssistStore` usage | Reservations serialize per workspace so limits cannot be raced; settle exactly once (actual or estimated); sweeps settle abandoned reservations at their estimate; summaries ordered by feature then model (with reserved and estimated tokens); purge never removes reserved records. |

`PostgresAssistStore` tables `tf_assist_settings` (versioned JSON `{"v":1,…}`), `tf_assist_usage` (numeric feature, actor kind and
state discriminators, `format_version`, `estimated`) and — migration 2 — `tf_assist_usage_days`, a per-workspace, per-UTC-day counter
of counted tokens (backfilled from existing usage). A reservation takes the workspace's transaction-scoped advisory lock, reads at
most `RequestsPerMinute` rows of the last minute, and charges the day counter with a conditional UPSERT
(`tokens = tokens + n` only while within the limit); no check scans a day of usage. Settlement adjusts the counter by
`actual − reserved` in the same transaction; estimated settlements and sweeps leave it unchanged. `tf_key_v1` keys over the full
scope, parameterized statements, rows that fail to decode throw. `InMemoryAssistStore` is a bounded development example. The shared contract lives in `tests/Assist/TicketFlow.Assist.StoreContracts`.

## Testing

Tests never call the real API: the service tests use a scripted `IChatClient`, and the Claude tests use a fake
`HttpMessageHandler` that captures request bodies (asserting JSON shape, headers, that masked values and the key never appear) and
replays responses and failures; retry delays are observed through a test `TimeProvider`.
