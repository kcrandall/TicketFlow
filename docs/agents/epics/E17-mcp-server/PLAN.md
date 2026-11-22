# E17 — MCP server toolsets

Status: **Completed**

Role-oriented Model Context Protocol (MCP) tools over the TicketFlow feature services that applications plug into **their own** MCP
server, behind **their own** MCP authentication.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: `TicketFlow.Mcp` (tools only, official `ModelContextProtocol` C# SDK) with selectable toolsets (`conversations`, `contacts`,
`routing`, `sla`, `automation`, `helpcenter`, `surveys`, `reports`, `admin`, `access`, `assist`), always-on self-awareness tools
(`whoami`, `list_toolsets`, `describe_tools`, `explain_access`), a permission catalog per tool, optional tool-list filtering and
structured, bounded results; optional `TicketFlow.Mcp.AspNetCore` (`MapTicketFlowMcp()` over the SDK HTTP transport, host principal
resolution); sample-host wiring at `/mcp`; `docs/mcp.md`.

Boundaries:

- No parallel permission system. Tools execute as the MCP caller's `PrincipalRef` (resolved from the host-authenticated user) and call
  the feature services, so Access rules, masking, note visibility, revision fencing and audit apply unchanged. The per-tool permission
  catalog only *describes* requirements (tool-list marking/filtering, explanations, forbidden-error hints).
- Authentication stays with the host: the core package never authenticates; `MapTicketFlowMcp()` always calls `RequireAuthorization`
  (default or named policy).
- `TicketFlow.Mcp` depends on `ModelContextProtocol` and feature abstractions only (no ASP.NET Core). Result shapes follow the hosting
  DTO conventions (string identifiers, camelCase enum tokens, `kind` discriminators, masked values absent) but are MCP-owned records:
  the hosting DTO assembly requires the ASP.NET Core shared framework.
- SDK: `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` 2.4.0 (released 2026-11-07, more than two weeks before adoption).

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Tool infrastructure: toolsets, catalog, caller resolution, results, registration](tickets/T01-tool-infrastructure.md) | Completed | E2, E11a |
| [T02 — Self-awareness tools and tool-list filtering](tickets/T02-self-awareness-and-filtering.md) | Completed | E17 T01 |
| [T03 — Feature toolsets](tickets/T03-feature-toolsets.md) | Completed | E17 T01, E3–E14 |
| [T04 — ASP.NET Core endpoint package and sample host](tickets/T04-aspnetcore-and-sample.md) | Completed | E17 T01, E11a |
| [T05 — Documentation](tickets/T05-docs.md) | Completed | E17 T02–T04 |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors, with concurrent E15/E16/UI work in the tree);
`dotnet test tests/Mcp/TicketFlow.Mcp.Tests -c Release` (17 passed); `dotnet test tests/Mcp/TicketFlow.Mcp.AspNetCore.Tests -c Release`
(3 passed); `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release --filter FullyQualifiedName~Mcp` (1 passed; the existing
`InMemoryHostBootsSeedsAndServesAgentsAndVisitors` smoke test currently fails on the concurrent Blazor UI's static-assets manifest,
unrelated to E17). Package validation (`scripts/release.py validate-projects`, run with the not-yet-listed `TicketFlow.UI.Blazor`
excluded) — "Validated 65 package projects and central versions". No PostgreSQL store: the epic persists nothing. 70 tools in 12
groups. Parallel post-ticket reviews are still outstanding before commit.
