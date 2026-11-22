# T03 — PostgreSQL linking store

Epic: [E16 — External identities and account linking](../PLAN.md)

Status: **Completed**

## Goal

`PostgresContactStore` implements `IContactLinkingStore` through appended migration 3 (`account-linking`).

## Dependencies

E16 T01.

## Acceptance criteria

- Migration 3 widens the audit kind check (1-11), adds `tf_contact_audit.suggestion_id`, `tf_contact_linking_policies` and
  `tf_contact_link_suggestions` (`tf_key_v1` keys, `format_version`, partial unique pending-pair index, list indexes); migrations 1-2
  unchanged.
- Shared `ContactLinkingStoreContract` passes with two independent data sources; concurrent links/resolves converge on PostgreSQL.

## Verification

- `TICKETFLOW_TEST_POSTGRES=... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release`.

## Risks and boundaries

Suggestion creation serializes per scope with an advisory lock (pending capacity). Listing joins both contacts and translates the
Access filter twice with distinct parameter prefixes; untranslatable filters deny all.

## Results

- `PostgresContactStore.Linking.cs`, `PostgresContactMigrations` step 3, `AddPostgresContactStores` registers `IContactLinkingStore`.
- New tests: `LinkingPolicyContractUsesIndependentClients`, `LinkSuggestionContractUsesIndependentClients`,
  `LinkingAuditKindsAndSuggestionIdsPersist`, `ConcurrentLinksConvergeOnPostgres` (4 services over 2 data sources, 5 rounds); version
  assertions updated to 3.
- Against an isolated `postgres:18` container: 20 passed, 0 failed.
