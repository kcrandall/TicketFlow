# Epic and ticket workflow

## Roadmap

| Epic | Status | Outcome |
| --- | --- | --- |
| [E1 — Foundation](E1-foundation/PLAN.md) | Completed | Core scope, identifiers, principals, DI, in-memory stores, test helpers, CI and packaging |
| [E2 — Access control](E2-access-control/PLAN.md) | Completed | Permission catalog, roles, scoped grants, deny, conditions, masking, query filters, break-glass and audit |
| [E3 — Workspaces, inboxes and teams](E3-workspaces-inboxes-teams/PLAN.md) | Completed | Workspace settings, inboxes, teams, inbox agents as grants, DST-safe business calendars |
| [E4 — Contacts and identity graph](E4-contacts-and-identity-graph/PLAN.md) | Completed | Contacts, verified/claimed identity graph, audited merge/unmerge, masking, change log |
| [E5 — Conversations and timeline](E5-conversations-and-timeline/PLAN.md) | Completed | Revisioned union timeline, outbox with leased consumers, Access-filtered lists, contact merge follower |
| [E6 — Channels and EnvelopeFlow integration](E6-channels-and-envelopeflow/PLAN.md) | Completed | Channel contract, EnvelopeFlow email/SMS/WhatsApp/Slack adapter, signed API channel, IMAP mailbox polling channel |
| [E7 — Live chat and realtime](E7-live-chat-and-realtime/PLAN.md) | Completed | Live-chat channel, visitor sessions, realtime fan-out, SignalR hubs, TypeScript widget, WalFlow CDC & cross-node realtime bus |
| [E8 — Routing and assignment](E8-routing-and-assignment/PLAN.md) | Completed | Round robin, least busy, skills, sticky owner, overflow, fenced exactly-once assignment, collision presence |
| [E9 — SLA and business calendars](E9-sla-and-calendars/PLAN.md) | Completed | Business-time SLA targets, pause states, escalations, durable timers, risk lists |
| [E10 — Automation and macros](E10-automation-and-macros/PLAN.md) | Completed | Union triggers/actions, loop-safe idempotent engine, dry-run/replay, macros, canned responses |
| [E11 — Hosting and agent console](E11-hosting-and-agent-console/PLAN.md) | Completed | ASP.NET Core endpoints, hosted workers, IdentityFlow adapter, sample host, Blazor SSR management UI |
| [E12 — Help center and surveys](E12-help-center-and-surveys/PLAN.md) | Completed | Help center with safe rendering and full-text search; CSAT/NPS surveys |
| [E13 — Assist](E13-assist/PLAN.md) | Completed | Microsoft.Extensions.AI assistant with Claude provider, masking-safe context, quotas |
| [E14 — Reporting](E14-reporting/PLAN.md) | Completed | Exactly-once reporting projection, rollups, percentiles, audited export, wire-format InsightFlow HTTP exporter |
| [E15 — Event stream](E15-event-stream/PLAN.md) | Completed | Versioned public events, batched outbox projection, ordered subscriptions, webhooks, PostHog, Segment and WalFlow broker sinks |
| [E16 — Identity linking](E16-identity-linking/PLAN.md) | Completed | Customer account linking/merging policies and staff external identity mapping |
| [E17 — MCP server](E17-mcp-server/PLAN.md) | Completed | MCP toolsets (incl. definition write tools), conversation/article resources and prompts executing as the caller's TicketFlow principal |

## Workflow

- Read `AGENTS.md`, the epic `PLAN.md` and the ticket before starting.
- Mark ticket and epic status together; record real verification commands under Results.
- After focused tests pass, run parallel post-ticket reviews from `docs/agents/prompts`.
