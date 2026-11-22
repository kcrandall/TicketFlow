# T03 — Inbound pipeline, outbound dispatcher, delivery status and reply windows

Epic: [E6 — Channels and EnvelopeFlow integration](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Channels`: `InboundChannelPipeline` (endpoint → inbox with binding/enabled checks, sender normalization,
Contacts resolve-or-create, thread correlation by reference chain then channel thread, same-contact rule, start or
`AppendIncomingAsync` idempotently, channel state update), `ChannelOutboundDispatcher` as an E5 `IConversationOutboxHandler`
(reply outbox entries → channel send → `UpdateDeliveryStatusAsync`, dispatch records, retries with backoff, dead-letter, unknown
outcomes never retried), `ChannelDeliveryIngestor`, `ChannelReplyService` (refuses replies outside the reply window with
`ReplyWindowClosed`), `ChannelEndpointService` (`channel:manage` / `channel:read`), DI.

## Dependencies

E6 T01, T02; E3, E4, E5.

## Acceptance criteria

- Full inbound → conversation → reply → dispatch → delivery status round trip with in-memory stores; duplicates are replayed.
- Workers act as grant-holding API client principals (`ChannelsOptions.WorkerPrincipal`).
- Retryable rejections back off and dead-letter as Failed after `MaximumSendAttempts`; unknown outcomes are Failed without retry.

## Verification

- `dotnet test tests/Channels/TicketFlow.Channels.Tests -c Release`; PostgreSQL round trip in the Postgres test project.

## Risks and boundaries

No hosted worker (E11). Outbound attachments need channel support (capability check).

## Results

Implemented `InboundChannelPipeline`, `ChannelOutboundDispatcher` (`channels.dispatch`), `ChannelDeliveryIngestor`,
`ChannelReplyService`, `ChannelEndpointService`, `ChannelRegistry`, `ChannelsOptions` (worker principal, send attempts, claimed
matching) and DI. Tests: `InboundPipelineTests` (11), `RoundTripTests` (9: full inbound → reply → dispatch → receipt round trip,
retries/backoff, exhausted retries, permanent/unknown outcomes, interrupted attempts, recipient fallback, misconfigured worker,
endpoint administration, dispatch reads), `ReplyWindowTests` (4); PostgreSQL round trip in the Postgres project.

- `dotnet test tests/Channels/TicketFlow.Channels.Tests -c Release` — 35 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55606;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Channels/TicketFlow.Channels.Stores.Postgres.Tests -c Release` — 6 passed.

Review fixes (2026-11-22): (P1) `IChannelAddressOwnershipValidator` with default `CrossScopeAddressOwnershipValidator` refuses addresses nested under or enclosing another scope's address (`ChannelRefusal.AddressNotOwned`); (P2) per-contact thread lookup keeps one conversation per speaker in shared threads; (P1) provider `Retry-After` honoured — `ChannelDispatchRetryException` now derives from the new additive E5 `ConversationOutboxRetryException`, whose `RetryAfter` the outbox dispatcher uses as a release floor, and not-yet-due redeliveries do not send; (P3) inbound receive times clamped to now + 2 minutes; (P3) delivery conflicts map to retryable `DeliveryReportRejection.Conflict`; (P2) `IChannelWorkerPrincipalResolver` moved into Channels.Abstractions; (P3) `ChannelConflict(long)`. Regression tests: `ReviewRegressionTests` (7). `dotnet test tests/Channels/TicketFlow.Channels.Tests -c Release` — 42 passed; `tests/Conversations/TicketFlow.Conversations.Tests` — 46 passed.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
