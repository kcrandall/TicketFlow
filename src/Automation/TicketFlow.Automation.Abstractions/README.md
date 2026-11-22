# TicketFlow.Automation.Abstractions

Contracts for TicketFlow automation, macros and canned responses.

- `AutomationRule`: the `AutomationTrigger` union (conversation created, message received/sent, conversation updated by field,
  status changed, assignment changed, SLA warning/breach, cron `ScheduledTrigger` with a time zone, contact updated), the closed
  three-valued `AutomationCondition` AST (status, priority, inbox, team, assignee, labels, channel kind, attributes, message content
  with bounded regex timeouts, business hours, elapsed time; and/or/not; depth ≤ 8, ≤ 64 nodes) and the ordered `AutomationAction`
  union (assign user/team/routing, labels, priority, status, snooze, attribute, canned reply, note, signed webhook, survey, SLA
  escalation).
- `AutomationSchedule` (bounded five-field cron, DST-correct), `Macro` (personal/shared), `CannedResponse` (shortcut + per-locale
  `AutomationTemplate`s: whitelisted `{{variables}}`, no code, bounded output, masked values never substituted).
- `AutomationExecution` with the `AutomationActionOutcome` union, `AutomationCause` (loop prevention), `AutomationCodec` and
  `AutomationEventCodec` (versioned JSON with explicit discriminators).
- `AutomationPermissions` (`automation:read`, `automation:manage` (sensitive), `macro:run`, `macro:manage`,
  `canned-response:manage`), store contracts (`IAutomationRuleStore`, `IAutomationExecutionStore`, `IMacroStore`,
  `ICannedResponseStore`), services (`IAutomationRuleService` with dry-run and replay, `IMacroService`, `ICannedResponseService`)
  and integration seams (webhook sender/destination resolver, survey trigger, agent directory).
