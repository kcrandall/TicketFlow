# Reporting

Support analytics inside TicketFlow's own stores: the conversation outbox (E5) is projected into immutable **facts** and
**15-minute rollup cells**, and Access-filtered reports read the cells. Three packages live under `src/Reporting`:

| Package | Contents |
| --- | --- |
| `TicketFlow.Reporting.Abstractions` | `ReportMetric`, `ReportFact`, `ReportDimensions`, `ReportConversationState`, `ReportProjection`, `ReportBuckets`, `ReportHistogram`, store and service queries, results and unions, `ReportPermissions`, `ReportResources`, `IReportingStore`, `IReportingService`, `IReportSatisfactionSource`. |
| `TicketFlow.Reporting` | `ReportingProjector` (outbox consumer `reporting`), `ReportingService`, `ReportingRetentionWorker`, `SurveyReportSatisfactionSource`, `ReportingOptions`, DI (`AddReporting`, `AddInMemoryReportingStores`) and the development `InMemoryReportingStore`. |
| `TicketFlow.Reporting.Stores.Postgres` | `PostgresReportingStore`, `PostgresReportingMigrations` (component `ticketflow.reporting`), `AddPostgresReportingStores`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)
    .AddConversations().AddPostgresConversationStores(dataSource)
    .AddSurveys().AddPostgresSurveyStores(dataSource)              // optional: CSAT/NPS in reports
    .AddReporting(options =>
    {
        options.SlaWorkerClientId = new ApiClientId("sla-worker"); // must equal SlaOptions.WorkerClientId
        options.FactRetention = TimeSpan.FromDays(400);
    })
    .AddPostgresReportingStores(dataSource);
await new PostgresReportingStore(dataSource).InitializeAsync();   // explicit schema creation

