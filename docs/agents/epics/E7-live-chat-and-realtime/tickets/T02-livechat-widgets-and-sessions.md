# T02 — Live chat widgets, visitor sessions and identity verification

Epic: [E7 — Live chat and realtime](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Channels.LiveChat` contracts and security core: widget configuration per inbox (allowed origins, theme refs, pre-chat
fields, away message, deflection, attachment limits, identity policy), `ILiveChatWidgetService` (`livechat:read`/`livechat:manage`
on the inbox), store contracts for widgets and sessions with an in-memory development store, visitor session tokens (random,
SHA-256 at rest, expiry, rotation with a short grace window, constant-time verification), origin policy, HMAC identity verification
(application-resolved inbox secret) and `IVisitorTokenValidator`.

## Dependencies

E6 T01–T03, E7 T01.

## Acceptance criteria

- Tokens are never stored; tampered, expired, revoked and rotated-out tokens fail; verification is constant time.
- Origins match exactly (scheme, host, port); HMAC and signed-token identification yield a verified `ExternalUser` identity.

## Verification

- `dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Tests -c Release`.

## Risks and boundaries

TicketFlow never stores identity secrets; the IdentityFlow token adapter is E11.

## Results

Implemented in `TicketFlow.Channels.LiveChat`: `LiveChatWidget` (origins, theme ref, pre-chat fields, greeting/away overrides,
deflection, attachment and identity policies), `ILiveChatWidgetService` (`livechat:read`/`livechat:manage` on the inbox, binding checks),
`ILiveChatWidgetStore`/`ILiveChatSessionStore` with `InMemoryLiveChatStore`, `VisitorSessionTokens` (256-bit secrets, SHA-256 at rest,
constant-time verification, rotation grace), `LiveChatOrigin`, `VisitorIdentityHmac`, `ILiveChatIdentitySecretResolver`,
`IVisitorTokenValidator`, `LiveChatCodec`.

- `dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Tests -c Release` — 24 passed, 0 failed (includes token, rotation, origin, HMAC,
  signed-token, widget administration, model validation and in-memory store contract tests).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
