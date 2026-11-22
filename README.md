# TicketFlow

Modular customer support for .NET 11: workspaces, inboxes, contacts, conversations, routing, SLAs, automation and fine-grained access control, with omnichannel delivery through EnvelopeFlow and application-owned identity.

TicketFlow provides domain contracts, behavior and replaceable stores that applications compose with their own authentication and hosting. Optional packages add ASP.NET Core endpoints, realtime fan-out, a live-chat widget and an agent console.

## Implementation status

Planned and in progress. See the [epic and ticket roadmap](docs/agents/epics/README.md). Nothing is published to NuGet yet.

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
- [Agent workflow and review prompts](docs/agents/README.md)

## License

[MIT](LICENSE), copyright Kyle Crandall.
