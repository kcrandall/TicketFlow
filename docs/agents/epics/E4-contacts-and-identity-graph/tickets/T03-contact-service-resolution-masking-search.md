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
