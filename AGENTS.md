# TicketFlow Agent Guide

TicketFlow is a modular .NET 11 customer-support framework: workspaces, inboxes, contacts, conversations, routing, SLAs, automation and fine-grained access control. Delivery channels (email, SMS, WhatsApp, Slack/Teams, push) come from EnvelopeFlow through an optional adapter; identity comes from IdentityFlow through an optional adapter.

## Boundaries
- Core owns scope (tenant -> project -> environment -> app -> workspace), identifiers, principals and provider-neutral primitives. Core never depends on ASP.NET Core, a database, a provider SDK or a sibling Flow repository.
- Feature abstractions own payloads and replaceable contracts; feature implementations own behavior; `*.Stores.Postgres` own durable persistence behind those contracts.
- `TicketFlow.Access` owns permissions, roles, scoped grants, conditions, masking and decision audit. Every feature that reads or mutates support data authorizes through Access, and list/search paths use Access query filters, never post-filtering of unbounded results.
- Channels are adapters behind `TicketFlow.Channels.Abstractions`. Transport details (SMTP, Twilio, Meta, ...) belong in EnvelopeFlow. Contact addresses and provider thread IDs are correlation evidence, never authentication.
- Applications own authentication and user schema. Optional hosting packages may expose endpoints/realtime, but libraries must work without them.
- Cross-repository dependencies live only in `src/Integrations/TicketFlow.Integrations.<Repo>` and reference the other repository's published abstraction packages by pinned central version.
- TicketFlow owns its architecture, domain model and roadmap. Chatwoot and other help desks are feature references only; never copy their code, schema or naming wholesale.

## Language
- Target net11.0 / C# 15. Prefer `union` types for closed result/decision sets (authorization decisions, command outcomes, channel capabilities) and `closed` record hierarchies for private ASTs. Switches over unions must be exhaustive without a discard arm.
- Do not expose unions directly as persisted JSON shapes; persisted/store envelopes use explicit versioned discriminators.

## Working Standards
- Prefer focused changes, async I/O with CancellationToken, file-scoped namespaces, nullable annotations, options and structured logging.
- Omit braces for single-statement if/else/loops unless declarations, comments, directives or dangling-else hazards require them.
- Use rg for searches. Never revert unrelated changes or use destructive git commands without authorization.
- Use central NuGet versions, net11.0, warnings as errors and the TicketFlow.slnx solution.
- Tests use MSTest and mirror source domains. Run focused dotnet test commands first, then solution build/test.
- Supplied in-memory stores are development examples. Do not imply durable guarantees. Custom-store contracts define required atomicity, revision fencing and scope enforcement.
- Do not log message bodies, contact addresses, masked attribute values, credentials or tokens.

## Planning And Review
- Plans live under docs/agents/epics/E#-name/PLAN.md with tickets/T##-name.md.
- Update the ticket and epic status together. Keep planned capabilities distinct from implemented ones.
- After code and focused tests pass, spawn parallel reviews before committing; use docs/agents/prompts/post-ticket-*.md with security, correctness, tests and perf/API slices.
- Reviewers report only high-confidence actionable findings with severity, file/line, impact and fix idea.

## Commits And Releases
- Commit subject style: `E2 T03 add scoped role grants`. Body: `Summary:`, `Message:`, `Verification:` with real commands.
- Work directly on main when the user requests it.
- Packages use lockstep versions from TicketFlowVersion. NuGet publication is an explicit manual release operation, separate from pushing to GitHub.
- CI is manual-only from main. Do not add push, pull-request, schedule or dependency-bot triggers without user authorization.
