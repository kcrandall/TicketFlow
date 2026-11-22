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
has no grants in gets 403. Access listings (roles, grants by target, field policies) go through `IAccessAdministrator.List*Async`, which
requires `access:manage` from standard grants.

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
- **Revision fencing**: single-record reads return `ETag: "7"`. Updates and deletes of existing records require `If-Match: "7"` (a weak
  `W/"7"` is accepted too, since the revision is the only validator; 428 when missing, 400 when malformed, 412 with `currentRevision` and
  the current `ETag` when stale). Agent routing settings are replaced with `If-Match` and created with `If-None-Match: *`. Reply locks
  return their fence as `ETag`; `DELETE /conversations/{id}/reply-lock` releases with `If-Match: "{fence}"`. Conversation
  commands accept an optional `If-Match` (without one they apply to the current revision, retrying concurrent writers) and return the
  new revision and appended sequences; clients refetch the timeline to read them.
- **Request size**: `MaximumRequestBodyBytes` (default 1 MiB) is attached as `IRequestSizeLimitMetadata` (enforced by Kestrel and
  HTTP.sys while reading) and requests with a larger `Content-Length` get 413.
- **Pagination**: every collection endpoint takes `limit` (default 50, 1-200) and `cursor` and returns `PageResponse<T>`
  (`items`, `next`). Cursors are opaque base64url tokens; pass `next` back unchanged (malformed cursors are 422).
- **CSRF**: authenticated `POST` requests must carry a JSON body (`Content-Type: application/json`) or the `X-TicketFlow-Request`
  header (any value); others get 400 `json-or-header-required`. Body-less commands (heartbeat, unmerge, article publish/archive, assist
  summary, ...) therefore send the header. Neither can be sent cross-origin without a CORS preflight, so cookie-authenticated hosts are
  not exposed to cross-site form or `fetch(no-cors)` posts; `PUT`, `PATCH` and `DELETE` always require a preflight.
- **Routes** use kebab-case segments (`/help-center/...`, `/live-chat/...`, `/canned-responses`, `/reply-lock`).
- **Streaming export**: `POST /contacts/export` streams `application/x-ndjson` (one masked contact per line) from the service's bounded
  async enumerable and returns the audit event id in `X-TicketFlow-Audit-Event`. Reveal and export responses are `Cache-Control: no-store`.

### DTOs

Wire DTOs (`TicketFlow.Hosting.AspNetCore.Contracts`) follow one naming convention: request bodies end in `Request`, request-only
nested parts in `Input`, response bodies and response-only parts in `Response`, and parts used in both directions (attribute values,
attachments, channel bindings, grant subjects and scopes, skills, translations, survey questions and policies) in `Model`. They are
separate from domain records: identifiers are strings, enums camelCase
strings, closed sets use explicit `kind`/`type` discriminators with one populated payload (timeline events, attribute values, grant
subjects and scopes, action outcomes), and unions are never serialized. Nested definitions owned by a feature travel as that feature's
versioned codec JSON (`DefinitionResponse.Definition`): SLA and routing policies, automation rules, macros and canned responses, calendar
schedules, access conditions, live-chat widgets and assistant settings. Masked values are absent (`masked`/`revealable` flags plus
`maskedFields`); revealing is an explicit, audited call.

### Anonymous endpoints

The public portal (`/portals/{slug}`, collections, articles, search, view and feedback recording) and survey responses
(`POST /surveys/invitation`, `POST /surveys/respond`) need no principal; readers enforce portal visibility and token validity.
Survey tokens travel in request bodies, never in URLs (keep them in the link's fragment so they stay out of server logs and
`Referer`). **A rate-limiting policy is required in production**: set `AnonymousRateLimitingPolicy` to a policy registered with
`AddRateLimiter` (partitioned per client, for example by `RemoteIpAddress` after forwarded-headers processing, as the sample host does)
and call `UseRateLimiter()`; apply the same policy to the live-chat visitor hub and upload endpoint
(`MapTicketFlowRealtime().Visitor.RequireRateLimiting(...)`).

### Browser hosts

ASP.NET Core form antiforgery is not applied: endpoints never bind forms, and the CSRF rule above (JSON body or
`X-TicketFlow-Request` header on every authenticated `POST`) forces a CORS preflight for every state-changing request. Cookie hosts
should still use `SameSite=Lax` (or `Strict`) cookies and must not allow credentialed CORS from untrusted origins. Bearer-token hosts
are not affected.

### OpenAPI

