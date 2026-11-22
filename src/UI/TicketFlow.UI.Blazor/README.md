# TicketFlow.UI.Blazor

Optional Blazor (static SSR) management UI for TicketFlow: a shadcn-style dashboard, dark by default and themeable, for conversations,
contacts, inboxes, teams, business hours, channels, routing, SLAs, automation, help center, surveys, reports, access and settings.

```csharp
services.AddTicketFlow()
    /* features and stores */
    .AddTicketFlowEndpoints(options => options.Partition = partition)   // partition + principal resolvers
    .AddIdentityFlowPrincipals();                                       // or your own ITicketFlowPrincipalResolver
services.AddTicketFlowBlazorUI(options =>
{
    options.BasePath = "/support";
    options.Workspace = new WorkspaceId("acme");
    options.LoginPath = "/account/login";      // the host's sign-in; the UI never authenticates users itself
    options.LogoutPath = "/account/logout";
});

app.MapStaticAssets();
app.UseAuthentication();
app.MapTicketFlowBlazorUI();                   // pages, form actions, /reports/export.csv
```

- Inherits the host's authentication and maps users with the registered `ITicketFlowPrincipalResolver`.
- Sections are gated by a workspace-level Access snapshot (`IAccessEvaluator.LoadDecisionContextAsync`); features that are not
  registered are hidden. The services still authorize every read and write.
- Forms are SSR posts with antiforgery, revision fencing and friendly conflict/forbidden/validation messages.
- Themes: shadcn token names, built-in zinc/neutral/slate/stone/blue/violet/green/rose/orange in dark, light and system mode, persisted in a
  cookie; hosts add themes with `TicketFlowTheme` token overrides or CSS.
- Optional live conversation updates over the TicketFlow SignalR agent hub; everything works without JavaScript.
- Embed inside an existing Blazor layout with `<TicketFlowUI Path="@path" />` and `MapTicketFlowBlazorUI(pages: false)`.

See docs/ui-blazor.md.
