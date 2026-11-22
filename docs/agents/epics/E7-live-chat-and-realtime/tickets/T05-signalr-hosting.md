# T05 — SignalR hubs

Epic: [E7 — Live chat and realtime](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Realtime.SignalR` (FrameworkReference `Microsoft.AspNetCore.App`): an agent hub (host-authenticated `ClaimsPrincipal` →
`PrincipalRef` through `IRealtimePrincipalResolver`), a visitor hub (session token handshake, origin check) and an attachment upload
endpoint, wired to the realtime registry as per-connection sinks. Backplane guidance documented (Redis optional).

## Dependencies

E7 T01–T03.

## Acceptance criteria

- TestHost + SignalR client integration: visitor connects, sends, agent replies, visitor receives; unauthorized subscriptions and
  foreign origins are refused.

## Verification

- `dotnet test tests/Hosting/TicketFlow.Realtime.SignalR.Tests -c Release`.

## Risks and boundaries

Core and feature packages do not reference ASP.NET Core; only this hosting package does.

## Results

Implemented `TicketFlow.Realtime.SignalR`: `AgentRealtimeHub`, `VisitorRealtimeHub`, `SignalRRealtimeConnection`, `RealtimeWireFormat`
(`tf.event`, `"v":1`), `IRealtimePrincipalResolver`, `IVisitorPartitionResolver`, `MapTicketFlowRealtime`, `MapLiveChatAttachmentUpload`,
`AddTicketFlowSignalR`. Central versions added: `Microsoft.AspNetCore.TestHost` and `Microsoft.AspNetCore.SignalR.Client` 11.0.0 (tests).

- `dotnet test tests/Hosting/TicketFlow.Realtime.SignalR.Tests -c Release` — 3 passed, 0 failed (TestServer + SignalR client: visitor ↔
  agent round trip with typing, push, reconnect-with-cursor and read receipts; foreign origins, bad tokens, missing principals and
  unauthorized subscriptions refused; attachment upload authentication).

Review fixes (2026-11-22): server-side typing throttle per connection and conversation in both hubs (`TypingThrottleInterval`, default
2 s); optional agent hub origin allow-list (`AgentAllowedOrigins`, `RequireAgentOrigin`) checked in `OnConnectedAsync`, with CORS/SameSite
guidance in docs/realtime.md; SignalR connections are aborted when their visitor session ends.
- `dotnet test tests/Hosting/TicketFlow.Realtime.SignalR.Tests -c Release` — 6 passed (new: typing throttle; foreign agent origins
  rejected; ended session closes the other tab and refuses calls; store-revoked session refused mid-connection).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
