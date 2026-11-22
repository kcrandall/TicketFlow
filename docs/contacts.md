# Contacts and identity graph

Contacts unify channel identities without treating addresses as authentication. The feature ships as three packages:

| Package | Contents |
| --- | --- |
| `TicketFlow.Contacts.Abstractions` | `Contact`, identities and normalisation, typed attributes, merge records, change events, audit events, views, union results, service and store contracts, `ContactJsonCodec`. |
| `TicketFlow.Contacts` | `ContactService`, `ContactAttributeDefinitionService`, `ContactLinkingService`, `ContactMerger`, DI (`AddContacts`, `AddInMemoryContactStores`) and development in-memory stores (including `InMemoryContactLinkingStore`). |
| `TicketFlow.Contacts.Stores.Postgres` | `PostgresContactStore` (also `IContactLinkingStore`), `PostgresContactMigrations`, `AddPostgresContactStores`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl()
    .AddPostgresAccessStores(dataSource)
    .AddContacts(options => options.ClaimedIdentityMatching = ClaimedIdentityMatching.WhenUnique)
    .AddPostgresContactStores(dataSource);
await new PostgresContactStore(dataSource).InitializeAsync();
```

## Model

- `Contact`: id, `ContactProfile` (display name, avatar reference, locale, time zone, company), typed custom attributes (≤ 128),
  labels (≤ 64), identities (≤ 64), blocked flag, created/updated timestamps and `MergedInto` for merge tombstones. `StoredContact`
  adds the full `TicketScope` and revision.
- `ContactAttributeDefinition(key, type, sensitive, displayName)`: text, number, boolean, date or list. The type is immutable.
  Values are the `ContactAttributeValue` union; persisted JSON uses explicit `"t"` discriminators.
- `ContactIdentity(address, verification)` with `IdentityVerification = IdentityVerified(source, at) | IdentityClaimed(source, at)`.

## Identity normalisation

`ContactIdentityAddress.Normalize(kind, value, namespace?, defaultCallingCode?)` returns `ContactIdentityAddress | ContactAddressInvalid`
and never echoes the input in errors or `ToString`.

| Kind | Namespace | Normalisation |
| --- | --- | --- |
| Email | none | Trimmed; IDN domain via `IdnMapping` (STD3 rules), whole address lowercased; local part ≤ 64, address ≤ 254; no quoted local parts or domain literals. |
| Phone | none | E.164: `+` or `00` prefix, or an explicit 1-3 digit default calling code (one trunk `0` dropped); separators `space - . ( ) /`; 7-15 digits, no leading 0. No phone metadata library. |
| WhatsApp | none | As phone; bare international digits (WhatsApp IDs) accepted. |
| WidgetVisitor | optional token | Opaque, exact, ≤ 256, no control characters, well-formed UTF-16. |
| ExternalUser | required issuer token | Opaque, exact (for example an IdentityFlow subject). |
| SocialHandle | required network token | Leading `@` removed, lowercased, no whitespace. |

## Identity graph rules

- Verified identities are unique per (scope, kind, namespace, normalized value) across live contacts; claimed identities may repeat.
  Stores enforce this atomically (`ContactIdentityTaken`).
- **Resolve-or-create** (`IContactService.ResolveAsync`): verified evidence matching a verified holder is a `Verified` match;
  claimed evidence, or a claimed holder, is only ever a `Claimed` match, allowed when `ClaimedIdentityMatching.WhenUnique` and exactly
  one holder exists. Several candidates yield `ContactAmbiguous` (listing only contacts the caller may read); otherwise a contact is
  created. A concurrent verified-identity race retries so both callers converge on the winner. Matching needs `contact:read`,
  creation `contact:create`. Channel ingestion runs as a grant-holding principal (for example an API client); automation and system
  principals are denied by Access.
- **Merge** (`contact:merge` on both contacts and the result, sensitive): source identities missing on the target move; a verified
  source identity replaces a claimed target identity for the same address; attributes missing on the target are copied and conflicts
  keep the target unless `PreferSource`; labels union; empty profile fields fill from the source; the target becomes blocked when the
  source is. The source becomes a tombstone (`MergedInto`) with no identities and a `ContactMergeRecord` keeps its snapshot and the
  exact contributions.
- **Unmerge** restores the source snapshot and removes from the target only contributions that are unchanged since the merge
  (moved identities always return to the source). Chains unmerge newest first.
- Merge, unmerge and delete are one atomic store batch each, including the `ContactAuditEvent` and outbox change events.

## Account linking

Verified application accounts (`ExternalAccountRef(namespace, externalId)` → verified `ExternalUser` identity) are linked through
`IContactLinkingService` (E16): `LinkAccountAsync` attaches the account or merges the anonymous/email-only contact into the contact
already holding it (`ContactLinked | ContactMergedInto | ContactAlreadyLinked | ContactLinkConflict | ContactRefused`, idempotent,
concurrent links converge on one contact), `UnlinkAccountAsync`, the `ResolveCustomerAsync` login helper, the per-workspace
`ContactLinkingPolicy` (`LinkingOff | LinkingSuggest | LinkingAutoMerge`) and link suggestions (Access-filtered list, accept, reject).
Merges reuse `MergeAsync` (audited, conversations follow, undo with `UnmergeAsync`). Details: [identity-linking.md](identity-linking.md).

## Access

| Permission | Owner | Use |
| --- | --- | --- |
| `contact:read`, `contact:update`, `contact:export`, `contact:merge`, `contact:reveal` | Access built-ins (reused) | Read, edit, export (sensitive), merge/unmerge (sensitive), reveal (sensitive). |
| `contact:create` | Contacts | Create and resolve-create. |
| `contact:delete` (sensitive) | Contacts | Delete a contact and its merge history. |
| `contact-attribute:manage` (sensitive) | Contacts | Define attributes and their sensitivity. |
| `contact:link` (sensitive) | Contacts | Link/unlink verified accounts, resolve signed-in customers, review link suggestions. |
| `contact-linking:manage` (sensitive) | Contacts | Change the workspace linking policy. |

- The contact resource is `ContactAccess.Resource`: scope, contact id and labels; no team, inbox, assignee or condition attributes,
  so team/inbox grants never apply and attribute conditions fail closed. Mutations authorize against the current and resulting labels.
- **Masking**: `contact.email` (email), `contact.phone` (phone, WhatsApp), `contact.social`, `contact.external-id` (widget visitor,
  external user) and `contact.attribute.<key>` are removed from `ContactView`s per Access field policies. A sensitive attribute with
  no field policy applying to the permission is masked and not revealable (fail closed); `ContactFieldPaths.SensitivePolicy` builds
  the recommended policy. Reveal goes through `IAccessEvaluator.RevealAsync` (audited by Access).
- **Search** (`contact:read`): by folded name prefix or normalized identity prefix, keyset-paged (1-100 per page, opaque cursor
  bound to the search), with the planned `AccessFilter` inside the store query. Identity search on a kind masked for the caller is
  refused, so prefixes cannot become an oracle for masked values. Pages may be short; a contact matching several identities can
  appear on more than one page of an identity search.
- **Export** (`contact:export`, sensitive): justified, audited (`ContactAuditKind.Exported`) before streaming, bounded by
  `ContactsOptions.MaximumExportContacts` (default 10,000), streamed in keyset pages and masked per `contact:export`. Every exported
  contact's decision is audited by Access (sensitive decisions are never cached) and `ExportCompleted` records the emitted count.
- **Identity edits** on a field masked or unreadable for the actor are refused with a uniform `Forbidden`; `IdentityTaken` is only
  reported when the actor can read the holder with that field unmasked.
- **Delete** authorizes `contact:delete` on every cascaded tombstone and emits a `Deleted` change per removed contact.
- Searches examine at most 1000 rows per page (store-configurable) and return a continuation cursor when that bound is hit.

## Change events

Audit kinds: `Merged`, `Unmerged`, `Exported`, `Deleted`, `ExportCompleted`, `Linked`, `Unlinked`, `LinkSuggested`,
`LinkSuggestionAccepted`, `LinkSuggestionRejected`, `LinkingPolicyChanged` (`ContactAuditEvent.Suggestion` names the suggestion).

Every write appends `ContactChange`s (`Created`, `Updated`, `Deleted`, `Merged`, `Unmerged`; identifiers only) in the write
transaction with a gapless sequence per scope. Durable consumers (for example E5 conversations following merges) poll
`IContactStore.ReadChangesAsync(scope, afterSequence, limit)`. `IContactChangeObserver`s are notified in process after commit; an
observer failure propagates but the change stays applied.

## Store contracts

| Contract | Guarantees |
| --- | --- |
| `IContactStore` | Scope-confined; atomic `ContactWriteBatch` (≤ 8 contacts, ≤ 16 changes) fenced per contact revision; creates at revision 1; verified identity uniqueness; recursive tombstone cascade on delete; merge record create/delete; gapless per-scope outbox; filtered keyset search. |
| `IContactAttributeDefinitionStore` | Compare-and-set; type immutable; ≤ 256 definitions per workspace, checked atomically. |
| `IContactAuditSink` | Durable before returning; failures throw. |
| `IContactLinkingStore` | Scope-confined; CAS policy; one pending suggestion per (candidate, account) pair, atomically; CAS suggestion decisions; Access-filtered bounded listing over both contacts. |

`tests/Contacts/TicketFlow.Contacts.StoreContracts` verifies these against the in-memory stores and PostgreSQL. In-memory stores are
bounded, process-local development examples. `PostgresContactStore` uses `tf_key_v1` keys, a partial unique index for verified
identities, `"C"`-collated `(scope_key, name_folded, contact_id)` and `(scope_key, kind, value, contact_id)` search indexes, explicit
`format_version` columns and parameterized SQL. Migration 3 (`account-linking`) widens the audit kind check, adds
`tf_contact_audit.suggestion_id`, `tf_contact_linking_policies` and `tf_contact_link_suggestions` (partial unique pending-pair index).
Custom `IContactStore` implementations must also register an `IContactLinkingStore`, because `AddContacts` registers
`IContactLinkingService`.
