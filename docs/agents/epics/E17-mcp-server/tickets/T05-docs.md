# T05 — Documentation

Epic: [E17 — MCP server toolsets](../PLAN.md)

Status: **Completed**

## Goal

`docs/mcp.md`: plugging the tools into your own MCP server with your own auth (HTTP and stdio), toolsets and their tools, roles and
permissions, filtering, self-awareness tools, result/error conventions, sample host usage. Package READMEs.

## Dependencies

E17 T02–T04.

## Acceptance criteria

- Every toolset and tool is listed with its permissions; examples compile against the shipped API.

## Verification

- Review against `TicketFlowToolCatalog` (the catalog test asserts every tool name appears in `docs/mcp.md`).

## Risks and boundaries

None.

## Results

Wrote `docs/mcp.md` (authorization model, plugging in over HTTP and stdio, granting the agent a role, self-awareness tools, filtering,
result/error conventions, every toolset and tool with annotations and permissions, resources, sample host) and the two package READMEs.
`CatalogTests.TheGuideAndDocsNameEveryTool` asserts every catalog tool appears in `docs/mcp.md` and in the guide resource.
