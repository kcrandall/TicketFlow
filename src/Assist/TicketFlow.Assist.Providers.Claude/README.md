# TicketFlow.Assist.Providers.Claude

Claude provider for TicketFlow Assist, built on the official `Anthropic` .NET SDK and its `Microsoft.Extensions.AI` `IChatClient`
adapter.

- Cached system prompts (`cache_control: ephemeral`), JSON-schema structured outputs (`output_config.format`), thinking level →
  `output_config.effort`.
- `ClaudeRetryHandler`: retries 408/409/429/5xx/529, attempt timeouts and network failures with exponential backoff and jitter,
  honoring `retry-after-ms`, `retry-after` and `x-should-retry`; SDK retries are disabled. A total timeout bounds each call.
- Provider failures map to `AssistProviderException` (`AssistProviderFailure`, retryable flag, status code). No content is logged.
- Defaults: `claude-haiku-5-5` for triage and sentiment, `claude-sonnet-5-5` for summary, draft, translation and tone; `claude-opus-5-5`
  is registered for workspaces that opt in.

```csharp
using TicketFlow.Assist.Providers.Claude;

builder.AddAssist().AddClaudeAssist(options => options.GenerationModel = "claude-sonnet-5-5");
// API key: options.ApiKey, configuration "TicketFlow:Assist:Claude:ApiKey" or "ANTHROPIC_API_KEY". Never hard-code it.
```
