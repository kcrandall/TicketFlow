# Project structure

Use .NET 11, `TicketFlow.slnx`, central NuGet versions and mirrored MSTest projects. Add a package when a working optional slice exists.

The dependency-ordered public package inventory is maintained once in [scripts/nuget-projects.tsv](../scripts/nuget-projects.tsv). `scripts/verify-package-metadata.sh` checks solution membership, IDs, dependency order, package READMEs and central versions. Samples and test support are nonpackable.

## Source organization

- `src/TicketFlow.Core` owns scope, identifiers, principals, clock and validation primitives.
- `src/TicketFlow` is the meta-package (no concrete store).
- Feature directories (`Access`, `Workspaces`, `Contacts`, `Conversations`, `Routing`, `Sla`, `Automation`, `Channels`, `HelpCenter`, `Assist`, ...) contain `X.Abstractions`, `X` and `X.Stores.Postgres` projects.
- `src/Stores` owns `TicketFlow.Stores.InMemory` development stores and shared PostgreSQL migration support.
- `src/Integrations` adapts sibling Flow repositories and other optional frameworks.
- `src/Hosting` owns optional ASP.NET Core endpoints, realtime and worker hosting.
- `clients/` holds the live-chat widget and agent console (built separately from the .NET solution).
- `samples/` contains finite composition examples.

## Verification organization

`tests` mirrors the source domains. `tests/TicketFlow.Testing` owns shared helpers (random PostgreSQL schemas, fake clocks, principals). Store contract fixtures live in `tests/TicketFlow.StoreContracts`.

## Dependency rules

- Core has no ASP.NET Core, database, provider SDK or sibling Flow dependency.
- Feature behavior depends on its abstractions, Core and `TicketFlow.Access.Abstractions`.
- Stores implement feature interfaces and define atomicity, revision fencing, scope and retention guarantees.
- DI preserves application-supplied implementations.
