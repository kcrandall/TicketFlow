# TicketFlow.Mcp

Model Context Protocol tools for TicketFlow, built on the official `ModelContextProtocol` C# SDK. Plug them into **your own** MCP
server, behind **your own** MCP authentication:

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport()                       // or WithStdioServerTransport(), ...
    .WithTicketFlowTools(options =>
    {
        options.Toolsets = TicketFlowToolsets.Conversations | TicketFlowToolsets.Contacts | TicketFlowToolsets.Reports;
        options.DefaultWorkspace = "acme";
        options.ResolvePrincipal = (caller, workspace, ct) => /* map caller.User to a PrincipalRef */;
    });
```

- Toolsets: `conversations`, `contacts`, `routing`, `sla`, `automation`, `helpcenter`, `surveys`, `reports`, `admin`, `access`
  (sensitive, not in `Standard`) and `assist`. Tools of toolsets whose feature services are not registered answer
  `toolset-unavailable` and are hidden from `tools/list`.
- Always on: `whoami`, `list_toolsets`, `describe_tools` (every tool, enabled here or not, with required permissions and granting
  built-in roles) and `explain_access`.
- Every call runs as the caller's `PrincipalRef` (from `ITicketFlowMcpPrincipalResolver`) through the feature services: Access grants,
  deny rules, conditions, masking, note visibility, revision fencing and audit apply unchanged. `TicketFlowToolCatalog` only
  describes permissions (tool-list marking/filtering, explanations, forbidden-error hints).
- Results are JSON structured content plus the same JSON text, bounded in size, with opaque `next` cursors; domain outcomes become
  error results with a stable `error.code` (`forbidden`, `not-found`, `conflict` + `currentRevision`, `invalid`, ...).

`TicketFlow.Mcp.AspNetCore` adds `WithTicketFlowHostPrincipals()` and `MapTicketFlowMcp()`. See `docs/mcp.md`.
