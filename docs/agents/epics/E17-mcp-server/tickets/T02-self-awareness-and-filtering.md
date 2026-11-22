# T02 — Self-awareness tools and tool-list filtering

Epic: [E17 — MCP server toolsets](../PLAN.md)

Status: **Completed**

## Goal

Always-registered tools: `whoami` (principal kind/id, scope, teams, own grants summary, held workspace permissions; sensitive
permissions only on request because each decision is audited), `list_toolsets`, `describe_tools` (every TicketFlow tool across all
toolsets, including ones not plugged in, with description, permissions, granting built-in roles, whether the caller holds them and
whether the toolset is enabled on this server) and `explain_access(permission | tool, resource?)` (allowed / masked / denied with
reason, no resource data). Tool-list filtering: `ShowAll` (default; tools carry `_meta` with permissions and `permitted`) or
`HideUnpermitted`.

## Dependencies

E17 T01.

## Acceptance criteria

- `describe_tools` lists every catalog tool; `enabled` is false for unselected toolsets or missing services.
- `whoami` never probes sensitive permissions unless `includeSensitive` is true; `explain_access` on a conversation first reads it as
  the caller (not visible → `not-found`, no decisions leaked).
- Filtering evaluates workspace-level decisions for non-sensitive permissions only (sensitive tools stay listed unless
  `ProbeSensitivePermissions` is enabled).

## Verification

- `dotnet test tests/Mcp/TicketFlow.Mcp.Tests -c Release` (whoami, explain_access, describe_tools, filtering via an in-memory MCP
  client/server round trip).

## Risks and boundaries

Workspace-level decisions do not reflect narrower team/inbox grants (same caveat as `GET /me`); the server still enforces every call.

## Results

Implemented `whoami`, `list_toolsets`, `describe_tools`, `explain_access` (`SelfTools`) and the `tools/list` filter
(`TicketFlowToolListFilter`: removes disabled tools, marks `ticketflow/permitted`, `HideUnpermitted`, sensitive probing opt-in) over
`TicketFlowPermissionProbe` (batch decision contexts at workspace level). Tests: `SelfToolTests` (4: whoami audit behaviour,
describe_tools completeness and verdicts, explain_access reasons/masking/not-found, ShowAll marking and HideUnpermitted).

- `dotnet test tests/Mcp/TicketFlow.Mcp.Tests -c Release` — 17 passed, 0 failed (2026-11-22).
