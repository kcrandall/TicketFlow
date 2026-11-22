# TicketFlow.Hosting.AspNetCore

Optional ASP.NET Core minimal-API endpoints over the TicketFlow feature services.

```csharp
services.AddTicketFlow()
    /* features and stores */
    .AddTicketFlowEndpoints(options => options.Partition = new AccessPartition("tenant", "project", "prod", "web"))
    .AddTicketFlowOpenApi();
services.AddScoped<ITicketFlowPrincipalResolver, MyPrincipalResolver>();   // or AddIdentityFlowPrincipals()

app.MapTicketFlowEndpoints();   // or MapTicketFlowConversations(), MapTicketFlowContacts(), ...
app.MapTicketFlowOpenApi();     // /ticketflow/openapi/ticketflow.json
```

- Routes: `/ticketflow/api/workspaces/{workspaceId}/...` (authenticated) and `/ticketflow/public/workspaces/{workspaceId}/...`
  (public portal, survey responses; optional rate-limiting policy).
- Every handler authorizes through the feature services (Access); principals come from `ITicketFlowPrincipalResolver`, partitions from
  `ITicketFlowPartitionResolver`.
- Errors are problem details with a stable `code`; reads return `ETag: "{revision}"` and writes on existing records require `If-Match`
  (428 missing, 412 stale with `currentRevision`).
- DTOs are separate from domain records (explicit `kind` discriminators, camelCase enum strings, codec JSON for feature definitions).

See docs/hosting.md.