// Host loop (E11 hosts both): register once, then run in every iteration.
await dispatcher.RegisterAsync();               // ConversationOutboxDispatcher; registers consumer "reporting"
await dispatcher.RunOnceAsync();
await retention.RunOnceAsync();                 // ReportingRetentionWorker
```

The projector reads conversations, timelines, inboxes and calendars through the trusted stores and writes only reporting
tables, so it needs no Access grant.

## Facts

A fact is `ReportFact(sourceId, metric, conversation, occurredAt, dimensions, value)`; its identity is `(scope, sourceId, metric)`.
The source is the timeline event that produced it; the conversation-created fact uses `ReportFactIds.Created(scope, conversation)`,
a name-based UUID. Values are 1 for count metrics and microseconds (0 to 3660 days) for duration metrics. Dimensions are the
conversation's inbox, its team, the attributed agent, the channel kind token (the conversation's channel thread, else the inbox's
channel binding), the priority and the labels — all as of the event. Facts carry identifiers only: no bodies, addresses, attribute
values or contact identifiers.

| Metric | Source | Agent dimension |
| --- | --- | --- |
| `ConversationCreated` | conversation creation (`CreatedAt`, initial dimensions) | initial assignee |
| `ConversationResolved` | `StatusChanged` into Resolved or Closed from an unresolved status | assignee |
| `ConversationReopened` | `StatusChanged` from Resolved/Closed back to Open, Pending or Snoozed | assignee |
| `MessageReceived` / `MessageSent` | incoming / outgoing `MessageEvent` | assignee / the sending user (none for API clients and automation) |
| `FirstResponseTime` (+ `…BusinessTime`) | the first **user** reply that answers a waiting contact: reply − oldest unanswered incoming message | replying user |
| `ResponseTime` (+ `…BusinessTime`) | every user reply that answers a waiting contact | replying user |
| `ResolutionTime` (+ `…BusinessTime`) | the first resolution: resolve − creation | assignee |
| `Assignment` / `Handoff` | `AssignmentChanged` to an agent / from one agent to another | new assignee |
| `TeamTransfer` | `TeamChanged` from one team to another (team dimension = new team) | assignee |
| `SlaAchieved` / `SlaBreached` | `sla.met` with `breached: false` / `sla.breached`, **only when appended by the SLA worker**, at the event's own `at` instant | assignee |

- Replies by API clients and automation (auto-acknowledgements, bots) count as `MessageSent` but do not answer the contact: the
  wait keeps running until a user replies. A resolve or close ends the wait without a response fact.
- **Business time** measures the inbox's business calendar with `BusinessCalendarCalculator` (DST-correct, holidays and date
  overrides). Without a calendar, with a deleted calendar or a time zone the host cannot resolve, business time equals calendar
  time. The inbox's *current* calendar is used when the fact is derived.
- **SLA authenticity**: any principal with `conversation:manage` can append extension events, so `sla.*` events count only when
  `TimelineEvent.Actor` is the API client `ReportingOptions.SlaWorkerClientId`. SLA outcomes are bucketed at the breach/met instant
  carried in the payload, which can be earlier than the append (a late event).
- Compliance = achieved ÷ (achieved + breached): each SLA clock cycle either achieves once or breaches once.

## Projection

`ReportingProjector` (consumer `reporting`) synchronizes the conversation of every `Started`, `EventsAppended` and `Deleted` change
(delivery updates are skipped):

1. Read the reporting state (`ReportConversationState`: dimensions, wait marker, first-response flag, resolution count, last applied
   sequence, revision) and the conversation snapshot. A missing conversation deletes the state; its facts stay.
2. A conversation seen for the first time is replayed from sequence 1. Its initial dimensions are reconstructed by undoing the
   timeline (first `Previous` values; label changes reversed), because start requests set them without events. Timelines longer
   than `MaximumReplayEvents` (default 10 000) are **adopted** instead: one created fact with current dimensions and no history.
3. Fold the events after the state's `LastSequence` and write each timeline page (≤ 200 events) as one `ReportProjection`
   (state + facts) **fenced on the state revision**; conflicts repeat the pass (`MaximumWriteAttempts`, default 16).

**Exactly once**: the store inserts a fact only when its key is new and adds exactly the newly inserted facts to their cells in the
same transaction. Redelivered entries, concurrent syncs, crashes between write and acknowledgement and full replays therefore never
count anything twice. **Late events** (a consumer registered after conversations existed, backfills, `sla.*` instants in the past)
land in the cells of their own instants. Facts are immutable: changing how past events are interpreted requires recomputing.

`IReportingStore.RebuildRollupsAsync(scope, from, to)` (cell-aligned, ≤ 31 days) atomically recomputes a range's cells from its
facts — the repair path for imported facts or damaged cells. Facts already removed by retention cannot be restored, so rebuild only
ranges inside the fact retention window.

## Rollups, buckets and percentiles

Cells are keyed by (metric, 15-minute UTC cell, inbox, team, agent, channel, priority, label or none, histogram bin) and keep count,
sum, minimum and maximum. Each fact adds to one per-fact row and one row per carried label (label rows serve label grouping and
label filters).

- **Buckets**: every current time-zone offset and DST transition is a multiple of 15 minutes, so local hours, days, ISO weeks
  (Monday) and months are exact unions of cells in any zone. `ReportBuckets.Boundaries(zone, from, to, granularity)` computes bucket
  starts with `TimeZoneInfo`: a spring-forward day has 23 hours and no 02:00 hour bucket, a fall-back day 25 hours with two 01:00
  hour buckets, a midnight skipped by a DST gap (Chile) starts at the first valid local instant, an ambiguous midnight at its first
  occurrence. Changing a workspace's time zone never requires rebuilding cells. Stores group cells by bucket index (`width_bucket`
  in PostgreSQL) over the supplied boundaries; at most 1000 buckets per query, hourly trends at most 40 days.
- **Percentiles** use deterministic histogram bins (`ReportHistogram`), chosen over exact percentiles so that cells stay
  self-sufficient (they outlive fact retention) and compose across any dimension and bucket. Bin 0 holds values under one second;
  each power-of-two range of seconds is split into 8 equal bins. The nearest-rank estimate (rank ⌈p·n⌉) is the midpoint of the bin
  holding that rank clamped into the bin's observed [minimum, maximum]: exact when the bin holds one distinct value, otherwise within
  half a bin width — at most 6.25 % relative error from one second up, at most half a second below. Means are exact (sum ÷ count).

## Reports

`IReportingService` (scope = the actor's workspace scope):

| Call | Result |
| --- | --- |
| `GetOverviewAsync(actor, ReportQuery)` | Volume (created, resolved, reopened), messages received/sent, assignments, handoffs, team transfers, open backlog (current open conversation states matching the filter — not range-bound), duration summaries (count, mean, median, p90) for first response, response and resolution in calendar and business time, SLA achieved/breached and compliance %, CSAT % and NPS. |
| `GetLeaderboardAsync(actor, ReportLeaderboardQuery)` | Per agent: resolved, messages sent, assignments, handoffs, first response / response / resolution summaries, satisfaction; sorted by resolved, messages, assignments or median first response / response (lowest first), then agent id; 1-100 rows. |
| `GetTrendsAsync(actor, ReportTrendQuery)` | 1-16 metrics bucketed by hour/day/week/month in the workspace time zone (`IWorkspaceSettingsStore`; `ReportingOptions.DefaultTimeZoneId`, default UTC, when unset or unresolvable), optionally grouped by inbox, team, agent, channel, priority or label; sparse points with count, sum and duration percentiles. |
| `ExportAsync(actor, ReportExportQuery, TextWriter)` | RFC 4180 CSV of facts (`source_id, metric, occurred_at, conversation_id, inbox_id, team_id, agent_id, channel, priority, labels, value, unit`), streamed page by page (≤ 1000 facts per store read, flushed per page), at most `MaximumRows` (default 100 000, ceiling 1 000 000) with `Truncated`. Fields starting with `= + - @`, tab or CR are prefixed with `'` (spreadsheet formula injection). |

