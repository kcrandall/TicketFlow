# T04 — Generic API channel with signed webhooks

Epic: [E6 — Channels and EnvelopeFlow integration](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Channels.Api`: inbound through application calls (`ApiChannelInbound`), outbound as an HMAC-SHA256 signed webhook
(timestamp + delivery id + body, replay window, `ApiWebhookVerifier` and a bounded replay guard for receivers), SSRF-safe
destinations (HTTPS, no user info, public addresses only checked at connect time, no redirects or proxies, optional host
allow-list), application-owned destination and secret resolution.

## Dependencies

E6 T01, T03.

## Acceptance criteria

- Tampered bodies, wrong keys, stale or future timestamps and replayed delivery ids are rejected.
- Private, loopback, link-local and metadata destinations are blocked before any connection.

## Verification

- `dotnet test tests/Channels/TicketFlow.Channels.Api.Tests -c Release`.

## Risks and boundaries

Signing keys are never stored by TicketFlow; receivers must deduplicate delivery ids durably in production.

## Results

Implemented `TicketFlow.Channels.Api`: `ApiChannel`, `ApiChannelInbound`, `ApiWebhookSignature`, `ApiWebhookReplayGuard`,
`ApiWebhookDestinationPolicy`, `ApiWebhookHttp` (connect-time public-address guard), `IApiChannelDestinationResolver`, `AddApiChannel`.
Tests: `ApiWebhookSignatureTests` (5: valid, tampering of body/id/timestamp/key/signature, window, malformed, replay guard) and
`ApiChannelTests` (5: signed payload, HTTP outcome mapping, blocked URIs, DNS-rebinding to private networks, pipeline round trip).

- `dotnet test tests/Channels/TicketFlow.Channels.Api.Tests -c Release` — 10 passed, 0 failed.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
