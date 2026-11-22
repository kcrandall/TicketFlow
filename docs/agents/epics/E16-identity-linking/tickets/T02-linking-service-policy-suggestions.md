# T02 — Contact linking service, policy and suggestions

Epic: [E16 — External identities and account linking](../PLAN.md)

Status: **Completed**

## Goal

`ContactLinkingService`: `LinkAccountAsync` (attach the verified account, or merge the named/evidenced contact into the account
holder through `IContactService.MergeAsync`), `UnlinkAccountAsync`, `ResolveCustomerAsync`, policy get/set, suggestion
list/accept/reject; `InMemoryContactLinkingStore`; DI.

## Dependencies

E16 T01.

## Acceptance criteria

- Idempotent links (by contact, tombstone or moved evidence); `OtherAccount` and `AmbiguousEvidence` conflicts; masked
  `contact.external-id`/evidence fields refuse (no oracle).
- Two simultaneous links of one account end on one account contact; concurrent first logins create one contact.
- Policy: Off does nothing; Suggest stores one pending suggestion per pair; AutoMerge merges only on verified claims meeting the rules,
  never on claimed-only evidence, falls back to a suggestion without `contact:merge`; never crosses scopes.
- Suggestions listed with the `contact:read` Access filter on both contacts; accept merges and records the merge id; reject audited.
- Unmerge (existing) restores the anonymous contact.

## Verification

- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release`.

## Risks and boundaries

Merges reuse `MergeAsync` so authorization, merge records, audit and change events (and the conversation follower) are unchanged.
Suggestion decisions are written after the merge (separate store); a lost decision write leaves the merge applied and the suggestion
is superseded on the next accept.

## Results

- `ContactLinkingService`, `InMemoryContactLinkingStore`, `AddContacts`/`AddInMemoryContactStores` registrations.
- Tests (`ContactLinkingTests`, 15): link/merge/idempotency/unmerge, conflicts and authorization, 10-round concurrent link convergence,
  resolve-customer (claims, upgrades, creation permission), concurrent resolves, session-contact linking, Off/Suggest/accept,
  reject + Access filtering, AutoMerge rules, scope isolation, unlink, in-memory store contract.
- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` — 96 passed, 0 failed.
