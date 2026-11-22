# T03 — Live chat channel and visitor service

Epic: [E7 — Live chat and realtime](../PLAN.md)

Status: **Completed**

## Goal

`LiveChatChannel` (`ITicketChannel`, kind `livechat`) pushes dispatched agent replies to the visitor session topic and keeps them
fetchable with a sequence cursor; `ILiveChatVisitorService` runs visitor messages through the E6 inbound pipeline as the channel
worker, links conversations to the session, enforces pre-chat, attachments through `IAttachmentStore` (size/type limits, session
ownership), typing and read receipts, deflection through `IHelpCenterSearch.SearchPublicAsync` and the business-hours-aware widget
state; `ILiveChatAgentService` exposes masked visitor context to agents.

## Dependencies

E7 T01, T02, E12 (help center search).

## Acceptance criteria

- End to end in memory: visitor message → conversation → agent reply → dispatcher → visitor receives the push and can refetch it.
- A visitor cannot read, type into or mark another session's conversations; notes never reach visitors.

## Verification

- `dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Tests -c Release`.

## Risks and boundaries

Read receipts are realtime events plus a stored visitor cursor; they are not mirrored into message delivery state.

## Results

Implemented `LiveChatVisitorService`, `LiveChatChannel` (`ITicketChannel` kind `livechat`), `LiveChatSessionDirectory`
(`IVisitorSessionDirectory`), `LiveChatAgentService` (masked visitor context), `IAttachmentStore` + `InMemoryAttachmentStore`, `AddLiveChat`,
`AddInMemoryLiveChatStores`. End-to-end in memory: visitor message → pipeline → conversation → agent reply → dispatcher → visitor push →
refetch after cursor; isolation between sessions; pre-chat; attachments (type, size, ownership); deflection; business-hours away message;
disabled inbox; masking of pre-chat answers.

- `dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Tests -c Release` — 24 passed, 0 failed.

Review fixes (2026-11-22): `LiveChatChannel` and `LiveChatSessionDirectory` skip revoked/expired sessions; ending a session closes all
of its connections.
- `dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Tests -c Release` — 27 passed (new: revoked sessions get no pushes or agent
  typing; ending a session closes every tab and later calls are refused).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
