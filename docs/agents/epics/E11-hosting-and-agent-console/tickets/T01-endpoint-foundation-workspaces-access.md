# T01 — Endpoint foundation, workspaces and access administration

Epic: [E11 — Hosting and agent console](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Hosting.AspNetCore` (FrameworkReference `Microsoft.AspNetCore.App`): optional minimal-API endpoint groups over the
feature services. Foundation: `ITicketFlowPrincipalResolver` (HttpContext → `PrincipalRef` for a resolved `TicketScope`),
`ITicketFlowPartitionResolver` (host-provided tenant/project/environment/app; workspace from the `/workspaces/{workspaceId}` route
segment), an actor binder and group filter (401 without a principal), union → HTTP mapping (ProblemDetails with `code`, `errors`,
`currentRevision`; 403/404/409/412/422/428), strong `ETag` + `If-Match` revision fencing, request body size limits, optional group
authorization policy, anonymous rate-limiting hooks and OpenAPI document generation (`Microsoft.AspNetCore.OpenApi`) for the console's
typed client. JSON DTOs live in `TicketFlow.Hosting.AspNetCore.Contracts`, separate from domain records: no union JSON, explicit
`kind` discriminators, enums as camelCase strings. Complex nested definitions (calendar schedules, access conditions) travel as the
owning feature's versioned codec JSON.

First endpoint groups: `MapTicketFlowWorkspaces()` (settings, inboxes incl. inbox agents, teams incl. members, business calendars) and
`MapTicketFlowAccess()` (`/me` effective workspace permissions, roles, grants by target, field policies, break-glass).

## Dependencies

E1–E10, E12, E13.

## Acceptance criteria

- Every handler authorizes through the feature services (or, for access-store listings, an explicit `access:manage` point check);
  no endpoint reads stores without an Access decision.
- Writes on existing records require `If-Match` (428 when missing, 412 with `currentRevision` when stale); reads return `ETag`.
- Validation failures from domain constructors and codecs map to 422 without echoing input values.
- Endpoints work without IdentityFlow; hosts supply resolvers or option delegates.

## Verification

- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release` (TestServer: auth required, 403/404/409/412/428/422 and ETag).

## Risks and boundaries

Libraries keep working without hosting. Antiforgery is not applied (JSON APIs); cookie-authenticated hosts must add antiforgery or
SameSite/CORS protections — documented in docs/hosting.md.

## Results

Implemented `TicketFlow.Hosting.AspNetCore`: `TicketFlowEndpointOptions`, `ITicketFlowPrincipalResolver`, `ITicketFlowPartitionResolver`
(options fallbacks), `TicketFlowActor`/`TicketFlowPublicScope` binders, `TicketFlowEndpointFilter` (413, 400/401/404 binding failures,
422 validation), `TicketFlowHttp` (problem details with `code`, strong ETags, `If-Match` 428/400/412), request-size metadata, optional
group authorization and anonymous rate limiting, `AddTicketFlowOpenApi()`/`MapTicketFlowOpenApi()` and the workspace and access groups
(`/me` with opt-in sensitive probing). Tests: `FoundationTests` (9) and `TeamsCalendarsAndAccessAdministration`.

Verification (2026-11-22, repository root):

- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release` — 18 passed, 0 failed.
- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s).
