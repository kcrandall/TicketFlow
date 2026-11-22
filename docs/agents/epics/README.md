# Epic and ticket workflow

## Roadmap

| Epic | Status | Outcome |
| --- | --- | --- |
| [E1 — Foundation](E1-foundation/PLAN.md) | Planned | Core scope, identifiers, principals, DI, in-memory stores, test helpers, CI and packaging |
| [E2 — Access control](E2-access-control/PLAN.md) | Planned | Permission catalog, roles, scoped grants, deny, conditions, masking, query filters, break-glass and audit |
| [E3 — Workspaces, inboxes and teams](E3-workspaces-inboxes-teams/PLAN.md) | Planned | Workspace settings, inbox definitions, teams and membership, business calendars |
| [E4 — Contacts and identity graph](E4-contacts-and-identity-graph/PLAN.md) | Planned | Contacts, verified/claimed channel identities, audited merge/unmerge, PII-aware attributes |
| [E5 — Conversations and timeline](E5-conversations-and-timeline/PLAN.md) | Planned | Revisioned conversation timeline, messages, notes, labels, custom attributes, status/priority |
| [E6 — Channels and EnvelopeFlow integration](E6-channels-and-envelopeflow/PLAN.md) | Planned | Channel contract, EnvelopeFlow inbound/outbound adapter (email, SMS, WhatsApp, Slack/Teams), delivery status |
| [E7 — Live chat and realtime](E7-live-chat-and-realtime/PLAN.md) | Planned | Widget client, WebSocket gateway, visitor sessions, identity verification, presence/typing, SignalR fan-out via WalFlow |
| [E8 — Routing and assignment](E8-routing-and-assignment/PLAN.md) | Planned | Round robin, capacity, skills, sticky owner, overflow, fenced assignment, collision detection |
| [E9 — SLA and business calendars](E9-sla-and-calendars/PLAN.md) | Planned | First/next response and resolution targets, pause states, escalations, breach prediction |
| [E10 — Automation and macros](E10-automation-and-macros/PLAN.md) | Planned | Durable trigger/condition/action rules, dry-run, macros, canned responses |
| [E11 — Hosting and agent console](E11-hosting-and-agent-console/PLAN.md) | Planned | Optional ASP.NET Core endpoints, IdentityFlow adapter, React/TanStack agent console MVP |
| [E12 — Help center and surveys](E12-help-center-and-surveys/PLAN.md) | Planned | Articles, collections, locales, widget deflection, CSAT/NPS through EnvelopeFlow templates |
| [E13 — Assist](E13-assist/PLAN.md) | Planned | Pluggable assistant abstractions with Claude provider: summaries, drafts grounded in help center, triage; access/masking-aware context |
| [E14 — Reporting](E14-reporting/PLAN.md) | Planned | InsightFlow event emission and support reports (volume, response times, SLA, CSAT, agent performance) |

## Workflow

- Read `AGENTS.md`, the epic `PLAN.md` and the ticket before starting.
- Mark ticket and epic status together; record real verification commands under Results.
- After focused tests pass, run parallel post-ticket reviews from `docs/agents/prompts`.
