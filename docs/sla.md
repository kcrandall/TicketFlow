# SLAs and business calendars

Measurable, escalating service levels on conversations. Business calendars and `BusinessCalendarCalculator` come from E3
([workspaces](workspaces.md)); conversations, timelines and the outbox from E5 ([conversations](conversations.md)). Three packages
live under `src/Sla`:

| Package | Contents |
| --- | --- |
| `TicketFlow.Sla.Abstractions` | `SlaPolicy` (rules, conditions, targets, clock basis, pause policy, escalations with the `SlaAction` union), `SlaPolicyCodec`, `SlaTracker` and `SlaClock`, `SlaTrackerCodec`, the `SlaTimelineEvent` union and `SlaEventCodec`, `SlaPermissions`, store contracts `ISlaPolicyStore` / `ISlaTrackerStore`, services `ISlaPolicyService` / `ISlaService` with union results. |
| `TicketFlow.Sla` | `SlaEngine` (clock state machine), `SlaClockTime`, `SlaPolicyService`, `SlaService`, `SlaConversationTracker` (outbox handler), `SlaTimerScheduler`, DI (`AddSla`, `AddInMemorySlaStores`) and development in-memory stores. |
| `TicketFlow.Sla.Stores.Postgres` | `PostgresSlaStore` (both store contracts), `PostgresSlaMigrations`, `AddPostgresSlaStores`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddSla(options => options.WorkerClientId = new ApiClientId("sla-worker"))
    .AddPostgresSlaStores(dataSource);
await new PostgresSlaStore(dataSource).InitializeAsync();   // explicit schema creation