`ReportQuery` ranges are at most 366 days; overview and leaderboard ranges are aligned outward to whole cells, trend ranges to
bucket starts (the result reports the effective range). Aggregates return at most 100 000 rows and flag `Truncated`.

**Satisfaction** comes from `IReportSatisfactionSource`; `AddReporting` adapts survey aggregates (`ISurveyStore.AggregateAsync`) when
a survey store is registered, so the survey's response resource (inbox, team, agent at resolution) is filtered with the same Access
plan. Survey aggregates cannot apply channel, priority or label restrictions, so `Satisfaction` is `null` when the report filters on
them or on more than one of inbox/team/agent; leaderboards include per-agent satisfaction only without inbox, team, channel, priority
or label restrictions. Survey responses are counted at their response time. No change to the surveys feature was needed.

## Access

| Permission | Sensitive | Use |
| --- | --- | --- |
| `report:view` (Access built-in: Supervisor, Viewer, Admin, Owner) | no | Overview, leaderboard and trends over the rows the planned filter allows. |
| `report:view-own` (registered by `AddReporting`) | no | User principals: additionally the rows attributed to themselves (`agent = self`), within that permission's plan. |
| `report:export` (registered by `AddReporting`) | **yes** | CSV export. Every plan is audited (`FilterPlanned`, also when refused). |

Feature permissions are held only by Owner until custom roles grant them (for example grant agents a role with `report:view-own`).

- Every report plans `report:view` with `IAccessQueryPlanner` and ORs in `report:view-own ∧ agent = self` for user principals; the
  combined filter runs **inside the store query**. A principal without applicable grants gets empty reports, not a refusal, like
  every list path. A plan exceeding the filter node limit falls back to the narrower `report:view` plan.
- Rollup rows are filtered over `ReportResources.Rollup` — scope, inbox, team and the attributed agent as assignee, with **unknown
  labels and attributes**, so label and attribute conditions fail closed on aggregates (`team ∈ A ∨ has-label(x)` still includes
  team A rows: Kleene logic). Team- and inbox-scoped `report:view` grants see their team's/inbox's rows; an agent with
  `report:view-own` sees exactly the facts attributed to them (their replies, assignments, resolutions while assigned).
