# TicketFlow

Catch-all meta-package for the TicketFlow stack. Installing `TicketFlow` pulls in `TicketFlow.Core`, every support
domain (access control, workspaces, contacts, staff identity, conversations, channels, live chat, mailbox, routing,
SLAs, automation, help center, surveys, assist, reporting, realtime, and integration events), the in-memory and
PostgreSQL stores, ASP.NET Core hosting, SignalR realtime, background workers, the Blazor management UI
(`TicketFlow.UI.Blazor`), and the MCP server toolsets (`TicketFlow.Mcp` and `TicketFlow.Mcp.AspNetCore`).

`AddTicketFlow()` returns a `TicketFlowBuilder` and registers `TimeProvider.System` only when the application has not
registered its own `TimeProvider`. All registrations remain explicit opt-ins via builder extensions so the host
controls which features, stores, routes, UI, and MCP toolsets are active.

Optional third-party and sibling-library adapters (`TicketFlow.Integrations.IdentityFlow`,
`TicketFlow.Integrations.IdentityFlow.SignalR`, `TicketFlow.Integrations.EnvelopeFlow`,
`TicketFlow.Integrations.WalFlow`, `TicketFlow.Integrations.InsightFlow`, `TicketFlow.Integrations.PostHog`,
`TicketFlow.Integrations.Segment`, and `TicketFlow.Assist.Providers.Claude`) remain explicit package opt-ins.
