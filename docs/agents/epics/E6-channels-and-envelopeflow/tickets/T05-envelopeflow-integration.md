# T05 — EnvelopeFlow integration

Epic: [E6 — Channels and EnvelopeFlow integration](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Integrations.EnvelopeFlow` referencing EnvelopeFlow abstraction packages at the pinned central version
`0.1.0-alpha.3.1`: an `IInboundEventHandler` mapping `InboundEvent` messages to `InboundChannelMessage` and delivery receipts to
delivery reports; email, SMS, WhatsApp and Slack channels over EnvelopeFlow transports with threading (email In-Reply-To/References,
WhatsApp reply id and 24-hour window, Slack thread ts); destination encoding for WhatsApp/chat binding resolvers; an
`ISurveyInvitationSender` via the conversation's EnvelopeFlow channel.

## Dependencies

E6 T01–T03; E12 surveys; EnvelopeFlow `0.1.0-alpha.3.1` on nuget.org.

## Acceptance criteria

- Restore works against nuget.org; fakes for every transport; email/SMS/WhatsApp/Slack round trips with threading assertions.

## Verification

- `dotnet test tests/Integrations/TicketFlow.Integrations.EnvelopeFlow.Tests -c Release`.

## Risks and boundaries

No EnvelopeFlow.Conversations dependency. Feedback events are ignored (suppression belongs to EnvelopeFlow feedback packages).

## Results

Implemented `TicketFlow.Integrations.EnvelopeFlow` against `EnvelopeFlow.Core`, `.Inbound.Abstractions`, `.Email/.Sms/.WhatsApp/.Chat.Abstractions`
`0.1.0-alpha.3.1` (central `PackageVersion` entries; restored from nuget.org): `EnvelopeFlowInboundEventHandler`, email/SMS/WhatsApp/chat
channels, `EnvelopeFlowDestinations` + `EnvelopeFlowDestinationContextResolver`, `EnvelopeFlowSurveyInvitationSender`, DI. Tests with fake
transports (`EnvelopeFlowRoundTripTests`, 9): email threading round trip with receipts, SMS business-number endpoints and failure
codes, receipt grace retries, WhatsApp window and binding resolution, Slack thread ts, attachment references, unroutable events,
email retry advice/unknown outcomes, survey invitations over email and WhatsApp templates.

- `dotnet test tests/Integrations/TicketFlow.Integrations.EnvelopeFlow.Tests -c Release` — 9 passed, 0 failed.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
