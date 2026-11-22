# E16 — External identities and account linking

Status: **Completed**

Customers and support staff can be referenced by external ids. Customers who first appear anonymously (widget visitor) or by email
only are later linked to an application account, and their earlier conversations follow; staff sign in through several providers
that map to one canonical `UserId`. Design and usage: [docs/identity-linking.md](../../../identity-linking.md).

## Completion gate

Public APIs implement the outcome with focused tests (in-memory and PostgreSQL), Release builds of the touched projects and the
solution, and parallel post-ticket reviews.

Outcome: verified account linking with merge-into-account (idempotent, concurrency-safe, audited, conversations follow), a
per-workspace Off/Suggest/AutoMerge linking policy with Access-filtered suggestions, the `ResolveCustomerAsync` login helper, the
live-chat identify hook, and authorized staff identity mapping with a resolver and optional agent profiles.

## Boundaries

- Customer linking lives in Contacts (public API additive; consumers unchanged). Staff identities live in the new `src/Identity`.
- LiveChat receives only an additive optional hook (constructor parameter + option).
- `TicketFlow.Integrations.IdentityFlow` (E11a) is not edited; its integration point is documented.
- No edits to existing migrations; Contacts migration 3 is appended.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Account linking model, contracts, permissions and codec](tickets/T01-linking-model-and-contracts.md) | Completed | E4 |
| [T02 — Contact linking service, policy and suggestions](tickets/T02-linking-service-policy-suggestions.md) | Completed | E16 T01 |
| [T03 — PostgreSQL linking store](tickets/T03-postgres-linking-store.md) | Completed | E16 T01 |
| [T04 — Live chat identify hook](tickets/T04-livechat-identify-hook.md) | Completed | E16 T02, E7 |
| [T05 — Staff identity mapping and agent profiles](tickets/T05-staff-identity-mapping.md) | Completed | E2, E3 |

## Verification record

2026-11-22, against an isolated `postgres:18` container (`TICKETFLOW_REQUIRE_POSTGRES=1`):

- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` — 96 passed (15 new linking tests).
- `dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release` — 20 passed (4 new).
- `dotnet test tests/Channels/TicketFlow.Channels.LiveChat.Tests -c Release` — 30 passed (3 new).
- `dotnet test tests/Identity/TicketFlow.Identity.Tests -c Release` — 5 passed; `tests/Identity/TicketFlow.Identity.Stores.Postgres.Tests` — 4 passed.
- Consumers unchanged: Conversations 46 + Conversations PostgreSQL 18, Channels 42, LiveChat PostgreSQL 4, Automation 42, Routing 48,
  Assist 28, Reporting 37, Sla 31, TicketFlow.Tests 6, Hosting.AspNetCore 18, Realtime.SignalR 6, Hosting.Worker 7, Integrations.Surveys 3 — all passed.
- `dotnet build TicketFlow.slnx -c Release` — 0 warnings, 0 errors.
- `bash scripts/verify-package-metadata.sh` fails on `src/Events/TicketFlow.Events` (E15, concurrently in progress: listed in the tsv,
  not yet in the solution); unrelated to E16.

Parallel post-ticket reviews (docs/agents/prompts/post-ticket-*.md) are still outstanding before commit.

## Review fixes (2026-11-22, after commit 4574992)

1. Explicit links refuse combining two accounts of one namespace held by the account contact (`SharesNamespaceWithOtherAccount`
   before `MergeAsync`, matching auto-merge/accept). Test `ExplicitLinksNeverCombineTwoAccountsOfOneNamespace`.
2. Suggestions come from verified claims only by default; `LinkingSuggest(IncludeClaimedEvidence)` / `LinkingAutoMerge(..., IncludeClaimedEvidence)`
   opt in (codec `"claimed":true`); claimed suggestions are marked (`RequiresConfirmation`) and need
   `AcceptSuggestionAsync(..., confirmClaimedEvidence: true)` (new overload; the old one keeps working). Tests
   `ClaimedEvidenceSuggestsOnlyWhenOptedInAndAcceptNeedsConfirmation`, updated policy/codec/AutoMerge/reject tests.
3. Perf: contact-filtered suggestion listing uses `candidate_key`/`account_key` index scans (UNION ALL, keyset); retention
   `PruneDecidedSuggestionsAsync` (both stores) with migration 4's partial `updated_at` index; policy candidate checks share one
   `IAccessDecisionContext`. Tests `SuggestionStatementsUseIndexes` (EXPLAIN), `VerifyPruneAsync` contract (in-memory + PostgreSQL),
   `PolicyCandidatesShareOneDecisionContext`.
4. `ContactChangeKind.IdentityLinked` / `IdentityUnlinked` appended with account links/unlinks (migration 4 widens the check); events
   follower maps `IdentityLinked` → `ContactIdentityLinkedData(contact, "external-user", true)` (one additive line in
   `src/Events/TicketFlow.Events/EventFollowers.cs`). Tests `LinkAndUnlinkAppendIdentityChangeKinds`, `IdentityChangeKindsPersist`.
5. Staff identity results renamed to one prefix: `StaffIdentityWriteApplied|Taken|NotFound|Rejected|Conflict`,
   `StaffIdentityProfileWriteApplied`, unions `StaffIdentityWriteResult` / `StaffIdentityProfileWriteResult`.

Verification (isolated `postgres:18`, `TICKETFLOW_REQUIRE_POSTGRES=1`): Contacts 100, Contacts PostgreSQL 23, Identity 5, Identity
PostgreSQL 4, LiveChat 30, Conversations 46, Conversations PostgreSQL 18 — all passed. `src/Events` and `src/Mcp` currently fail to
build from those fixers' in-progress changes (unrelated errors); the E16 call sites compile.
