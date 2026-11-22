# T04 — Live chat identify hook

Epic: [E16 — External identities and account linking](../PLAN.md)

Status: **Completed**

## Goal

When a visitor identifies, link the anonymous visitor contact to the verified account so it (and its conversations) merges into the
account contact and the session continues on it.

## Dependencies

E16 T02, E7.

## Acceptance criteria

- Additive only: optional `IContactLinkingService? linking = null` constructor parameter on `LiveChatVisitorService` and
  `LiveChatOptions.LinkVisitorContactOnIdentify` (default true).
- Runs as the channel worker with the session's `WidgetVisitor` evidence; best effort (never fails identification, logs no ids).
- Earlier conversation is re-pointed to the account contact; later messages thread into it; unmerge moves it back.

## Verification

- `dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Tests -c Release`.

## Risks and boundaries

The channel worker needs `contact:link` and `contact:merge`; without them identification still succeeds (warning logged).

## Results

- `LiveChatVisitorService.LinkVisitorContactAsync`, `LiveChatOptions.LinkVisitorContactOnIdentify`, docs/live-chat.md note.
- `IdentityLinkingTests` (3): merge into existing account with conversation follow + session continuation + unmerge; visitor contact
  becomes the account contact; best effort / disabled.
- `dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Tests -c Release` — 30 passed, 0 failed.
