# T03 — Contact service: commands, resolve-or-create, masking, search and export

Epic: [E4 — Contacts and identity graph](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Contacts`: `IContactService` with create/get/update (closed `ContactEdit` hierarchy)/delete, attribute definition
management, inbound resolve-or-create (`ContactResolution` = Matched | Created | Ambiguous | refused), masked `ContactView`
projections, reveal through `IAccessEvaluator.RevealAsync`, Access-filtered keyset search by name or identity prefix and a bounded,
audited export iterator. DI via `AddContacts()` and `AddInMemoryContactStores()`.

## Dependencies

E4 T02.

## Acceptance criteria

- Every read and mutation authorizes through Access; list/search/export use `IAccessQueryPlanner` filters in the store query.
- `contact.email`, `contact.phone`, `contact.social`, `contact.external-id` and `contact.attribute.<key>` are masked per Access field
  policies; sensitive attributes without a policy fail closed (masked, not revealable); identity-prefix search on a masked kind is
  refused.
- Resolve matches verified evidence first, then claimed evidence per `ClaimedIdentityMatching`; races on verified identities retry.

## Verification

- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release`.

## Risks and boundaries

No logging of addresses, attribute values or names. Resolve-or-create runs as a grant-holding principal (for example an API client
for channel ingestion); system/automation principals are denied by Access.

## Results

Implemented in `TicketFlow.Contacts`:

- `ContactService`: create/get/update (`ContactEdit` closed hierarchy, authorized against current and resulting labels)/delete (audited in the write batch), resolve-or-create (`ContactResolution` = Matched | Created | Ambiguous | Refused; claimed evidence never yields a verified match; ambiguity lists only readable candidates; identity races retry), masked `ContactView`s, reveal via `IAccessEvaluator.RevealAsync`, search with planned Access filters and bound opaque cursors (identity search refused for masked kinds), audited bounded export iterator.
- Sensitive attributes without a field policy fail closed (masked, not revealable).
- `ContactAttributeDefinitionService`, `ContactsOptions`, `AddContacts()`, `AddInMemoryContactStores()`.
- Tests: `ContactServiceTests`, `ResolutionTests`, `SearchAndExportTests`.

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
