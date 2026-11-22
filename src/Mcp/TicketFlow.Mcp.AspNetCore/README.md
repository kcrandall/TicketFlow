# TicketFlow.Mcp.AspNetCore

Optional ASP.NET Core wiring for `TicketFlow.Mcp`.

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTicketFlowTools(options => options.DefaultWorkspace = "acme")
    .WithTicketFlowHostPrincipals();       // MCP caller = ITicketFlowPartitionResolver + ITicketFlowPrincipalResolver

app.UseAuthentication();
app.UseAuthorization();
app.MapTicketFlowMcp("/mcp", authorizationPolicy: "mcp");   // always RequireAuthorization (default or named policy)
```

- `WithTicketFlowHostPrincipals()` resolves the MCP caller from the MCP request's `HttpContext` with the same
  `ITicketFlowPartitionResolver` and `ITicketFlowPrincipalResolver` the HTTP endpoints use (for example
  `AddIdentityFlowPrincipals()`), so an API key maps to an `ApiClientPrincipal` holding ordinary Access grants.
- `MapTicketFlowMcp()` maps the SDK's Streamable HTTP endpoint and requires authorization; authentication schemes and policies
  stay with the host.
