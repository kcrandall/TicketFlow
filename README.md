# TicketFlow

Modular customer support for .NET 11: workspaces, inboxes, contacts, conversations, routing, SLAs, automation and fine-grained access control, with omnichannel delivery through EnvelopeFlow and application-owned identity.

TicketFlow provides domain contracts, behavior and replaceable stores that applications compose with their own authentication and hosting. Optional packages add ASP.NET Core endpoints, realtime fan-out, a live-chat widget and an agent console.

## Implementation status

Implemented (see the [epic and ticket roadmap](docs/agents/epics/README.md); nothing is published to NuGet yet):

- **Foundation and access control** — scope, identifiers, principals; permissions, roles, scoped grants, conditions, masking, break-glass, decision audit and query filters ([access-control.md](docs/access-control.md)).
- **Workspaces, inboxes, teams and business calendars** ([workspaces.md](docs/workspaces.md)).
- **Contacts and identity graph** — verified/claimed identities, masking, reveal, merge/unmerge, export ([contacts.md](docs/contacts.md)).
- **Conversations and timeline** — revisioned timelines, commands, durable outbox ([conversations.md](docs/conversations.md)).
- **Channels** — channel runtime, API channel, EnvelopeFlow adapter ([channels.md](docs/channels.md), [envelopeflow-integration.md](docs/envelopeflow-integration.md)).
- **Live chat and realtime** — live-chat channel, widget, SignalR hubs ([live-chat.md](docs/live-chat.md), [realtime.md](docs/realtime.md)).
- **Routing and assignment** ([routing.md](docs/routing.md)), **SLA and calendars** ([sla.md](docs/sla.md)), **automation and macros** ([automation.md](docs/automation.md)).
- **Help center and surveys** ([help-center.md](docs/help-center.md), [surveys.md](docs/surveys.md)) and **assist** ([assist.md](docs/assist.md)).
- **Hosting** — ASP.NET Core endpoints with OpenAPI, hosted workers with health checks and telemetry, a runnable sample host ([hosting.md](docs/hosting.md)).
- **IdentityFlow integration** — principal resolution and visitor identity tokens ([identityflow-integration.md](docs/identityflow-integration.md)).

In progress or planned: reporting (E14) and the React/TanStack agent console (E11 T07). In-memory stores are development examples; PostgreSQL stores are the durable implementations.

## Design choices

- **Access first.** Permissions, roles, scoped grants (tenant → workspace → team → inbox), explicit deny, attribute conditions, field masking, time-bound grants and decision audit live in `TicketFlow.Access`. The same policy answers point checks and produces store query filters, so list/search/report paths never return rows a principal cannot see.
- **Conversations are timelines.** Messages, private notes, assignments, label/attribute changes, SLA events and assistant actions are ordered, revisioned timeline events.
- **Contacts are identity graphs.** Channel identifiers are verified or claimed evidence; merges and unmerges are audited.
- **Channels are adapters.** EnvelopeFlow supplies email, SMS, WhatsApp, Slack/Teams and push; TicketFlow adds live chat, an API channel and mailbox polling behind one channel contract.
- **Optional everything else.** Stores, hosting, realtime, assistant and reporting adapters are separately selectable.

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
- [Hosting: endpoints, workers and the sample host](docs/hosting.md)
- [IdentityFlow integration](docs/identityflow-integration.md)
- [Agent workflow and review prompts](docs/agents/README.md)

## License

[MIT](LICENSE), copyright Kyle Crandall.
