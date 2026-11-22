# Package versioning and release workflow

TicketFlow packages use one lockstep version from `TicketFlowVersion` in root `Directory.Build.props`; the first public prerelease (`0.1.0-alpha.2.1`, 67 packages) was published to nuget.org on 2026-11-22 via NuGet trusted publishing.

## Build conventions

The repository targets `net11.0` (C# 15), uses central package versions, nullable annotations, implicit usings, deterministic builds, and warnings as errors. `global.json` selects SDK `11.0.100` with `latestPatch` roll-forward and no prerelease SDKs. Tests use MSTest through shared test props and mirror source domains. Libraries ship MIT metadata, package README files, XML API documentation, SourceLink, and `.snupkg` symbols. SourceLink points to `https://github.com/kcrandall/TicketFlow`; source access remains subject to repository permissions.

The supported package graph is `scripts/nuget-projects.tsv`, with package ID and relative project path separated by a tab. Dependencies precede consumers. Every packable source project must belong to `TicketFlow.slnx` and the manifest, and every package ID must match `TicketFlow` or `TicketFlow.*`. A new package requires an entry, package README, solution membership, and any necessary central dependencies.

## Local verification

```sh
dotnet restore TicketFlow.slnx
dotnet build TicketFlow.slnx --configuration Release --no-restore
dotnet test TicketFlow.slnx --configuration Release --no-build --no-restore
bash scripts/test-release-scripts.sh
bash scripts/verify-package-metadata.sh
bash scripts/pack.sh --version 0.1.0-alpha.1 --prerelease alpha
bash scripts/verify-package-contents.sh --package-dir artifacts/nuget
bash scripts/smoke-package-consumption.sh --package-dir artifacts/nuget
```

`pack.sh` restores, builds, runs tests, and packs all supported libraries. It accepts `--configuration`, `--output`, `--skip-tests`, and `--validate-graph-only`. The output directory must be empty: choose a fresh directory (for example `artifacts/nuget-rehearsal`) for another rehearsal. The default is `artifacts/nuget`. Package creation writes a complete `release-manifest.json` with SHA-256 hashes for every `.nupkg` and `.snupkg`.

Verification checks the exact package set, versions, internal dependencies, required metadata, package README, assemblies, API documentation, symbols, and checksums. The consumer smoke restores exact versions into a fresh cache, mapping every TicketFlow package ID to the local artifact feed and external dependencies to NuGet.org, then builds/runs an external consumer referencing every package. `pack.sh` invokes .NET from the repository root so the selected SDK always comes from this repository's `global.json`. Regression tests cover version/channel validation, manifest paths and duplicates, missing/extra artifacts, metadata, internal dependency versions, checksum tampering, argument handling, local publish rejection, consumer feed confinement, and SDK selection from a foreign working directory.

## Versions

`resolve-version.sh --version VERSION --prerelease alpha|beta|stable` accepts canonical SemVer without build metadata. Bare versions receive the requested alpha/beta suffix. An existing prerelease must match that channel. Stable versions cannot have a prerelease identifier. Omitting `--version` uses `TicketFlowVersion`.

Examples: `0.1.0-alpha.1`, `0.1.0-beta.1`, `0.1.0`. CI and unspecified manual prereleases append the GitHub run number and attempt to avoid accidental artifact-version reuse. Explicit manual versions are kept exactly as resolved. Every package in one release receives the same version.

## GitHub Actions

`.github/workflows/ci.yml` runs only when manually dispatched from main (`workflow_dispatch` plus a `refs/heads/main` job guard). Commits, pull requests, and schedules do not start CI; do not add those triggers or dependency-bot configuration without owner authorization. It validates release tools and the package graph, restores, builds, tests against an ephemeral PostgreSQL 18 service, packs local prerelease artifacts, verifies contents/checksums, runs the isolated consumer smoke, and uploads packages and test results. CI never publishes NuGet packages.

`.github/workflows/nuget-publish.yml` is manually dispatched from main only. `target=dry-run` is the default and creates validated artifacts without publication. Public publication requires `target=public` and `confirm_public_publish=publish-public`. Stable public publication additionally requires `confirm_stable_publish` to equal the exact resolved version, such as `0.1.0`.

The build job creates and validates a complete immutable artifact set. The separate publish job downloads those artifacts, verifies their bytes again, and publishes them without rebuilding. Action revisions are pinned to commit SHAs. There is no scheduled dependency-update configuration; updates remain owner-initiated.

## Test infrastructure

Manual CI and the release build job start an ephemeral PostgreSQL 18 service and set `TICKETFLOW_TEST_POSTGRES`, because `pack.sh` runs the full solution tests, including PostgreSQL contracts once `*.Stores.Postgres` packages exist. There are no paid-provider checks in CI. Locally use `bash scripts/test-postgres.sh` or set `TICKETFLOW_TEST_POSTGRES` to an isolated database when validating durable adapters; missing configuration makes those tests Inconclusive, which is explicitly unavailable evidence, not a pass.

## Package validation baselines

All source libraries enable SDK package validation. Before a supported public release, select the exact prior published lockstep version as `PackageValidationBaselineVersion` for every package that existed in that release, review intentional public API changes and update the major/minor version policy accordingly. Newly added packages have no previous baseline. Do not invent a published baseline for an unpublished milestone. Archive checks and the isolated exact-feed consumer gate remain mandatory. During partial publication recovery, inventory exactly which version/hash pairs exist on the feed, stop on conflicting immutable bytes, and publish only verified missing artifacts after renewed explicit release authorization; never mask conflicts with `--skip-duplicate`.

## Publication setup

Publication uses [NuGet trusted publishing](https://aka.ms/nuget/trusted-publishing); no long-lived API key is stored in GitHub. The publish job requests `id-token: write`, and `NuGet/login` exchanges its GitHub OIDC token for a short-lived nuget.org API key for the nuget.org account `kcrandall`.

The nuget.org trusted publishing policy for TicketFlow is active (first exercised on 2026-11-22 in `0.1.0-alpha.2.1`) for account `kcrandall`, repository `kcrandall/TicketFlow`, workflow file `nuget-publish.yml`, and package pattern `TicketFlow*`. Renaming the workflow file or adding a package outside that pattern requires updating the policy on nuget.org first.

The publish job runs in the `nuget-stable` environment for stable releases and `nuget-prerelease` otherwise. Both environments are restricted to `main` via GitHub deployment branch policies and need no stored secrets.

NuGet publication is public regardless of GitHub repository visibility. It is separate from authorization to push main. Package versions are immutable, and publication deliberately omits `--skip-duplicate`. A partial publication requires investigation; the workflow must not silently accept a mixed release. `publish-nuget.sh` rejects execution outside a manually dispatched main-branch GitHub Actions run and rechecks confirmation, channel, artifact integrity, and presence of the trusted-publishing API key.
