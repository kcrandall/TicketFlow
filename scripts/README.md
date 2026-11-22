# Build and release tools

Run tools from any working directory. Bash, Python 3 (standard library only), and the SDK selected by `global.json` are required.

```sh
bash scripts/test-release-scripts.sh
bash scripts/verify-package-metadata.sh
bash scripts/pack.sh --version 0.1.0-alpha.1 --prerelease alpha
bash scripts/verify-package-contents.sh --package-dir artifacts/nuget
bash scripts/smoke-package-consumption.sh --package-dir artifacts/nuget
```

`pack.sh` restores, builds, tests, and packs the dependency-ordered projects in `nuget-projects.tsv`. It requires a fresh output directory and writes `release-manifest.json` with SHA-256 checksums for every package and symbol package. Use `--skip-tests` only after the corresponding test run. `--validate-graph-only` validates the package graph without packing.

`resolve-version.sh --version VERSION --prerelease alpha|beta|stable` validates canonical SemVer without build metadata. A bare version receives the requested alpha/beta suffix; an existing suffix must match the requested channel. Stable rejects prerelease versions. Omit `--version` to use `TicketFlowVersion` from root build props.

`verify-package-metadata.sh` checks source solution membership, effective package IDs, dependency order, package README files, and central package-version usage. `verify-package-contents.sh` checks the complete package/symbol set, internal dependency versions, metadata, README, assembly/XML documentation, and unchanged checksums. The consumer smoke restores into an isolated cache with exact-version references and source mappings that confine TicketFlow IDs to the local feed, then builds/runs an external application referencing every local package. `pack.sh` selects the repository working directory before invoking .NET so `global.json` controls SDK selection from any caller directory.

Only the manually dispatched main-branch NuGet workflow calls `publish-nuget.sh`. Public publication requires explicit confirmation and uses NuGet trusted publishing (GitHub OIDC), so no API-key secret is stored. It publishes the tested artifacts without rebuilding and refuses duplicate package versions. See [release versioning](../docs/agents/release-versioning.md) for trusted publishing and environment setup. Pushing main does not publish NuGet packages.

## Local PostgreSQL tests

`bash scripts/test-postgres.sh` runs the solution tests with a dedicated, digest-pinned PostgreSQL 18 container, randomized loopback port and bounded connection pool, exposed to tests through `TICKETFLOW_TEST_POSTGRES`. It removes only its own labeled container after success or failure and accepts additional `dotnet test` arguments. It never accesses existing databases or publishes anything. Without `TICKETFLOW_TEST_POSTGRES`, PostgreSQL contract tests report Inconclusive rather than passing.