// Host loop (E11 will ship a hosted worker): register once, then run both in every iteration.
await dispatcher.RegisterAsync();                 // ConversationOutboxDispatcher, consumer "sla" among others
await dispatcher.RunOnceAsync();
await scheduler.RunOnceAsync();                   // SlaTimerScheduler
```

The tracker appends timeline events as the API client `WorkerClientId` (one `ApiClientPrincipal` per workspace scope). Grant it a
custom role with `conversation:manage` in every workspace that uses SLAs; without the grant, outbox deliveries fail and retry (and
dead-letter after the dispatcher's attempt limit).

## Policies

A policy is referenced by inboxes through `Inbox.SlaPolicy` (`SlaPolicyId`, E3). `SlaPolicy(id, name, rules, pause, atRiskPercent)`:

- **Rules** (1-32) are evaluated in order; the first whose `SlaCondition` matches applies. A condition matches when the priority is
  one of `Priorities` (empty = any) and the conversation carries every label in `Labels` (≤ 16, empty = any). A conversation that
  matches no rule has no SLA.
- **Targets** (`SlaTargets`): `FirstResponse`, `NextResponse`, `Resolution`, each optional (at least one), whole seconds between one
  minute and 365 days.
- **Basis** (`SlaClockBasis`): `BusinessHours` measures the open time of the inbox's business calendar through
  `BusinessCalendarCalculator` (calendar time when the inbox has no calendar or the calendar was deleted); `CalendarTime` measures
  wall-clock time.
- **Pause policy** (`SlaPausePolicy`): `PauseWhenPending` and `PauseWhenSnoozed` (both default true). A snooze pauses only until its
  `Until` instant, even though conversations stay `Snoozed` until an agent or incoming message changes them.
- **Escalations** (≤ 16 per rule, unique per metric and threshold): at `ThresholdPercent` (1-100, 100 = breach) of a metric's target,
  record ≤ 8 `SlaAction`s — `NotifyUserAction`, `NotifyTeamAction`, `RaisePriorityAction`, `ReassignTeamAction`. SLA never executes
  them: they travel in `sla.warning` / `sla.breached` timeline events for automation (E10) and notifications (E7).
- **At risk**: a running, unbreached clock whose elapsed time reached `AtRiskPercent` (1-99, default 75) of its target.

`SlaPolicyCodec` persists definitions as `{"v":1,"name":…,"risk":…,"pause":{…},"rules":[…]}` with explicit discriminators (actions
`"a"`: `notify-user`, `notify-team`, `raise-priority`, `reassign-team`); durations are integer microseconds. Unknown versions,
discriminators and properties are rejected.

## Clocks

`SlaEngine` replays the conversation timeline in sequence order. Before applying a fact at instant `t` it advances every running clock
to `t`, firing crossed thresholds in instant order (ties by metric, then threshold); facts older than the last evaluation are applied
at the last evaluation instant.

| Metric | Starts | Satisfied (`sla.met`) | Otherwise |
| --- | --- | --- | --- |
| First response | When the contact starts waiting (first incoming message) and no reply exists yet | First outgoing reply | Cancelled by resolve/close without a reply |
| Next response | Each time the contact starts waiting again after a reply (cycle 1, 2, …) | Next outgoing reply | Cancelled by resolve/close |
| Resolution | When the rule applies (conversation creation for the inbox's policy) | Resolve or close | Resumes with its elapsed time when an incoming message reopens the conversation; completes again on the next resolve |

- **Elapsed time** accumulates in measured time (`SlaClockTime.Between`); the predicted breach instant is
  `SlaClockTime.Add(runningSince, target − elapsed)`. Business time is real elapsed time across DST (a skipped hour is never open, a
  repeated hour is open twice), holidays and date overrides come from the calendar.
- **Pauses** stop running clocks and record a `SlaPauseInterval` (newest 32 kept).
- **Thresholds** fire once per clock cycle (`FiredThresholds`); 100 % always fires (`sla.breached`), escalation thresholds below 100
  fire `sla.warning`. A breached clock keeps running until satisfied, then completes as `Missed` (`sla.met` with `breached: true`).
- **Re-matching**: priority and label changes re-run rule matching at the change instant. A different rule emits `sla.applied`,
  re-targets active clocks keeping their elapsed time, cancels clocks the new rule does not measure and starts the ones it does
  (response clocks from the instant the contact started waiting). No matching rule cancels every active clock. Policy edits are not
  applied retroactively; they take effect at the next re-match.

The tracker derives an `SlaSchedule`: `NextCheckAt` (earliest unfired threshold, snooze expiry, or now while emissions are pending),
`DueAt` (earliest predicted breach of a running, unbreached clock), `AtRiskAt`, `Breached` (an active clock is breached) and
`UrgencyAt` (the earlier of `DueAt` and the first breach of an active clock; orders risk lists).

## Timeline events

Emissions are versioned `ExtensionEvent`s (payload version 1, `SlaEventCodec`):

| Kind | Payload |
| --- | --- |
| `sla.applied` | `key`, `policy`, `policyRevision`, `rule` (index), `ruleName`, `basis`, `calendar`, `targets`, `at` |
| `sla.warning` | `key`, `policy`, `metric`, `cycle`, `pct`, `target`, `at` (crossing instant), `dueAt`, `actions` |
| `sla.breached` | `key`, `policy`, `metric`, `cycle`, `target`, `at` (breach instant), `actions` (the 100 % escalation's) |
| `sla.met` | `key`, `policy`, `metric`, `cycle`, `target`, `elapsed`, `breached`, `at` |

Instants are UTC ISO-8601 with microseconds; durations integer microseconds; metrics `first-response|next-response|resolution`.
Consumers decode with `SlaEventCodec.TryDecode(extension, out var slaEvent)` and switch over the `SlaTimelineEvent` union. Payloads
carry identifiers only (no bodies, addresses or attribute values).

**Consumers must verify the actor.** Any principal holding `conversation:manage` can append an extension event of any kind, so an
`sla.*` event is authentic only when `TimelineEvent.Actor` is the configured SLA worker (`AccessActorKind.ApiClient` with
`SlaOptions.WorkerClientId`). Automation (E10) and notifications (E7) must ignore `sla.*` events from other actors; the tracker's own
duplicate detection does the same, so a forged event can neither trigger escalations nor suppress the real one.

## Tracker and timers

`SlaConversationTracker` is an `IConversationOutboxHandler` (consumer `sla`). For each change (delivery updates and extension/note-only
changes are skipped) it synchronizes the conversation:

1. Read the tracker and the conversation snapshot; a missing conversation deletes the tracker (and its timer).
2. Load the inbox, its policy and calendars. A new tracker replays the timeline from sequence 1 (or, beyond
   `SlaOptions.MaximumReplayEvents`, adopts the snapshot's waiting/reply facts); an existing one replays the events after its
   `LastSequence`. Then advance to now.
3. Write the tracker revision-fenced (`expectedRevision`), retrying conflicts up to `MaximumWriteAttempts`; the same transaction sets
   the timer to `NextCheckAt`.
4. Flush pending emissions through `IConversationService.AppendExtensionAsync` as the worker principal. Each emission was recorded
   in the tracker (with a unique `key` and the conversation head at the time) before any append. Each flush round reads the
   conversation revision, scans the timeline **once** from the oldest pending emission's recorded head for `sla.*` events whose actor
   is the worker, and appends the missing emissions **fenced on that revision** (`expectedRevision`, advancing with each successful
   append). Any concurrent conversation write — including another worker appending the same emission — makes the append conflict and
   the round rescans. Redelivery, concurrent schedulers and syncs, and a crash between append and acknowledgement therefore never
   duplicate a timeline entry.
5. Acknowledge the flushed emissions in a final tracker write. When the flush fails — the append is refused (no grant), conversation
   writers keep winning (`MaximumWriteAttempts` rounds), or the scan span exceeds `MaximumDuplicateScanEvents` (default 1000; the
   result is treated as unknown, nothing is appended blindly) — the remaining emissions stay pending, `SlaTracker.FlushRetryAt` is set
   to now + `TimerRetryDelay` and the timer follows it, so failures back off instead of spinning; the sync then throws
   `SlaFlushDeferredException` (the outbox delivery retries; the scheduler counts it as retried).

`SlaTimerScheduler.RunOnceAsync` claims due timers (`ClaimDueAsync`, leased) and synchronizes each tracker on behalf of its claim, so
warnings, breaches and snooze expiries happen without new conversation events. Tracker writes release a timer lease only for the claim
that holds it (matching fence): the scheduler's final write releases its own lease, while writes from the outbox path reschedule the
timer but leave another worker's live lease alone. A failed evaluation (other than a deferred flush) releases the timer for
`TimerRetryDelay`.

The tracker reads conversations and timelines through the trusted `IConversationStore`, because it mirrors the exact Access resource
(team, inbox, assignee, labels and attribute condition values) for list filtering; every timeline write is access-checked.

## Status and risk queries

`ISlaService` (`sla:read`):

- `GetStatusAsync(actor, conversation)`: existence first, then `sla:read` on `ConversationAccess.Resource` of the live conversation.
  Returns `SlaStatusView` evaluated at now without persisting: per clock elapsed and remaining measured time (negative once breached),
  predicted `DueAt`, `AtRisk`, `Breached`, outcome; plus paused flag and the policy/rule. A conversation without a tracker (or rule)
  reports no policy and no clocks.
- `ListAsync(actor, SlaRiskQuery(filter, inbox, limit, after), cursor)`: `AtRisk`, `Breached` or `AtRiskOrBreached` conversations,
  ordered by `UrgencyAt` then conversation id, keyset-paged (1-100) with an opaque cursor bound to the query. The `sla:read` plan from
  `IAccessQueryPlanner` is applied inside the store query over the mirrored resource; every returned row is also point-checked against
  the live conversation, so a stale mirror never leaks a row. "Breached" (as of the query instant: `UrgencyAt <= now`) includes running
  clocks past their due instant whose timer has not fired yet.

## Permissions

| Permission | Sensitive | Use |
| --- | --- | --- |
| `sla:read` | no | Policy get/list (workspace level); status and risk lists (conversation resource, so team/inbox-scoped grants and conditions apply). |
| `sla:manage` | no | Policy create/update/delete (workspace level). |

Both are registered by `AddSla()` (feature-owned, not Access built-ins), so only Owner holds them until custom roles grant them.

The SLA worker needs `conversation:manage` to append extension events. That permission also allows changing priority, labels,
attributes and participants, so the worker is over-privileged relative to its job; a narrower extension-append permission would
need a change in the conversations feature and is not part of E9. Treat the worker's API client credentials accordingly.
Existence of a policy is checked after authorization (`Forbidden` precedes `NotFound`); for status, the conversation's existence is
checked first, like the conversation service.

## Stores

| Contract | Guarantees |
| --- | --- |
| `ISlaPolicyStore` | Scope-confined; create at revision 1 (conflict when present); compare-and-set update/delete; ≤ 256 policies per workspace, atomically. |
| `ISlaTrackerStore` | Scope-confined trackers; create or compare-and-set write that atomically sets the timer and releases the lease only for the holding claim (`release` fence); delete removes the timer; `ClaimDueAsync` leases due timers earliest first, incrementing a fence, never sharing a live lease; `ReleaseAsync` is fenced and requires a live lease; `ListAsync` applies the risk filter at `now` and the `AccessFilter` in the bounded keyset query (untranslatable → no rows). |

`tests/Sla/TicketFlow.Sla.StoreContracts` verifies both against the in-memory stores and PostgreSQL (two independent data sources),
including concurrent compare-and-set writers, concurrent timer claimers (each due timer claimed exactly once) and risk-list parity
with `AccessFilterEvaluator`. The in-memory stores are process-local development examples with no durability.

PostgreSQL: `tf_sla_policies`, `tf_sla_trackers` and `tf_sla_timers`, created only by `InitializeAsync` through the append-only
`ticketflow.sla` migration catalog; `tf_key_v1` keys plus raw-column comparison and `"C"`-collated identifiers; `format_version` on
every row; tracker state stored as `SlaTrackerCodec` text with indexed `due_at`/`at_risk_at`/`breached`/`urgency_at` and Access
columns (`team_id`, `inbox_id`, `assignee_id`, `labels`, `access_attributes`); policy capacity under a workspace advisory lock; timers
indexed on `due_at` and claimed with `FOR UPDATE SKIP LOCKED` plus lease and fence. Claims, lease expiry and release checks use the
**database clock** (`statement_timestamp()`), so hosts with skewed clocks cannot steal or keep leases; `new PostgresSlaStore(dataSource,
leaseClock)` substitutes a deterministic clock for tests (`AddPostgresSlaStores` always uses the database clock). Due instants are
still computed by application hosts, which need reasonably synchronized clocks. Risk lists bound breached rows with
`urgency_at <= now` (range scan on the urgency indexes) and at-risk rows with the partial index
`tf_sla_trackers_at_risk (scope_key, at_risk_at) WHERE at_risk_at IS NOT NULL AND NOT breached` (migration 2, `at-risk-index`).
Undecodable rows throw.

## Known limits

- A tracker that cannot append (missing grant, unknown time zone on the host) keeps its emissions pending; outbox deliveries
  dead-letter after the dispatcher's attempt limit, timers retry every `TimerRetryDelay`.
- An emission whose duplicate-scan span exceeds `MaximumDuplicateScanEvents` (a very busy conversation while the worker could not
  append) stays pending until the option is raised; it is never appended without a decidable scan. At most 64 emissions can be pending.
- Initial priority and labels come from the conversation snapshot at the first sync (start requests carry them without timeline
  events); a resumed tracker keeps its own label set and replays later changes at their event instants.
- Changing an inbox's policy or calendar affects conversations at their next re-match; deleting a policy leaves existing trackers on
  their applied rule snapshot.
- Facts committed while a sync is evaluating can be applied at the sync's evaluation instant (milliseconds late).
- First-response clocks cancelled by a resolve are not restarted by a reopen; the reopened conversation is tracked by next response
  only once a reply exists.
- No hosted worker is shipped (E11 hosts the dispatcher and scheduler loops).
