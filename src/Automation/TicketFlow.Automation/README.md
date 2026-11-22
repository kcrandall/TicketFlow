# TicketFlow.Automation

Automation engine for TicketFlow.

- `AddAutomation(options => options.ClientId = new ApiClientId("automation-worker"))` registers `automation:read`,
  `automation:manage` (sensitive), `macro:run`, `macro:manage`, `canned-response:manage`, the access-checked
  `IAutomationRuleService` (CRUD, dry-run, replay, execution log), `IMacroService` (runs with the agent's own permissions) and
  `ICannedResponseService` (masking-aware rendering).
- `AutomationOutboxHandler` (outbox consumer `automation`) evaluates rules in priority order as the automation API client — whose
  grants bound everything automations can do — with loop prevention (cause index, opt-in for automation-caused events, chain depth
  and per-conversation rate limits) and idempotent, resumable execution records per (rule, triggering event).
- `AutomationScheduler` (cron rules over access-filtered bounded conversation scans) and `AutomationContactFollower`
  (`ContactUpdated` rules from the durable contact change log). Hosts call `RunOnceAsync` / `CatchUpAsync` in their loops.
- `SignedAutomationWebhookSender` reuses `TicketFlow.Channels.Api` HMAC-SHA256 signatures and the SSRF destination guard.
- `AddInMemoryAutomationStores()` registers development-only, non-durable stores.