`AddTicketFlowOpenApi()` adds a document (default name `ticketflow`) containing only TicketFlow endpoints, with stable operation ids
(`ListConversations`, `Reply`, ...), DTO schemas, a success response on every operation and the shared problem responses (400, 401,
403, 404, 409, 412, 413, 422, 428 on authenticated groups; 400, 404, 413, 422, 429 on anonymous ones); `MapTicketFlowOpenApi()` serves it at `/ticketflow/openapi/{documentName}.json`.
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
| `event-delivery` | `EventDeliveryWorker.RunOnceAsync` (integration event delivery) | 2 s |
| `event-retention` | `EventRetentionWorker.RunOnceAsync` | 1 h |
| `event-contacts` | `EventContactFollower.CatchUpAsync` per workspace | 10 s |
| `event-surveys` | `EventSurveyFollower.CatchUpAsync` per workspace | 30 s |
| `reporting-retention` | `ReportingRetentionWorker.RunOnceAsync` | 1 h |

Each loop runs in a fresh DI scope per run. A run that reports work starts the next run immediately (up to `MaximumDrainRuns`), then
waits `Interval`; every delay gets ±`Jitter` (default 20 %) so nodes do not run in lockstep. A failing run backs off exponentially from
`InitialBackoff` to `MaximumBackoff`. Per-workspace loops enumerate `IWorkspaceDirectory` (workspaces with settings; in-memory and
PostgreSQL implementations ship with the workspace stores) and process at most `MaximumWorkspacesPerRun` workspaces per run, keeping
the directory position between runs and wrapping at the end, so every workspace is covered across runs. A failing workspace is backed
off on its own (same exponential schedule) and skipped until then; the loop keeps its cadence and reports the failures as item failures
(Degraded health), never as a failed run.
Loops whose library service is not registered report `NotConfigured` and never run; `Enabled = false` reports `Disabled`. Host
shutdown cancels in-flight runs. `AddTicketFlowWorkerLoop<TService>(name, run)` hosts additional loops with the same scheduling,
health and telemetry.

**Service identities**: `AddTicketFlowWorkers()` validates at startup that the SLA worker client consumers trust
(`AutomationOptions`, `ReportingOptions`, `EventsOptions` `SlaWorkerClientId`) equals `SlaOptions.WorkerClientId` (host start fails
otherwise). `TicketFlowServiceIdentities.Collect(services)` returns the worker API clients of the registered features so hosts can
reserve them (the sample host adds them to `IdentityFlowPrincipalOptions.ReservedApiClientIds`).

**Package shape**: `TicketFlow.Hosting.Worker` references the feature implementation packages whose loops it hosts (conversations,
routing, SLA, automation, surveys, live chat, assist, events, reporting), so referencing it brings those assemblies along even when a
host registers only some features; unregistered features simply report `NotConfigured`. Hosts that want a smaller graph host loops
themselves (each library loop is a plain `RunOnceAsync`/`CatchUpAsync` call) or use `AddTicketFlowWorkerLoop`.

Leased library loops (outbox, routing, SLA, automation schedules, survey claims) are safe on several nodes; per-workspace followers are
idempotent and their checkpoints are compare-and-set.

**Health**: `TicketFlowWorkerHealthCheck` is unhealthy once a running loop failed `UnhealthyAfterFailures` (default 5) runs in a row,
degraded after any failed run, when the last run had item failures, or when a running loop has not completed a run for
`StaleAfterIntervals` (default 5) intervals (at least one minute); its data lists `loop: Status:runFailures:itemFailures` only.

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
connections pass `?workspace=acme`), staff identity links (E16), integration events (E15) and reporting (E14) with their loops hosted
by the workers, OpenAPI, `/health`, the per-IP anonymous rate limiter and the widget demo page (bundle served from
`clients/widget/dist`; build it with `npm run build`). Seeded data: workspace `acme`, live-chat inbox `web-chat` with widget key
`wk_demo`, team `tier1`, users `admin` (Owner), `agent1`, `agent2` (Agent), grant-holding API clients for the channel worker, routing
engine, SLA tracker and automation, a public help-center portal `help` with a published article and a CSAT survey, plus the UI demo
content (more inboxes, teams and staff, contacts, two weeks of history, current conversations with SLA risk, automation, articles and
CSAT responses; see [ui-blazor.md](ui-blazor.md)).

Authentication uses a **development-only** handler (`X-Dev-User` header or `dev_user` query parameter, no verification) that refuses to
run outside the Development environment; it emits IdentityFlow-shaped claims so `AddIdentityFlowRealtimePrincipals()` maps users exactly as it
would for real IdentityFlow sessions (`X-Dev-Api-Key: mcp-assistant` emits an API-key principal, mapped to the API client
`idf:mcp-assistant`). Replace it with IdentityFlow authentication for anything beyond local development.

## Verification

- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release` (includes a sweep over every mapped route: principals
  without grants never receive data, unfenced writes require `If-Match`, every OpenAPI operation declares success and problem responses)
- `dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Tests -c Release`
- `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Hosting/TicketFlow.Hosting.Worker.Stores.Postgres.Tests -c Release`
- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release` (the PostgreSQL case needs `TICKETFLOW_TEST_POSTGRES`)

## Not yet implemented

- Cross-node SignalR fan-out (backplane and `IRealtimeBus`), see [realtime.md](realtime.md).
