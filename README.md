# TicketFlow

Modular customer support for .NET 11: workspaces, inboxes, contacts, conversations, routing, SLAs, automation and fine-grained access control, with omnichannel delivery through EnvelopeFlow and application-owned identity.

TicketFlow provides domain contracts, behavior and replaceable stores that applications compose with their own authentication and hosting. Optional packages add ASP.NET Core endpoints, hosted workers, realtime fan-out, a live-chat widget, a Blazor SSR agent console, reporting, an integration event stream and MCP toolsets.

## NuGet packages

TicketFlow `0.1.0-alpha.2.1` (67 packages + symbols) is published on [nuget.org](https://www.nuget.org/packages/TicketFlow). Install the composition entry point or pick individual feature, store and integration packages:

```bash
dotnet add package TicketFlow --prerelease
```

All packages share one lockstep version (`scripts/nuget-projects.tsv`; 72 packable packages on `main` including the post-`alpha.2.1` follow-up packages below).

## Implementation status

Epics E1–E17 and their documented follow-ups are implemented (see the [epic and ticket roadmap](docs/agents/epics/README.md)). In-memory stores are development examples; PostgreSQL stores (`*.Stores.Postgres`) are the durable implementations.

- **Foundation and access control** — scope, identifiers, principals; permissions, roles, scoped grants, conditions, masking, break-glass, decision audit and query filters ([access-control.md](docs/access-control.md)).
- **Workspaces, inboxes, teams and business calendars** ([workspaces.md](docs/workspaces.md)).
- **Contacts, identity graph and identity linking** — verified/claimed identities, masking, reveal, merge/unmerge, account linking suggestions and staff external identity mapping ([contacts.md](docs/contacts.md), [identity-linking.md](docs/identity-linking.md)).
- **Conversations and timeline** — revisioned timelines, commands, durable multi-consumer outbox with single and batch handlers plus a published CDC row contract ([conversations.md](docs/conversations.md)).
- **Channels** — channel runtime, signed API channel, EnvelopeFlow adapter (email, SMS, WhatsApp, Slack), and IMAP mailbox polling via MailKit with durable UID/UIDVALIDITY checkpoints ([channels.md](docs/channels.md), [envelopeflow-integration.md](docs/envelopeflow-integration.md)).
- **Live chat and realtime** — live-chat channel, visitor sessions, TypeScript web widget (`clients/widget`), SignalR hubs, and WalFlow CDC / cross-node realtime bus with PostgreSQL `LISTEN`/`NOTIFY` backplane ([live-chat.md](docs/live-chat.md), [realtime.md](docs/realtime.md)).
- **Routing and assignment** ([routing.md](docs/routing.md)), **SLA and calendars** ([sla.md](docs/sla.md)), **automation and macros** ([automation.md](docs/automation.md)).
- **Help center and surveys** ([help-center.md](docs/help-center.md), [surveys.md](docs/surveys.md)) and **assist** ([assist.md](docs/assist.md)).
- **Reporting and InsightFlow export** — exact reporting projections, 15-minute rollups, histogram percentiles, audited CSV export, and wire-format HTTP export to InsightFlow ([reporting.md](docs/reporting.md)).
- **Integration event stream** — CloudEvents v1 log, ordered subscriptions, signed webhooks, PostHog, Segment and WalFlow broker sinks ([events.md](docs/events.md)).
- **Hosting and Blazor UI** — ASP.NET Core endpoints with OpenAPI, hosted worker loops with health checks and telemetry, Blazor SSR management UI (`TicketFlow.UI.Blazor`), and a runnable sample host ([hosting.md](docs/hosting.md), [ui-blazor.md](docs/ui-blazor.md)).
- **IdentityFlow integration** — principal resolution and verified visitor identity tokens ([identityflow-integration.md](docs/identityflow-integration.md)).
- **MCP server toolsets** — Access-enforced MCP tools (including definition write tools), conversation/article resources and workflow prompts ([mcp.md](docs/mcp.md)).

## Design choices

- **Access first.** Permissions, roles, scoped grants (tenant → workspace → team → inbox), explicit deny, attribute conditions, field masking, time-bound grants and decision audit live in `TicketFlow.Access`. The same policy answers point checks and produces store query filters, so list/search/report paths never return rows a principal cannot see.
- **Conversations are timelines.** Messages, private notes, assignments, label/attribute changes, SLA events and assistant actions are ordered, revisioned timeline events.
- **Contacts are identity graphs.** Channel identifiers are verified or claimed evidence; merges and unmerges are audited.
- **Channels are adapters.** EnvelopeFlow supplies email, SMS, WhatsApp, Slack/Teams and push; TicketFlow adds live chat, an API channel and IMAP mailbox polling behind one channel contract.
- **Optional everything else.** Stores, hosting, realtime, assistant, reporting, event stream, UI and MCP adapters are separately selectable.

## Build and test

Use the .NET SDK pinned in `global.json`:

```bash
dotnet restore TicketFlow.slnx
dotnet build TicketFlow.slnx --configuration Release --no-restore
dotnet test TicketFlow.slnx --configuration Release --no-build
```

Set `TICKETFLOW_TEST_POSTGRES` to an isolated PostgreSQL database to run durable adapter tests, or run `bash scripts/test-postgres.sh`.

## Documentation

- [Architecture and extension boundaries](docs/architecture.md)
- [Access control model](docs/access-control.md)
- [Project structure and dependency rules](docs/project-structure.md)
- [Channels](docs/channels.md) & [EnvelopeFlow integration](docs/envelopeflow-integration.md)
- [Live chat](docs/live-chat.md) & [Realtime](docs/realtime.md)
- [Reporting](docs/reporting.md) & [Integration event stream](docs/events.md)
- [Hosting: endpoints, workers and sample host](docs/hosting.md) & [Blazor UI](docs/ui-blazor.md)
- [IdentityFlow integration](docs/identityflow-integration.md) & [Identity linking](docs/identity-linking.md)
- [MCP server toolsets](docs/mcp.md)
- [Agent workflow and review prompts](docs/agents/README.md)

## License

[MIT](LICENSE), copyright Kyle Crandall.
