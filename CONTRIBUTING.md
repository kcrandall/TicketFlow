# Contributing

Read [AGENTS.md](AGENTS.md) and the relevant [epic and ticket](docs/agents/epics/README.md) before changing behavior.
Keep application-owned auth, replaceable stores, access-checked features and optional channel/hosting packages intact.

Use the SDK in `global.json`, central package versions, and mirrored MSTest projects. Verify focused behavior first:

```sh
dotnet test tests/Access/TicketFlow.Access.Tests/TicketFlow.Access.Tests.csproj
dotnet build TicketFlow.slnx --configuration Release
dotnet test TicketFlow.slnx --configuration Release --no-build --no-restore
bash scripts/test-release-scripts.sh
```

When the package graph changes, update the solution, `scripts/nuget-projects.tsv`, and package README files; run pack and isolated
consumer verification as documented in [release/versioning](docs/agents/release-versioning.md).

Update ticket and epic statuses together, record real checks, and obtain the scoped post-ticket reviews before an authorized commit.
Never commit secrets, provider payloads or customer conversation content.

GitHub pushes and CI do not publish NuGet packages. Public publication requires the separate manual release workflow, which authenticates with NuGet trusted publishing.
