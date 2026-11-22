# T03 — Feature toolsets

Epic: [E17 — MCP server toolsets](../PLAN.md)

Status: **Completed**

## Goal

Thin tools over the feature services: `conversations` (list, get, timeline, start, reply, note, assign, team, status, priority,
labels, delete), `contacts` (search, get, create, update profile, reveal, merge, unmerge, link suggestions), `routing` (queue,
agents, agent status), `sla` (status, risk list, policies), `automation` (rules, dry run, macros list/run, canned responses
list/render), `helpcenter` (search, portals, articles, read, draft create/update, publish/unpublish), `surveys` (definitions,
responses, aggregates), `reports` (overview, leaderboard, trends), `admin` (workspace, inboxes, teams and membership, calendars,
channel endpoints), `access` (roles, grants, field policies, create/revoke grant), `assist` (summary, draft, triage).

## Dependencies

E17 T01, E3–E14.

## Acceptance criteria

- Every tool calls exactly the feature service the HTTP endpoints call, as the caller; masked values stay absent; notes appear only
  with `note:read`; writes on existing records accept `expectedRevision` and conflicts return `currentRevision`.
- Annotations: reads `readOnly` + `idempotent`; deletes, merges, grant changes, macro runs and team-member removal `destructive`.
- Per-tool tests: forbidden for a role lacking the permission, masked contact identity stays masked, round trip of a write.

## Verification

- `dotnet test tests/Mcp/TicketFlow.Mcp.Tests -c Release`.

## Risks and boundaries

Contact linking tools depend on E16's `IContactLinkingService` (concurrent work); they are registered only when that service is.

## Results

Implemented 66 feature tools: conversations (12), contacts (10, incl. E16 link suggestions over `IContactLinkingService`), routing (4),
sla (3), automation (6), helpcenter (8), surveys (3), reports (3), admin (9), access (5), assist (3). Every tool calls the same service
as the HTTP endpoints, as the caller; union results map to error codes; non-read-only tools declare `destructiveHint` explicitly.
Tests: `ToolTests` (7: round trip as an API client, forbidden with required permissions/roles per role, masking and note visibility,
unauthenticated/invalid/not-found mapping, toolset-unavailable, truncation and size bound, coverage across every toolset with services).

- `dotnet test tests/Mcp/TicketFlow.Mcp.Tests -c Release` — 17 passed, 0 failed (2026-11-22).

Follow-up completed (2026-11-22): added 22 definition write tools (automation rules, macros, canned responses, SLA policies, routing policies, access roles and field policies), Access-checked conversation/timeline/article MCP resources (`ticketflow://conversations/{conversationId}`, `.../timeline`, `ticketflow://helpcenter/articles/{articleId}`), and 4 safety-framed workflow prompts (`triage_conversation`, `draft_customer_reply`, `summarize_conversation`, `investigate_sla_risk`). Verified with `TicketFlow.Mcp.Tests` (23 passed; all 92 tools exercised by name, output schema and exact permission enforcement).

Review fixes: `AllToolsTests.EveryToolIsCalledByNameAndReturnsItsDeclaredOutputShape` calls all 70 tools (assist on its authorization path; no model provider is configured) and checks results against each `outputSchema`. `EveryDeclaredPermissionIsWhatTheServiceEnforces` grants a role holding every catalog permission except the tool's declared ones and expects `forbidden` (or an empty, Access-filtered list). `ToolTests.CustomerAuthoredTextIsDelimitedAsUntrustedAndTheRuleIsStated` covers the untrusted wrapping. `dotnet test tests/Mcp/TicketFlow.Mcp.Tests -c Release` — 21 passed.
