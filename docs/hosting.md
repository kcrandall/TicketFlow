# Hosting

Three optional packages turn the libraries into a running help desk. None is required: every feature works through its services
without them.

| Package | Contents |
| --- | --- |
| `TicketFlow.Hosting.AspNetCore` | Minimal-API endpoint groups over the feature services, ProblemDetails mapping, ETag/If-Match revision fencing, principal and partition resolution, OpenAPI. |
| `TicketFlow.Hosting.Worker` | `BackgroundService` loops for every library loop, per-loop options, jittered backoff, health check, metrics and traces. |
| `TicketFlow.Hosting.Worker.Stores.Postgres` | Durable `IWorkerCheckpointStore` (contact-merge catch-up position). |

The optional management UI (`TicketFlow.UI.Blazor`, mounted at `/support` in the sample) is described in [ui-blazor.md](ui-blazor.md).
`TicketFlow.Realtime.SignalR` (agent and visitor hubs) is described in [realtime.md](realtime.md); IdentityFlow authentication in
[identityflow-integration.md](identityflow-integration.md). `samples/TicketFlow.SampleHost` composes all of them.

## Endpoints

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddWorkspaces().AddContacts().AddConversations() /* ... features and stores ... */
    .AddTicketFlowEndpoints(options =>
    {
        options.Partition = new AccessPartition("tenant", "project", "prod", "web");   // or register ITicketFlowPartitionResolver
        options.AnonymousRateLimitingPolicy = "anonymous";                             // optional, host-registered policy
    })
    .AddTicketFlowOpenApi()
    .AddIdentityFlowPrincipals();          // or register ITicketFlowPrincipalResolver / set options.ResolvePrincipal

