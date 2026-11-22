# Automation, macros and canned responses

Durable trigger → condition → action rules, manual macros and reusable reply content. Conversations, the timeline and the outbox come
from E5 ([conversations](conversations.md)); SLA events from E9 ([sla](sla.md)); business calendars from E3
([workspaces](workspaces.md)); webhook signing and the SSRF guard from E6 ([channels](channels.md)). Three packages live under
`src/Automation`:

| Package | Contents |
| --- | --- |
| `TicketFlow.Automation.Abstractions` | `AutomationRule` (the `AutomationTrigger` union, the closed `AutomationCondition` AST, the ordered `AutomationAction` union), `AutomationSchedule`, `Macro`, `CannedResponse`, `AutomationTemplate`, `AutomationEvent`, `AutomationExecution` with the `AutomationActionOutcome` union, `AutomationCodec`, `AutomationEventCodec`, `AutomationPermissions`, store and service contracts, integration seams. |
| `TicketFlow.Automation` | `AutomationConditionEvaluator`, `AutomationContextBuilder`, `AutomationActionExecutor`, `AutomationRunner`, `AutomationOutboxHandler`, `AutomationScheduler`, `AutomationContactFollower`, `AutomationRuleService`, `MacroService`, `CannedResponseService`, `SignedAutomationWebhookSender`, DI (`AddAutomation`, `AddInMemoryAutomationStores`) and development in-memory stores. |
| `TicketFlow.Automation.Stores.Postgres` | `PostgresAutomationStore` (every store contract), `PostgresAutomationMigrations`, `AddPostgresAutomationStores`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddAutomation(options => options.ClientId = new ApiClientId("automation-worker"))
    .AddPostgresAutomationStores(dataSource);
services.AddSingleton<IAutomationWebhookDestinationResolver, MyWebhookDestinations>();   // optional: endpoints + keys from your secret store
await new PostgresAutomationStore(dataSource).InitializeAsync();                        // explicit schema creation