- The backlog filters conversation states (`ReportResources.Conversation`, labels known); export filters facts
  (`ReportResources.Fact`, labels known) with `report:export ∧ (report:view ∨ own)`; an export without any `report:export` grant is
  refused (`Forbidden`).
- Reports never read bodies or masked fields; facts carry identifiers only, so masking policies have nothing to redact.
- Batch decision contexts are not needed: reports never decide rows one by one; each report loads at most three plans.

## Retention

`ReportingRetentionWorker.RunOnceAsync()` deletes facts older than `FactRetention` and cells older than `RollupRetention` (each
optional, at least one day; `null` keeps them, the default), at most `MaximumPruneBatches` × `PruneBatchSize` rows per table per run,
store-wide (`PruneFactsAsync`, `PruneRollupsAsync`; `FOR UPDATE SKIP LOCKED` in PostgreSQL). Cells may outlive facts: reports read
only cells (and states), export reads facts. Conversation states are removed when their conversation is deleted.

## Stores

`IReportingStore` documents the contract: scope confinement; atomic projections (fenced state, insert-if-new facts, rollups for exactly
the inserted facts); keyed cells; Access-filtered aggregates, backlog counts and keyset fact lists (ordered by occurred-at, source id as
lowercase hex, metric); store-wide retention deletes; bounded rebuilds. The shared fixture
`tests/Reporting/TicketFlow.Reporting.StoreContracts` verifies the in-memory store and PostgreSQL (two independent data sources):
exactly-once replays (also with tampered content), late facts, bucket grouping and every dimension, histograms, label rows, scope
isolation, concurrent projections sharing cells, retention and rebuild equality, and randomized grant parity with point checks
(`AccessFilteredReportParity`). The in-memory store is a bounded (`MaximumFacts`, default 1 000 000), process-local development example
without durability.

`PostgresReportingStore`: tables `tf_report_conversations`, `tf_report_facts` and `tf_report_rollups`, created only by `InitializeAsync`
(append-only `ticketflow.reporting` catalog); `tf_key_v1` keys (conversation, fact = scope + source + metric, cell = scope + every cell
dimension) plus raw-column comparison; cells stored as microseconds since the Unix epoch (`cell_us`) so keys are immutable expressions;
`format_version` on every row. A projection is one transaction: fenced state upsert, one `INSERT … SELECT unnest(…) ON CONFLICT DO
NOTHING RETURNING` for the facts, one rollup upsert for the inserted facts ordered by cell key (no deadlocks between concurrent
projections). Projections take a shared and rebuilds an exclusive scope advisory lock. Filters are translated with
`PostgresAccessFilterTranslator` over derived tables that expose unknown labels/attributes as NULL columns, so SQL three-valued logic
matches `AccessFilterEvaluator`; untranslatable filters return no rows. Undecodable rows throw.

## Known limits

- Cells are hot rows when one workspace produces many facts in the same 15-minute cell; projections serialize on them briefly.
- Business-time facts use the inbox calendar at projection time, not the calendar that was in force when the event happened.
- Resolution time is measured once, for the first resolution; later resolutions count only as resolved.
- Satisfaction filters are limited to what survey aggregates support (see Reports).
- The open backlog is a current count; historical backlog trends are not modeled.
- CSV `labels` are joined with `;` (labels containing `;` are ambiguous in that column).
- **InsightFlow export** is deferred ([E14 T06](agents/epics/E14-reporting/tickets/T06-insightflow-export.md)): InsightFlow is not
  published and exposes no HTTP event ingestion contract. `IReportingStore.ListFactsAsync` (keyset, identifiers only, stable fact
  keys) is the seam a future exporter pages through.
- No hosted worker is included (E11 hosts the dispatcher and retention loops).
