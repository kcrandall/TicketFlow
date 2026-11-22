# T01 — SLA policy model, codec, permissions, policy store and service

Epic: [E9 — SLA and business calendars](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Sla.Abstractions` with `SlaPolicy` (ordered `SlaRule`s: `SlaCondition` over priority and labels, `SlaTargets` for
first response, next response and resolution, `SlaClockBasis` business/calendar time, `SlaEscalation`s with the `SlaAction` union),
`SlaPausePolicy`, a versioned `SlaPolicyCodec`, `SlaPermissions` (`sla:read`, `sla:manage`), `ISlaPolicyStore` and the
access-checked `ISlaPolicyService` in `TicketFlow.Sla`, plus a development in-memory policy store.

## Dependencies

E3 (inbox `SlaPolicyId`, business calendars), E2 (Access), E5 (priorities, labels).

## Acceptance criteria

- Bounded, validated model (≤ 32 rules, ≤ 16 escalations per rule, ≤ 8 actions per escalation, targets 1 minute–365 days in whole
  seconds, well-formed UTF-16 names); first matching rule wins.
- Codec round-trips every value with explicit `"v"`/discriminators and rejects unknown versions, kinds and properties.
- `sla:read`/`sla:manage` are registered by `AddSla()`; the service authorizes at workspace level before touching the store.
- Policy store: compare-and-set, ≤ 256 policies per workspace, scope-confined (shared contract).

## Verification

- `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release`.

## Risks and boundaries

Policies are referenced by inboxes through the opaque `SlaPolicyId`; deleting a policy does not check inbox references (a missing
policy means "no SLA").

## Results

Implemented `SlaPolicy`/`SlaRule`/`SlaCondition`/`SlaTargets`/`SlaEscalation`/`SlaAction` union/`SlaPausePolicy`, `SlaPolicyCodec`, `SlaPermissions` (`sla:read`, `sla:manage`, registered by `AddSla()`), `ISlaPolicyStore` (+ `InMemorySlaPolicyStore`) and `SlaPolicyService`. Tests: `ModelAndCodecTests` (validation, first-match, codec strictness), `SlaTrackerTests.PolicyServiceAuthorizesAtWorkspaceLevel`, `SlaStoreContract.VerifyPoliciesAsync` (in-memory and PostgreSQL).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E6/E8 work in the tree).
- `dotnet test tests/Sla/TicketFlow.Sla.Tests -c Release` — 26 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55609;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Sla/TicketFlow.Sla.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container `tf-pg-e9`) — 6 passed, 0 failed (inconclusive without PostgreSQL).
- `bash scripts/verify-package-metadata.sh` — fails only on E6's in-progress `src/Channels/TicketFlow.Channels` project not yet in the solution (not an E9 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