// Host loop (E11 will ship a hosted worker):
await dispatcher.RegisterAsync();                                // ConversationOutboxDispatcher; consumer "automation"
await dispatcher.RunOnceAsync();
await scheduler.RunOnceAsync();                                  // AutomationScheduler (cron rules)
await contactFollower.CatchUpAsync(scope);                       // AutomationContactFollower, per workspace scope
```

## The automation principal

Rules act as the API client `AutomationOptions.ClientId` (one `ApiClientPrincipal` per workspace scope; automation and system
principals cannot hold grants). Every action goes through `IConversationService`, so **an automation can never do more than that
client's grants allow**: grant it a custom role with exactly the permissions rules may use, for example `conversation:read`
(required — without it nothing runs), `conversation:assign`, `conversation:manage` (labels, priority, attributes and the
`automation.applied` marker), `conversation:reply`, `conversation:resolve`, `note:write` and `contact:read` (contact conditions and
template variables). A refused action is recorded as `AutomationActionRefused(Forbidden)` and the rule continues with its next
action. Conditions see only what the client may read: masked attribute values are unknown, never compared.

## Rules

`AutomationRule(id, name, trigger, condition?, actions, priority, enabled, stopProcessing, runOnAutomationEvents)` — ≤ 256 per
workspace, 1-16 actions, priority 0-10000. Enabled rules are evaluated in ascending priority, then identifier (ordinal); a matching
rule with `StopProcessing` ends evaluation for that event.

**Triggers** (`AutomationTrigger` union): `ConversationCreatedTrigger`, `MessageReceivedTrigger` (incoming, including a start
request's initial message), `MessageSentTrigger`, `ConversationUpdatedTrigger(fields)` (status, priority, assignee, team, labels,
attributes, participants; empty = any), `StatusChangedTrigger(to)`, `AssignmentChangedTrigger` (assignee or team),
`SlaEventTrigger(kinds, metrics)` (`sla.warning` / `sla.breached`, decoded with `SlaEventCodec.TryDecode` — **only when the
timeline actor is the SLA worker API client** `AutomationOptions.SlaWorkerClientId`, default `sla-worker`, which must match
`SlaOptions.WorkerClientId`; anyone with `conversation:manage` can append extension events, so the same payload from any other
principal is ignored by the engine and by replay), `ScheduledTrigger`
(cron, below) and `ContactUpdatedTrigger`. One timeline event fires a given rule at most once.

**Conditions** (`AutomationCondition`, closed, depth ≤ 8, ≤ 64 nodes): `AllOfCondition`, `AnyOfCondition`, `NegatedCondition`,
`StatusIsCondition`, `PriorityCondition` (equal / not equal / at least / at most), `InboxIsCondition`, `TeamIsCondition` (or none),
`AssigneeIsCondition` (or unassigned), `HasLabelsCondition` (conversation or contact; all / any / none), `ChannelKindIsCondition`
(inbox channel kind), `AttributeCondition` (conversation or contact attribute: exists, equal, not equal, in, contains, greater/less
than over the Access condition form), `MessageContentCondition` (contains, not contains, starts/ends with, regex), 
`BusinessHoursCondition` (the inbox's E3 calendar) and `ElapsedCondition` (time since created / last activity / waiting since /
last incoming / last outgoing). Evaluation is three-valued (Kleene) like Access conditions: missing data (no message, unreadable
contact, masked or absent attribute, no calendar) is unknown, and **a rule runs only when its condition is true**.

Regular expressions are ≤ 512 characters, culture-invariant, run with a 1-1000 ms match timeout (default 100 ms) over at most the
first 16 KiB of the body; a timeout is unknown (fail closed — negating it does not fire either).

**Actions** (`AutomationAction` union): `AssignUserAction`, `AssignTeamAction`, `AssignToRoutingAction(team?)` (sets the team, then
unassigns so the E8 routing consumer picks an agent), `AddLabelsAction`, `RemoveLabelsAction`, `SetPriorityAction`,
`SetStatusAction` (open, pending, resolved, closed), `SnoozeAction(duration)`, `SetAttributeAction(key, value?)`,
`SendReplyAction(cannedResponse)`, `AddNoteAction(template)`, `SendWebhookAction(destination)`, `TriggerSurveyAction(survey?)` and
`ExecuteSlaEscalationAction` (rules with an `SlaEventTrigger` only). Webhook, survey and SLA-escalation actions are rule-only.

`ExecuteSlaEscalationAction` maps the triggering event's `SlaAction` records: `RaisePriorityAction` sets the priority only when it is
higher than the current one, `ReassignTeamAction` sets the team; `NotifyUserAction` / `NotifyTeamAction` are left to notification
consumers (E7) — when an event carries only notify actions the outcome is `Skipped("sla-notify-delegated")`.

`TriggerSurveyAction` calls the optional `IAutomationSurveyTrigger` seam (idempotency key per execution and action); without a
registration it is skipped (`survey-unavailable`).

## Webhooks

`SendWebhookAction` references an opaque `WebhookDestinationId`; the application resolves it to an https endpoint and a 32-64 byte
key through `IAutomationWebhookDestinationResolver`, so rules never store URLs or secrets. `SignedAutomationWebhookSender` (the
default `IAutomationWebhookSender`) reuses `TicketFlow.Channels.Api`: version-one HMAC-SHA256 signatures and headers
(`ApiWebhookSignature`, `ApiWebhookHeaders`; receivers verify with `ApiWebhookSignature.Verify` or `ApiWebhookReplayGuard`), and the
`ApiWebhookDestinationPolicy` SSRF guard (https only, allowed ports, optional exact host allow-list, every resolved address public,
no redirects or proxies). The delivery id is stable per execution and action. The body carries identifiers only:
`{"v":1,"event":"automation.webhook","rule","conversation","inbox","status","priority","assignee","team","labels","trigger","sentAt"}`
— never message bodies, contact details or attribute values. 2xx is applied; anything else is `AutomationActionFailed(code)`
(`destination-missing`, `endpoint-blocked`, `connection-failed`, `timeout`, `transport-error`, `http-<status>`), not retried.

## Engine, idempotency and loop prevention

`AutomationOutboxHandler` (consumer `automation`) handles every change except delivery updates and deletions. It takes the
workspace's enabled event rules from `AutomationRuleCache` (decoded rule sets cached per scope and revalidated with one
`GetRuleSetVersionAsync` read; the store bumps the version on every rule write), reads the change's appended events with one
`ReadTimelineAsync` (trusted store, for actor and payload; `Started` also raises `ConversationCreated`) and runs `AutomationRunner`
with the trigger key `evt:<sequence>`. Per occurrence the runner fetches every firing rule's execution record with one
`FindManyAsync` and builds the evaluation context once, rebuilding it only after a rule ran actions — so many non-matching rules cost a
constant number of store calls.

- **Idempotent execution records.** Before acting, the runner begins an `AutomationExecution` unique per (rule, conversation,
  trigger key). A finished record is never re-run, so outbox redelivery and concurrent workers are harmless. Each action outcome is
  persisted before the next action; after a crash the record (status `Running`) resumes at the first unfinished action. Replies and
  notes use deterministic message ids (`auto-<execution>-<index>`) and are detected on resume, webhooks use stable delivery ids, and
  label, priority, status, assignment and attribute writes are idempotent, so no action is duplicated. A `Running` record found at the
  start of a run is taken over compare-and-set (a revision bump) before it is resumed — the loser of a race skips it — and a record a
  concurrent runner has just begun is left to that runner. A runner that loses a progress write stops; the next delivery resumes.
- **Cause metadata.** Events written by the automation client carry it as their timeline actor. Every sequence an execution produces
  (actions and its `automation.applied` marker — `{"v":1,"rule","execution","trigger","depth","applied","skipped","refused","failed"}`)
  is written to the cause index (`IAutomationExecutionStore.FindCauseAsync`) atomically with the execution record. The outbox delivers
  a conversation's changes in order, so the index is written before the consumer sees the produced changes.
- **Opt-in.** Rules ignore automation-caused events unless `RunOnAutomationEvents`.
- **Depth.** An execution triggered by an event an execution at depth *n* caused runs at depth *n + 1*; beyond
  `MaximumChainDepth` (default 3, at most 8) a `Suppressed(DepthExceeded)` record is written instead. An automation-caused event with
  no recorded cause (for example produced by the scheduler and consumed before its record was saved, or written by the client outside
  the engine) counts as maximum depth — fail closed.
- **Rate.** At most `RateLimit` executions (default 50, including suppressed ones) per conversation within `RateWindow` (default
  1 hour); beyond it, `Suppressed(RateLimited)`.
- Later rules see earlier rules' changes (the conversation is re-read before each rule and action).

## Scheduled rules

`ScheduledTrigger(schedule, statuses, inbox?, maximumConversations ≤ 500)` with `AutomationSchedule(expression, timeZoneId)`: five
cron fields (minute, hour, day of month, month, day of week; `*`, values, ranges, steps, lists; both day fields restricted = either
matches), at most 96 fires per day, evaluated in the IANA/Windows time zone (unknown zones are refused by the rule service).

DST: a local time that does not exist (spring-forward gap) fires once at the transition instant; a local time that occurs twice
(fall-back overlap) fires once, at its first occurrence. Fire instants are strictly increasing and never duplicated.

`AutomationScheduler.RunOnceAsync` claims due rules one at a time (`ClaimDueSchedulesAsync(1, …)`: leased, fenced,
`FOR UPDATE SKIP LOCKED`), so each lease covers a single rule's run, then selects the
most recently active conversations with the listed statuses through `IConversationStore.ListAsync` with the automation client's
planned `conversation:read` filter inside the query, and runs the rule per conversation with the trigger key `sched:<slot>` (one
execution per conversation and slot). The next run is the first fire after `max(slot, now)`, so missed slots coalesce. A run with
failures is released for `ScheduleRetryDelay`; if the release is refused (lease expired or fence moved) the run counts as lost and the
timer is claimable again once its lease expires. Rule writes reschedule the timer and bump its fence.

## Contact updates

`AutomationContactFollower.CatchUpAsync(scope)` reads the durable contact change log after the scope's `contacts` checkpoint (stored
compare-and-set in the execution store) and, for each `Updated` change, runs `ContactUpdatedTrigger` rules on the contact's most
recently active non-closed conversations (`MaximumContactConversations`, default 25, filtered by the client's read plan) with the
trigger key `contact:<sequence>`.

## Dry run, replay and the execution log

`IAutomationRuleService` (`automation:read`):

- `DryRunAsync(actor, AutomationDryRunRequest(conversation, event, rules?, draft?, messageBody?))` evaluates the workspace's rules (or an
  unsaved draft) against the live conversation **as the caller** (`conversation:read`, masked view) and returns per rule `Matched`,
  `NotMatched`, `Unknown`, `NotTriggered` or `Disabled` with would-be actions — replies and notes rendered from what the caller may
  see. Nothing is written: no conversation change, no execution record, no outbox entry.
- `ReplayAsync(actor, AutomationReplayRequest(conversation, from, to, rules?))` (window ≤ 7 days, ≤ 500 timeline events scanned)
  re-derives the trigger events of the conversation's timeline in the window and returns, per event, its automation cause, the dry-run
  previews against the **current** conversation (historical snapshots are not kept) and the executions actually recorded for it.
  Side-effect free.
- `ListExecutionsAsync(actor, AutomationExecutionQuery(rule?, conversation?, from?, to?, limit, after))` pages the log by start
  instant descending (keyset cursor). Records carry identifiers, statuses and outcome tokens only.

## Macros

`Macro(id, name, visibility, owner?, actions, description?)`: 1-16 non-rule-only actions; `Shared` macros are visible to every
principal with `macro:run` and managed with `macro:manage`; `Personal` macros belong to one user, who manages them with `macro:run`
(other users get `NotFound`). `IMacroService.RunAsync(actor, macro, conversation)` checks existence, `macro:run` on the live
conversation resource and visibility, then executes the actions **as the agent** through `IConversationService` — each action needs the
agent's own permission — stopping at the first refused or failed action. A best-effort `automation.macro-applied` marker is appended
when the agent holds `conversation:manage`.

## Canned responses and templates

`CannedResponse(id, shortcut, title, defaultLocale, variants, contentType)`: a workspace-unique shortcut token, 1-16 locale variants
(BCP 47-style tags), ≤ 1024 per workspace. A variant is selected by exact tag, then language, then the default locale (the contact's
locale when none is requested).

`AutomationTemplate` is deliberately not a programming language: literal text plus `{{ variable }}` or `{{ variable | fallback }}`
placeholders for a whitelist — `contact.name`, `contact.company`, `contact.attributes.<key>`, `agent.name`, `conversation.id`,
`conversation.status`, `conversation.priority`, `conversation.attributes.<key>`. No expressions, loops, includes or code. Templates are
≤ 16 KiB with ≤ 64 placeholders; each value is truncated to 4096 characters and stripped of control characters; output is capped at
64 KiB UTF-8 (larger renders fail). Values are escaped for the content type (Markdown punctuation backslash-escaped, HTML encoded).
Values come from the rendering principal's masked views, so **masked values are never substituted** — the fallback (or nothing)
renders instead. `agent.name` comes from the optional `IAutomationAgentDirectory` (the acting agent for macros and rendering, the
assignee for rules).

`ICannedResponseService`: reading, listing and shortcut lookup require `canned-response:manage` or `conversation:reply` somewhere in
the workspace (the planned reply filter is not deny-all, so team- and inbox-scoped agents qualify); writes require
`canned-response:manage`; `RenderAsync(actor, response, conversation, locale?)` requires `conversation:reply` on the conversation.

## Permissions

| Permission | Sensitive | Use |
| --- | --- | --- |
| `automation:read` | no | Rules, dry runs, replays and the execution log (workspace level). |
| `automation:manage` | yes | Create, update and delete rules (workspace level). |
| `macro:run` | no | List/read visible macros, manage personal macros (workspace level); run macros (conversation resource). |
| `macro:manage` | no | Create, update and delete shared macros. |
| `canned-response:manage` | no | Create, update and delete canned responses; also grants reading. |

All are registered by `AddAutomation()` (feature-owned), so only Owner holds them until custom roles grant them. Authorization precedes
existence checks for rules and canned responses; macro runs check conversation existence first, like the conversation service.

## Stores

| Contract | Guarantees |
| --- | --- |
| `IAutomationRuleStore` | Scope-confined; per-scope `GetRuleSetVersionAsync` bumped atomically by every applied write; create at revision 1; compare-and-set update/delete; ≤ 256 rules per workspace, atomically; list by priority then id; writes atomically set the schedule timer, clear its lease and bump its fence; leased, fenced `ClaimDueSchedulesAsync` (a live lease is never shared) with fenced complete/release. |
| `IAutomationExecutionStore` | Scope-confined; `BeginAsync` unique per (rule, conversation, trigger key), atomically; `FindManyAsync` (≤ 256 rules, one round trip); compare-and-set updates that write the cause index in the same transaction; bounded rate counts; keyset log queries; compare-and-set checkpoints; `PurgeAsync(before, limit ≤ 10000)` retention. |
| `IMacroStore` | Scope-confined; compare-and-set; ≤ 1024 per workspace. |
| `ICannedResponseStore` | Scope-confined; compare-and-set; shortcut unique per workspace (`DuplicateShortcut`); ≤ 1024 per workspace; keyset list by shortcut with prefix filter. |

`tests/Automation/TicketFlow.Automation.StoreContracts` verifies these against the in-memory stores and PostgreSQL (two independent
data sources), including concurrent schedule claimers (each due timer claimed exactly once) and concurrent beginners (one winner).
The in-memory stores are process-local development examples with no durability.

PostgreSQL (catalog version 2: v2 adds `tf_automation_rule_sets`, the executions `finished` flag with a partial purge index, the
`(scope_key, rule_id, started_at DESC, execution_id DESC)` log index and a cause index on `execution_id`; existing rows are
backfilled): `tf_automation_rules`, `tf_automation_executions`, `tf_automation_causes`, `tf_automation_checkpoints`,
`tf_automation_macros` and `tf_automation_canned_responses`, created only by `InitializeAsync` through the append-only
`ticketflow.automation` migration catalog; `tf_key_v1` keys plus raw-column comparison and `"C"`-collated identifiers;
`format_version` on every row; definitions and execution state stored as `AutomationCodec` JSON; capacities under workspace advisory
locks; schedule timers indexed on `next_run_at` and claimed with `FOR UPDATE SKIP LOCKED`. Lease times come from the store's
`TimeProvider`, so clients need synchronized clocks. Undecodable rows throw.

## Retention

The execution log and cause index grow with every execution. Hosts call `IAutomationExecutionStore.PurgeAsync(before, limit)`
periodically (for example daily, with a cutoff well beyond the outbox retention and replay window): it deletes, store-wide, up to
`limit` finished (completed or suppressed) records started before the cutoff together with their cause-index rows. `Running` records
are kept. PostgreSQL deletes in `FOR UPDATE SKIP LOCKED` batches, so concurrent purgers and runners never block each other. After a
purge an old trigger can execute again if its event is redelivered, so keep the cutoff older than any redelivery window.

## Known limits

- Webhook and survey failures are recorded, not retried; the rule continues with its next action.
- Rules continue after a refused action; macros stop at the first refused or failed action.
- A dry run or replay evaluates what the caller can see, which can differ from what the automation client sees.
- Replay evaluates historical triggers against the current conversation state.
- Fan-out within the depth limit can be exponential when several opted-in rules react to each other's changes; the per-conversation
  rate limit bounds it.
- The scheduler's and contact follower's produced events can reach the outbox consumer before their cause is recorded; they then count
  as maximum depth (fail closed).
- No hosted worker is shipped (E11 hosts the dispatcher, scheduler and contact-follower loops).