app.MapTicketFlowEndpoints();              // or MapTicketFlowConversations(), MapTicketFlowContacts(), ... per feature
app.MapTicketFlowOpenApi();                // GET /ticketflow/openapi/ticketflow.json
```

Groups: `MapTicketFlowWorkspaces` (settings, inboxes and inbox agents, teams and members, business calendars), `MapTicketFlowAccess`
(`/me`, roles, grants by target, field policies, break-glass), `MapTicketFlowContacts` (search, CRUD with edits, reveal, merge/unmerge,
NDJSON export, attribute definitions), `MapTicketFlowConversations` (filtered keyset lists, start, view, timeline, reply, note, assignee,
team, status, snooze, priority, labels, attributes, participants, delete, attribute definitions), `MapTicketFlowRouting` (own/managed
agent status, heartbeat, settings, queue, presence, reply locks, routing policies), `MapTicketFlowSla` (conversation SLA, risk list,
policies), `MapTicketFlowLiveChat` (widget configurations, channel endpoints, agent visitor view), `MapTicketFlowAutomation` (rules,
dry run, replay, executions, macros and runs, canned responses and rendering), `MapTicketFlowHelpCenter` (portals, collections,
articles and lifecycle, revisions, preview, feedback, agent search), `MapTicketFlowSurveys` (definitions, responses, aggregates),
`MapTicketFlowAssist` (summary, draft, triage, sentiment, translate, rewrite, settings, usage) and the anonymous
`MapTicketFlowPublicPortal` and `MapTicketFlowPublicSurveys`. Map only groups whose feature services are registered (service parameters
are `[FromServices]`, so mapping succeeds and a missing service fails the request).

### Routes, scope and principals

Authenticated groups live under `{RoutePrefix}/workspaces/{workspaceId}` (default `/ticketflow/api`), anonymous groups under
`{PublicRoutePrefix}/workspaces/{workspaceId}` (default `/ticketflow/public`). The scope of every request is the partition from
`ITicketFlowPartitionResolver` (host name, tenant claim or fixed configuration) plus the route workspace. `ITicketFlowPrincipalResolver`
maps the host-authenticated `HttpContext.User` to a `PrincipalRef` of exactly that scope; no principal means 401. The principal is an
identity, not a permission: every handler calls the feature service, which authorizes through Access, so a user naming a workspace it
has no grants in gets 403. Access-store listings (roles, grants by target, field policies) are guarded by an explicit `access:manage`
point check.

`GET /me` returns the principal kind, identifier, team snapshot and the catalog permissions it holds at workspace level, for UI
gating. Sensitive permissions are probed only with `?includeSensitive=true` because each such decision is audited. Narrower team or
inbox grants are not reflected; the server still enforces every call.

### HTTP conventions

- **Errors** are RFC 9457 problem details with `type` `urn:ticketflow:problem:{code}` and a stable `code` extension (`unauthenticated`,
  `forbidden`, `not-found`, `conflict`, `revision-mismatch`, `precondition-required`, `invalid`, `rejected`, `closed`, `merged`,
  `identity-taken`, `reveal-refused`, `reply-lock-held`, `quota-exceeded`, `rate-limited`, `provider-*`, ...), optional `errors`
  (non-sensitive validation messages) and `currentRevision`.
- **Status mapping**: Forbidden 403, NotFound 404, Invalid 422, Conflict 409 (412 when the request carried `If-Match`), store limits
  and uniqueness 409 `rejected`, closed conversations 409 `closed`, assistant quota/rate limits 429 with `Retry-After`, retryable
  provider failures 503 and others 502. Domain constructor and codec validation (`ArgumentException`, `FormatException`, `JsonException`)
  maps to 422; messages are returned only when TicketFlow produced them, so input values are never echoed.
- **Revision fencing**: single-record reads return a strong `ETag: "7"`. Updates and deletes of existing records require
  `If-Match: "7"` (428 when missing, 400 when malformed, 412 with `currentRevision` and the current `ETag` when stale). Conversation
  commands accept an optional `If-Match` (without one they apply to the current revision, retrying concurrent writers) and return the
  new revision and appended sequences; clients refetch the timeline to read them.
- **Request size**: `MaximumRequestBodyBytes` (default 1 MiB) is attached as `IRequestSizeLimitMetadata` (enforced by Kestrel and
  HTTP.sys while reading) and requests with a larger `Content-Length` get 413.
- **Cursors** are opaque continuation tokens (`next` in `PageDto<T>`); pass them back unchanged.
- **Streaming export**: `POST /contacts/export` streams `application/x-ndjson` (one masked contact per line) from the service's bounded
  async enumerable and returns the audit event id in `X-TicketFlow-Audit-Event`. Reveal and export responses are `Cache-Control: no-store`.

### DTOs

Wire DTOs (`TicketFlow.Hosting.AspNetCore.Contracts`) are separate from domain records: identifiers are strings, enums camelCase
strings, closed sets use explicit `kind`/`type` discriminators with one populated payload (timeline events, attribute values, grant
subjects and scopes, action outcomes), and unions are never serialized. Nested definitions owned by a feature travel as that feature's
versioned codec JSON (`DefinitionDto.Definition`): SLA and routing policies, automation rules, macros and canned responses, calendar
schedules, access conditions, live-chat widgets and assistant settings. Masked values are absent (`masked`/`revealable` flags plus
`maskedFields`); revealing is an explicit, audited call.

### Anonymous endpoints

The public portal (`/portals/{slug}`, collections, articles, search, view and feedback recording) and survey responses
(`POST /surveys/invitation`, `POST /surveys/respond`) need no principal; readers enforce portal visibility and token validity.
Survey tokens travel in request bodies, never in URLs (keep them in the link's fragment so they stay out of server logs and
`Referer`). Set `AnonymousRateLimitingPolicy` to a policy registered with `AddRateLimiter` and call `UseRateLimiter()`; apply the same
policy to the live-chat visitor hub and upload endpoint (`MapTicketFlowRealtime().Visitor.RequireRateLimiting(...)`).

### Browser hosts

Endpoints read JSON bodies only, so ASP.NET Core antiforgery is not applied. Hosts that authenticate agents with cookies must protect
state-changing requests themselves: `SameSite=Strict` (or `Lax`) cookies, a restrictive CORS policy and, where cross-site requests are
possible, antiforgery tokens (for example a custom header checked by middleware). Bearer-token hosts are not affected.

### OpenAPI

`AddTicketFlowOpenApi()` adds a document (default name `ticketflow`) containing only TicketFlow endpoints, with stable operation ids
(`ListConversations`, `Reply`, ...) and DTO schemas; `MapTicketFlowOpenApi()` serves it at `/ticketflow/openapi/{documentName}.json`.
External clients can generate typed clients from it; the bundled Blazor UI ([ui-blazor.md](ui-blazor.md)) calls the services directly.

## Workers

```csharp
services.AddTicketFlow()
    /* features and stores */
    .AddPostgresWorkerCheckpoints(dataSource)      // durable catch-up positions (in-memory default otherwise)
    .AddTicketFlowWorkers(options =>
    {
        options.Outbox.Interval = TimeSpan.FromSeconds(1);
        options.SurveyDispatch.Enabled = false;
    });
