# TicketFlow.Integrations.IdentityFlow.SignalR

SignalR side of the IdentityFlow adapter: `AddIdentityFlowRealtimePrincipals()` resolves agent hub connections
(`?workspace={workspaceId}`) like REST endpoints, and `AddIdentityFlowVisitorTokens()` validates IdentityFlow-issued JWTs as live-chat
visitor identity. See docs/identityflow-integration.md.
