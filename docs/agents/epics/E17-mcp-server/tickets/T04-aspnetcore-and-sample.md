# T04 — ASP.NET Core endpoint package and sample host

Epic: [E17 — MCP server toolsets](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Mcp.AspNetCore`: `WithTicketFlowHostPrincipals()` (MCP caller = `ITicketFlowPartitionResolver` +
`ITicketFlowPrincipalResolver` over the MCP request's `HttpContext`) and `MapTicketFlowMcp(pattern = "/mcp", policy)` over the SDK's
`MapMcp` with `RequireAuthorization`. Sample host: MCP endpoint at `/mcp` with the development handler (new `X-Dev-Api-Key` header
emits IdentityFlow API-key claims → `ApiClientPrincipal`), seeded API client `mcp-assistant` holding the built-in Agent role.

## Dependencies

E17 T01, E11a.

## Acceptance criteria

- Unauthenticated `/mcp` requests get 401 from the host's authorization; authenticated callers act as their resolved principal.
- The sample's MCP client sees `whoami` as `apiClient:mcp-assistant` with Agent permissions.

## Verification

- `dotnet test tests/Mcp/TicketFlow.Mcp.AspNetCore.Tests -c Release`
- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release`

## Risks and boundaries

The development handler stays Development-only.

## Results

Implemented `TicketFlow.Mcp.AspNetCore` (`WithTicketFlowHostPrincipals()` → `HostMcpPrincipalResolver` over
`IHttpContextAccessor`, `ITicketFlowPartitionResolver`, `ITicketFlowPrincipalResolver`; `MapTicketFlowMcp(pattern, policy)` =
`MapMcp(...).RequireAuthorization(...)`). Sample host: stateless MCP endpoint at `/mcp`, `Standard` toolsets, `DefaultWorkspace`,
`X-Dev-Api-Key` development header (IdentityFlow API-key claims), seeded `mcp-assistant` API client with the built-in Agent grant.

- `dotnet test tests/Mcp/TicketFlow.Mcp.AspNetCore.Tests -c Release` — 3 passed, 0 failed (401 unauthenticated, API client acting through
  Access with forbidden delete and `permitted` marks, Access-filtered empty list for an unknown client, host policy 403).
- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release --filter FullyQualifiedName~Mcp` — 1 passed (2026-11-22).