services.AddHealthChecks().AddTicketFlowWorkers();
// At startup, with the other explicit store migrations:
await app.Services.GetRequiredService<PostgresWorkerCheckpointStore>().InitializeAsync();
```

| Loop | Library call | Default interval |
| --- | --- | --- |
| `outbox` | `ConversationOutboxDispatcher.RegisterAsync` (once) and `RunOnceAsync` for every registered consumer (`channels.dispatch`, `routing`, `sla`, `automation`, `realtime`, `surveys.resolved`, ...) | 1 s |
| `outbox-pruning` | `IConversationOutbox.PruneAsync(now - OutboxRetention)` (default 7 days) | 1 h |
| `routing-maintenance` | `RoutingMaintenanceWorker.RunOnceAsync` | 5 s |
| `sla-timers` | `SlaTimerScheduler.RunOnceAsync` | 5 s |
| `automation-schedules` | `AutomationScheduler.RunOnceAsync` | 15 s |
| `automation-contacts` | `AutomationContactFollower.CatchUpAsync` per workspace | 10 s |
| `contact-merges` | `ConversationContactMergeFollower.CatchUpAsync` per workspace, position in `IWorkerCheckpointStore` (`contact-merges`) | 10 s |
| `survey-dispatch` | `ISurveyDispatcher.DispatchDueAsync` per workspace | 30 s |
| `livechat-pruning` | `ILiveChatSessionStore.PruneExpiredAsync(now)` | 1 h |
| `assist-maintenance` | `AssistMaintenance.RunOnceAsync` | 5 min |

Each loop runs in a fresh DI scope per run. A run that reports work starts the next run immediately (up to `MaximumDrainRuns`), then
waits `Interval`; every delay gets ±`Jitter` (default 20 %) so nodes do not run in lockstep. A failing run backs off exponentially from
`InitialBackoff` to `MaximumBackoff`. Per-workspace loops enumerate `IWorkspaceDirectory` (workspaces with settings; in-memory and
PostgreSQL implementations ship with the workspace stores), isolate failures per workspace and report them as a partial failure.
Loops whose library service is not registered report `NotConfigured` and never run; `Enabled = false` reports `Disabled`. Host
shutdown cancels in-flight runs. `AddTicketFlowWorkerLoop<TService>(name, run)` hosts additional loops (for example the reporting
retention worker) with the same scheduling, health and telemetry.

Leased library loops (outbox, routing, SLA, automation schedules, survey claims) are safe on several nodes; per-workspace followers are
idempotent and their checkpoints are compare-and-set.

**Health**: `TicketFlowWorkerHealthCheck` is unhealthy once a running loop failed `UnhealthyAfterFailures` (default 5) times in a row,
degraded after any consecutive failure; its data lists `loop: Status:failures` only.

**Telemetry**: `ActivitySource` and `Meter` `TicketFlow.Hosting.Worker` (`ticketflow.worker.runs`, `ticketflow.worker.items`,
`ticketflow.worker.run.duration`) tagged with loop name and outcome (`success`, `partial`, `failure`). Exceptions are logged by type
only; identifiers, bodies, addresses and messages never reach logs or telemetry.

## Sample host

```bash
docker compose -f samples/docker-compose.yml up -d                 # PostgreSQL 18 on 127.0.0.1:5439
dotnet run --project samples/TicketFlow.SampleHost -- seed         # migrations + demo data (idempotent)
dotnet run --project samples/TicketFlow.SampleHost                 # http://localhost:5080/support (UI), /demo (widget)
curl -H "X-Dev-User: agent1" http://localhost:5080/ticketflow/api/workspaces/acme/conversations
```

The sample composes every feature with PostgreSQL stores (`TicketFlow:Storage=InMemory` for a throwaway run), SignalR hubs (agent hub
connections pass `?workspace=acme`), workers, OpenAPI, `/health`, the anonymous rate limiter and the widget demo page (bundle served from
`clients/widget/dist`; build it with `npm run build`). Seeded data: workspace `acme`, live-chat inbox `web-chat` with widget key
`wk_demo`, team `tier1`, users `admin` (Owner), `agent1`, `agent2` (Agent), grant-holding API clients for the channel worker, routing
engine, SLA tracker and automation, a public help-center portal `help` with a published article and a CSAT survey, plus the UI demo
content (more inboxes, teams and staff, contacts, two weeks of history, current conversations with SLA risk, automation, articles and
CSAT responses; see [ui-blazor.md](ui-blazor.md)).

Authentication uses a **development-only** handler (`X-Dev-User` header or `dev_user` query parameter, no verification) that refuses to
run outside the Development environment; it emits IdentityFlow-shaped claims so `AddIdentityFlowPrincipals()` maps users exactly as it
would for real IdentityFlow sessions. Replace it with IdentityFlow authentication for anything beyond local development.

## Verification

- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release`
- `dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Tests -c Release`
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Stores.Postgres.Tests -c Release`
- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release` (the PostgreSQL case needs `TICKETFLOW_TEST_POSTGRES`)

## Not yet implemented

- Cross-node SignalR fan-out (backplane and `IRealtimeBus`), see [realtime.md](realtime.md).
