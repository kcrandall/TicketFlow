# T01 — Channel contracts

Epic: [E6 — Channels and EnvelopeFlow integration](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Channels.Abstractions`: the `ITicketChannel` contract (kind, `ChannelCapabilities` flags with reply window, recipient
identity kinds, `SendAsync`), `IChannelRegistry`, the normalized `InboundChannelMessage` (scope, channel kind, binding configuration
id, external message/thread ids, In-Reply-To/References, sender identity evidence, bodies, attachment refs, received-at),
`OutboundChannelMessage` and the `ChannelSendResult = Accepted | Rejected(code, retryable) | Unknown` union,
`ChannelDeliveryReport`, reply-window evaluation, service contracts (inbound pipeline, reply service, delivery ingestor, endpoint
administration) with union results and `channel:read` / `channel:manage` permissions.

## Dependencies

E3 (inbox channel binding), E4 (identity kinds), E5 (conversation commands, outbox).

## Acceptance criteria

- Every record validates bounds (`Utf16.IsValid`, control characters, lengths) and redacts bodies/addresses from `ToString`.
- Results are C# unions; capabilities are validated (reply window only with the flag, 1 minute–30 days).

## Verification

- `dotnet test tests/Channels/TicketFlow.Channels.Tests -c Release` (model tests).

## Risks and boundaries

Contracts only; no transport code. Addresses and thread ids are correlation evidence, never authentication.

## Results

Implemented `TicketFlow.Channels.Abstractions`: `ITicketChannel`, `ChannelCapabilities`/`ChannelFeatures`, `IChannelRegistry`,
`InboundChannelMessage` + `ChannelSenderEvidence`, `OutboundChannelMessage` + `ChannelThreadContext` + `ChannelTemplate`,
`ChannelSendResult` union, `ChannelDeliveryReport`, `ChannelReplyWindow`/`ReplyWindowState`, service contracts with unions
(`InboundResult`, `DeliveryReportResult`, `ChannelReplyResult`, `ReplyWindowResult`, `ChannelResult<T>`), `ChannelPermissions`
(`channel:read`, `channel:manage` sensitive) and `ChannelText` (failure-code tokens, reference parsing, body sanitizing).
Model tests: `ModelTests` (8).

- `dotnet test tests/Channels/TicketFlow.Channels.Tests -c Release` — 35 passed, 0 failed (2026-11-22).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
