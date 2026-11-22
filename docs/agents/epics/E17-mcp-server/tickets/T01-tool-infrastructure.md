# T01 — Tool infrastructure: toolsets, catalog, caller resolution, results, registration

Epic: [E17 — MCP server toolsets](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Mcp`: `[Flags] TicketFlowToolsets`, `[TicketFlowToolset]` / `[TicketFlowTool(permissions...)]` attributes on SDK
`[McpServerToolType]` classes, a reflection-built `TicketFlowToolCatalog` (every tool, its toolset, required / any-of / optional
permissions, annotations, required feature service, built-in roles that grant it), `ITicketFlowMcpPrincipalResolver` (host-supplied;
options delegate fallback), `TicketFlowToolContext` (per-call security wrapper modelled on the nightfall `McpToolSecurity`: resolves
the caller for the requested workspace, maps union results to error results, never throws for domain outcomes) and
`IMcpServerBuilder.WithTicketFlowTools(options)`.

## Dependencies

E2, E11a.

## Acceptance criteria

- Registration adds only the selected toolsets plus the self-awareness tools; repeated calls do not duplicate tools. A call filter
  answers `toolset-unavailable` for tools whose feature service is not registered.
- Results are `CallToolResult`s with JSON structured content plus the same JSON as text; errors set `isError` with a stable `code`
  (`unauthenticated`, `forbidden`, `not-found`, `conflict` + `currentRevision`, `invalid`, `rejected`, `closed`, `merged`, ...).
  Forbidden errors name the tool's required permissions and the built-in roles that grant them.
- Results are bounded (`MaximumResultBytes`, per-text truncation with `truncated` flags); list tools return opaque `next` cursors.
- Validation exceptions from TicketFlow constructors become `invalid` (messages only from TicketFlow assemblies); nothing logs
  arguments, bodies or addresses.

## Verification

- `dotnet test tests/Mcp/TicketFlow.Mcp.Tests -c Release` (catalog completeness, registration, error mapping).

## Risks and boundaries

The catalog describes, it never decides: every call is decided by the feature service through Access.

## Results

Implemented `TicketFlow.Mcp`: `TicketFlowToolsets` (+ `Standard`, `All`) and `TicketFlowToolsetCatalog`, `[TicketFlowToolset]` /
`[TicketFlowTool(permissions, AnyOf, Optional, Service)]`, reflection-built `TicketFlowToolCatalog` (descriptors with permissions,
annotations, services, `GrantedByBuiltInRoles`; `KnownPermissions` from every referenced feature), `ITicketFlowMcpPrincipalResolver` +
`TicketFlowMcpCaller` (options delegate fallback), `TicketFlowToolContext` (per-call wrapper: workspace + principal resolution, error
mapping, TicketFlow-only validation messages, size bound, tool-name/outcome Debug logging), `TicketFlowToolError` / `TicketFlowToolResults`
/ `TicketFlowToolJson` (structured content + text, cursors, tokens), `TicketFlowMcpRegistry` and `WithTicketFlowTools(options)` /
`WithTicketFlowTools(toolsets)` (tools created per method with `McpServerTool.Create`, `_meta` permission metadata, call filter
`toolset-unavailable`, `ticketflow://guide` resource). Tests: `CatalogTests` (6).

- `dotnet test tests/Mcp/TicketFlow.Mcp.Tests -c Release` — 17 passed, 0 failed (2026-11-22).
