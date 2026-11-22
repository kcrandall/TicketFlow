# Changelog

## Unreleased

- Add `IConversationOutboxBatchHandler` in `TicketFlow.Conversations.Abstractions` / `ConversationOutboxDispatcher` and batch projection in `TicketFlow.Events` (`EventProjector`).
- Add `ConversationOutboxRowCodec` published CDC row contract in `TicketFlow.Conversations.Abstractions`.
- Add `ConversationQueued` routing event, `QueueChange.Queued` realtime event, and `ticketflow.routing.queued.v1` integration event.
- Add `TicketFlow.Integrations.Segment` event sink for Segment `/v1/batch` with Basic auth, `messageId` deduplication and automatic payload splitting.
- Add `TicketFlow.Channels.Mailbox` and `TicketFlow.Channels.Mailbox.Stores.Postgres` (E6 T07): MailKit IMAP polling, MIME/References normalization, bounded streaming fetches, attachment storage seam, and durable UID/UIDVALIDITY + Message-Id deduplication.
- Add `TicketFlow.Integrations.WalFlow` (E7 T08): WalFlow `ISink` decoding `tf_conversation_outbox` inserts, versioned `RealtimePublicationEnvelope`, `CrossNodeRealtimeBus`, and `PostgresRealtimeBackplane` (`LISTEN`/`NOTIFY`) verified against PostgreSQL `wal_level=logical`.
- Add `TicketFlow.Integrations.InsightFlow` (E14 T06): wire-format HTTP reporting exporter matching InsightFlow's `ClientTrackBatchRequest` contract with host-configured scope, deterministic `eventId` deduplication and checkpointing over `IReportingStore.ListFactsAsync`.
- Extend `TicketFlow.Mcp` (E17 T03) with 22 definition write tools (automation rules, macros, canned responses, SLA policies, routing policies, access roles and field policies), Access-checked conversation/timeline/article resources, and 4 safety-framed support workflow prompts.

## 0.1.0-alpha.2.1 — 2026-11-22

- Initial NuGet prerelease of 67 packages (+ symbols) covering Epics E1–E17:
  - Foundation (`TicketFlow.Core`, `TicketFlow`, `TicketFlow.Stores.InMemory`, `TicketFlow.Stores.Migrations.Postgres`).
  - Access control (`TicketFlow.Access*`), Workspaces/Inboxes/Teams/Calendars (`TicketFlow.Workspaces*`), Contacts & Identity Graph (`TicketFlow.Contacts*`), Identity Linking (`TicketFlow.Identity*`).
  - Conversations & Revisioned Timeline (`TicketFlow.Conversations*`), Channels & Signed API Channel (`TicketFlow.Channels*`), EnvelopeFlow integration (`TicketFlow.Integrations.EnvelopeFlow*`).
  - Live Chat, SignalR Realtime & TypeScript web widget (`TicketFlow.Channels.LiveChat*`, `TicketFlow.Realtime*`, `clients/widget`).
  - Routing (`TicketFlow.Routing*`), SLA & Business Calendars (`TicketFlow.Sla*`), Automation & Macros (`TicketFlow.Automation*`).
  - Help Center & Surveys (`TicketFlow.HelpCenter*`, `TicketFlow.Surveys*`, `TicketFlow.Integrations.Surveys`), AI Assist with Claude provider (`TicketFlow.Assist*`).
  - Reporting (`TicketFlow.Reporting*`), Integration Event Stream with Webhooks, PostHog & WalFlow Sinks (`TicketFlow.Events*`, `TicketFlow.Integrations.PostHog`, `TicketFlow.Integrations.WalFlow.Sinks`).
  - ASP.NET Core Hosting, Hosted Workers, IdentityFlow integration, Blazor SSR UI (`TicketFlow.Hosting.*`, `TicketFlow.Integrations.IdentityFlow*`, `TicketFlow.UI.Blazor`), and MCP server toolsets (`TicketFlow.Mcp*`).
