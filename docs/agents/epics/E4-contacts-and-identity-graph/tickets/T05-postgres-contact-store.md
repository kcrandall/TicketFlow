# T05 — PostgreSQL contact store

Epic: [E4 — Contacts and identity graph](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Contacts.Stores.Postgres`: `PostgresContactStore` implementing every contact store contract with an append-only migration
catalog, `tf_key_v1` bounded keys, a partial unique index for verified identities, indexed keyset search with translated Access
filters and a per-scope gapless change sequence.

## Dependencies

E4 T02, E4 T04.

## Acceptance criteria

- Shared store contract passes against PostgreSQL with independent clients; concurrent verified-identity claims admit one winner.
- Search uses `(scope_key, name_folded, contact_id)` and `(scope_key, kind, value, contact_id)` indexes; no unbounded scans.

## Verification

- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Explicit schema creation only (`InitializeAsync`). Rows that fail to decode throw.

## Results

Implemented `TicketFlow.Contacts.Stores.Postgres`:

- `PostgresContactMigrations.Catalog` (`ticketflow.contacts` v1): contacts, identities (partial unique index on verified `identity_key`), attribute definitions, merges, change heads/changes, audit; `tf_key_v1` keys; `"C"` collation for sort/search columns.
- `PostgresContactStore`: one-transaction batches (ordered row locks, identity rows replaced after contact rows, `ON CONFLICT ... WHERE verified` for verified uniqueness, recursive tombstone cascade), keyset search with `PostgresAccessFilterTranslator` over a lateral constant row (no team/inbox/assignee, unknown attributes), gapless per-scope sequence via a head row, strict decoding.
- `AddPostgresContactStores(dataSource)`; explicit `InitializeAsync`.
- Tests: `PostgresContactStoreTests` (shared contract with independent clients, concurrent identity claims, gapless outbox across clients, idempotent init, 256-unit identifiers, batch audit atomicity, decode failure, index-backed plans, end-to-end service on PostgreSQL).

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E3 projects).
- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` — 72 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55474;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 12 passed, 0 failed.
- `bash scripts/verify-package-metadata.sh` — Validated 13 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.


### Post-ticket review fixes (2026-11-22)

Findings from the E4 review (after commit 3dac1d8), each with regression tests:

1. P2 masked-identity oracle: `UpdateAsync` refuses every Add/RemoveContactIdentity edit (including verified upgrades) on a field the actor's `contact:read` decision masks or denies, with one uniform `Forbidden` (no errors) before any presence check; `IdentityTaken` is reported only when the actor may read the holder with that field unmasked, otherwise `Forbidden` (create, update, resolve, unmerge). Tests: `MaskedIdentityEditsAreUniformlyForbidden`, `IdentityTakenIsHiddenWhenTheHolderIsUnreadable`.
2. P1 audit gap: the per-call decision cache never caches sensitive permissions (looked up in `IPermissionCatalog`, a new `ContactService` constructor dependency) or break-glass decisions, so each exported contact is audited by Access; export additionally records `ContactAuditKind.ExportCompleted` with the count actually emitted when the stream completes or is disposed. Tests: `ExportAuditsEverySameLabelContactAndTheEmittedCount` (3 same-label contacts: 4 export decisions, completed count 3; abandoned stream count 1), `SearchDecisionsAreStillCachedForNonSensitiveReads`.
3. Delete cascade: new `IContactStore.ListMergedIntoAsync`; the service authorizes `contact:delete` on every cascaded tombstone, passes the authorized set as `ContactDelete.ExpectedCascade` (stores return a conflict if the cascade differs), emits one `Deleted` change per removed contact (`Related` = root) and stores report removed ids in `ContactWriteApplied.Deleted`. Tests: contract `VerifyDeleteCascadeAsync` (in-memory and PostgreSQL), `DeleteAuthorizesAndReportsEveryCascadedTombstone`.
4. Unmerge authorizes `contact:merge` on the restored target before writing. Test: `UnmergeAuthorizesTheRestoredTarget`.
5. P3 label-filtered scans: searches examine at most `ContactSearch.DefaultMaximumRowsExamined` (1000, store-configurable) rows in index order per page and return a short page with a continuation cursor when the bound is hit. A GIN index on labels was not added: the shared Access translator emits `label = ANY(labels)`, which GIN cannot serve, and Access is owned by another agent. Test: contract `VerifyBoundedScanAsync` (both stores, bound 3).
6. Query plans: the store's real statements are built by internal `SearchCommand`, `FindByIdentityCommand` and `ReadChangesCommand`; `GeneratedStatementsUseIndexes` EXPLAINs them over 200 seeded rows with cursors and a translated filter (`tf_contacts_name`, `tf_contact_identities_search`, identity lookup index, `tf_contact_changes_pkey`, no sequential scans).
7. Tests: failed batches (fence conflict, identity taken) followed by success keep the change sequence contiguous (contract); `ConcurrentResolutionsRetryAndConvergeOnPostgres` (6 services over 2 data sources, 5 rounds, one contact created per round).
8. `AddContacts` no longer registers `TimeProvider` (`AddTicketFlow` owns it). Test: `AddContactsLeavesTimeProviderToAddTicketFlow`.

PostgreSQL migration 2 (`audit-export-completed`) widens the audit kind check for `ExportCompleted`.

Verification: `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` (81 passed); with `TICKETFLOW_REQUIRE_POSTGRES=1` against an isolated `postgres:18` container: `TicketFlow.Contacts.Stores.Postgres.Tests` 16 passed, `TicketFlow.Conversations.Tests` 36 passed, `TicketFlow.Conversations.Stores.Postgres.Tests` 15 passed.
