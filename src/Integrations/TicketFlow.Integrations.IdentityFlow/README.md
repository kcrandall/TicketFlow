# TicketFlow.Integrations.IdentityFlow

Maps IdentityFlow authentications (browser sessions, bearer tokens, PATs, API keys) to TicketFlow principals for
`TicketFlow.Hosting.AspNetCore` endpoints, the Blazor UI and MCP: staff identity links first, then prefixed identifiers (API clients
`idf:` by default, reserved worker ids refused), team snapshots, required scopes for non-session principals. SignalR agent connections and
live-chat visitor tokens: `TicketFlow.Integrations.IdentityFlow.SignalR`. See docs/identityflow-integration.md.
