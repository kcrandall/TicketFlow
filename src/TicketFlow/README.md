# TicketFlow

Provider-neutral dependency-injection composition. `AddTicketFlow()` returns a `TicketFlowBuilder` (defined in
`TicketFlow.Core`) and registers `TimeProvider.System` only when the application has not registered its own `TimeProvider`.

All registrations use TryAdd, so application-supplied implementations are preserved and repeated calls are safe. No store,
channel, authentication handler or HTTP route is selected; feature packages extend `TicketFlowBuilder` with explicit opt-ins —
for example `TicketFlow.Access` (`AddAccessControl()`, `AddInMemoryAccessStores()`) and `TicketFlow.Access.Stores.Postgres`
(`AddPostgresAccessStores(dataSource)`). Feature packages reference `TicketFlow.Core`, not this meta-package. Workspaces,
conversations and channels are roadmap work.
