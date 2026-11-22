# External identities and account linking

Customers and support staff can be referenced by external ids. Customers often first appear anonymously (a widget visitor) or by
an email address only and later sign in to an application account; their earlier conversations follow them. Staff sign in through
one or more identity providers that map to one canonical TicketFlow `UserId`.

| Package | Contents |
| --- | --- |
| `TicketFlow.Contacts.Abstractions` / `TicketFlow.Contacts` | Customer side: `IContactLinkingService` (`ContactLinkingService`), `ExternalAccountRef`, `ContactLinkResult`, `ResolveCustomerAsync`, `ContactLinkingPolicy`, link suggestions, `IContactLinkingStore` (`InMemoryContactLinkingStore`). |
| `TicketFlow.Contacts.Stores.Postgres` | `PostgresContactStore` also implements `IContactLinkingStore` (migration 3 `account-linking`). |
| `TicketFlow.Channels.LiveChat` | Additive hook: `IdentifyAsync` links the anonymous visitor contact (`LiveChatOptions.LinkVisitorContactOnIdentify`). |
| `TicketFlow.Identity.Abstractions` / `TicketFlow.Identity` | Staff side: `StaffIdentityRef`, `IStaffIdentityService`, `IStaffIdentityResolver`, `StaffProfile`, `IStaffIdentityStore` (`InMemoryStaffIdentityStore`). |
| `TicketFlow.Identity.Stores.Postgres` | `PostgresStaffIdentityStore` (`ticketflow.identity` migrations). |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddWorkspaces().AddPostgresWorkspaceStores(dataSource)
    .AddContacts().AddPostgresContactStores(dataSource)          // registers IContactLinkingService + IContactLinkingStore
    .AddConversations().AddPostgresConversationStores(dataSource) // the merge follower re-points conversations
    .AddStaffIdentity().AddPostgresStaffIdentityStores(dataSource);
await new PostgresContactStore(dataSource).InitializeAsync();
await new PostgresStaffIdentityStore(dataSource).InitializeAsync();
```

## Customer accounts

An account is `ExternalAccountRef(namespace, externalId)`: an issuer token and the exact identifier the application authenticated.
It is stored as a **verified** `ExternalUser` contact identity, so at most one live contact per scope holds it (store-enforced).
Addresses remain correlation evidence; only the caller-authenticated account is trusted.

### LinkAccountAsync

`LinkAccountAsync(actor, new ContactLinkRequest(account, verificationSource, contact: id | evidence: address))` names the contact
directly or through evidence (for example the `WidgetVisitor` identity of the session that just signed in). The scope is the actor's
(`PrincipalRef.Scope`), as everywhere in TicketFlow.

| Situation | Result |
| --- | --- |
| No contact holds the account | The account is attached to the contact as verified (`ContactLinked`, audited `Linked`, `Updated` change). |
| Another contact holds it | The named contact is merged **into** the account contact through `IContactService.MergeAsync` (`ContactMergedInto(target, source, mergeId)`); audited `Merged` + `Linked`; `Merged`/`Updated` changes so conversations follow. |
| The contact already holds it, or it is a tombstone merged into the holder, or the evidence already moved there | `ContactAlreadyLinked` (nothing written) — retries are idempotent. |
| The contact holds a **different** verified account of the same namespace (as the requested account or as any other account the holder has) | `ContactLinkConflict(OtherAccount)` — two accounts are never combined (same rule as auto-merge and accept). |
| The evidence matches several contacts | `ContactLinkConflict(AmbiguousEvidence, readable candidates)`. |
| Concurrent writers keep winning (8 attempts) | `ContactLinkConflict(Contended)`. |
| Not permitted / nothing found | `ContactRefused` (`Forbidden`, `NotFound`, …). |

Concurrency: two simultaneous links of one account race on the verified-identity uniqueness; the loser's write fails with
`ContactIdentityTaken`, it retries, finds the winner and merges into it. Every racer ends on **one** account contact (tested in memory
and on PostgreSQL with independent data sources).

`UnlinkAccountAsync(actor, contact, expectedRevision, account, justification?)` removes the verified identity (audited `Unlinked`).
A merge is undone with the existing `IContactService.UnmergeAsync(actor, mergeId)`; the merge follower moves the conversations back
(tested through the live-chat flow).

### ResolveCustomerAsync (login helper)

Hosts call `ResolveCustomerAsync(actor, new CustomerResolveRequest(account, source, claims, displayName, linkContact | linkEvidence))`
on sign-in (portal, widget identify):

1. With `linkContact`/`linkEvidence`, the session's earlier contact is linked/merged as above.
2. Otherwise the account contact is found, or created with the account, the display name and the claims (concurrent first logins
   create exactly one contact).
3. Claims (`CustomerClaim(address, verified)`, ≤ 8: email, phone, WhatsApp, social handle) are attached: verified claims as verified
   unless another contact already holds them verified (then claimed), claimed claims as claimed; a later verified claim upgrades a
   claimed identity. Audited `Linked` with the count.
4. The workspace linking policy runs (below). The result is `CustomerResolved(contact, created, merged, suggestions)`.

### Linking policy

`ContactLinkingPolicy = LinkingOff | LinkingSuggest | LinkingAutoMerge(ContactAutoMergeRules)` per workspace (default `LinkingOff`;
`GetPolicyAsync` returns revision 0 when none is stored). It applies to *other* contacts holding a claim's address (FindByIdentity,
same scope only):

- `LinkingOff`: nothing.
- Only **verified** claims are considered by default. `LinkingSuggest(IncludeClaimedEvidence: true)` /
  `LinkingAutoMerge(rules, IncludeClaimedEvidence: true)` also let claimed claims produce suggestions (never merges); those are marked
  (`EvidenceVerified = false`, view `RequiresConfirmation`) and `AcceptSuggestionAsync(..., confirmClaimedEvidence: true, ...)` is
  required to accept them (otherwise `Invalid`).
- `LinkingSuggest`: a pending `ContactLinkSuggestion(candidate → account, evidence)` is stored (one pending per pair; repeat logins
  reuse it), audited `LinkSuggested`.
- `LinkingAutoMerge(rules)`: a candidate is merged into the account contact when at least `MinimumMatchingIdentities` **verified**
  claims of `Kinds` match addresses the candidate holds as **claimed**, and (`RequireUnverifiedCandidate`, default) the candidate holds
  no verified identity; otherwise, or when the actor lacks `contact:merge`, a suggestion is stored. Claimed-only evidence and
  verified holders never auto-merge. Candidate `contact:link` checks share one `IAccessDecisionContext` per call. Auto-merge is opt-in: a visitor who typed someone else's address would have their own
  conversations attached to that account, so enable it only where provider verification is trustworthy.

Policies persist as `{"v":1,"k":"off"|"suggest"|"auto-merge",...}` with an optional `"claimed":true` (`ContactLinkingCodec`); unions are
never serialized.

### Suggestions

`ListSuggestionsAsync(actor, query(status, contact?, limit ≤ 100, after))` requires `contact:link`; rows are filtered by the planned
`contact:read` Access filter on **both** contacts inside the store query (bounded scan of 1000 rows per page, keyset by id). Views carry
identifiers and the evidence kind only, never the address. `AcceptSuggestionAsync` (`contact:read` + `contact:link` on both,
`contact:merge` through the merge) merges and records `Accepted` with the merge id (audited `LinkSuggestionAccepted`);
`RejectSuggestionAsync` records `Rejected` (audited, with justification). Suggestions whose contacts were merged meanwhile become
`Superseded` on accept. A contact-filtered listing runs two index-backed keyset scans (candidate and account keys) merged by id.

**Retention**: `IContactLinkingStore.PruneDecidedSuggestionsAsync(decidedBefore, limit ≤ 10,000)` deletes decided (accepted, rejected,
superseded) suggestions last updated before the cutoff, oldest first, store-wide (PostgreSQL: partial index on `updated_at`,
`FOR UPDATE SKIP LOCKED`); pending suggestions are never pruned. Hosts call it from a maintenance loop.

### Change log and events

Account links append `ContactChangeKind.IdentityLinked` (next to `Updated`, or `Created` when `ResolveCustomerAsync` creates the
contact); `UnlinkAccountAsync` appends `IdentityUnlinked` next to `Updated`. Merges keep `Merged`/`Updated`. The events contact
follower maps `IdentityLinked` to `ticketflow.contact.identity-linked.v1` (`ContactIdentityLinkedData(contact, "external-user", true)`);
`IdentityUnlinked` has no event type yet and is skipped.

### Permissions

| Permission | Sensitive | Use |
| --- | --- | --- |
| `contact:link` | yes | Link/unlink accounts, resolve customers, list/accept/reject suggestions. |
| `contact-linking:manage` | yes | Change the workspace linking policy (audited `LinkingPolicyChanged`). |
| `contact:merge` (built-in) | yes | Required whenever a link merges contacts. |
| `contact:create` | no | `ResolveCustomerAsync` creating the account contact. |

Only Owner holds the feature permissions by default; grant the login API client a custom role with `contact:read`, `contact:create`,
`contact:link` and `contact:merge`. Linking refuses actors whose `contact:read` decision masks the account (`contact.external-id`) or
evidence field, so linking cannot probe masked values.

### Live chat

After `ILiveChatVisitorService.IdentifyAsync` verifies a session, `LiveChatVisitorService` calls `LinkAccountAsync` as the channel
worker with the account `(widget identity namespace or token namespace, subject)`, source `livechat-hmac`/`livechat-token`, and the
session's `WidgetVisitor` evidence. The anonymous contact is merged into an existing account contact (its conversations follow) or
becomes the account contact; later messages carry the verified identity and thread into the same conversation. Linking is best
effort: failures are logged without identifiers and never fail identification. Grant the channel worker `contact:link` and
`contact:merge`; disable with `LiveChatOptions.LinkVisitorContactOnIdentify = false`.

## Staff identities

`StaffIdentityRef(provider, subject)` maps to a `UserId`, many-to-one (≤ 32 identities per user), unique per (scope, provider,
subject). Mappings are **workspace-scoped**, like the `UserPrincipal`s they produce: grants apply per workspace (tenant grants
everywhere), and scoping the mapping lets the no-escalation check see every grant the mapping confers.

- **Link/unlink** require `staff-identity:manage` (sensitive) and `IAccessAdministrator.CheckCanConferAsync` over every grant the
  target user holds in the workspace — its own, its teams' (from `ITeamMembershipResolver`) and tenant grants (which need
  tenant-scoped authority). Mapping a subject to a user makes whoever controls that subject act as the user, so an identity manager
  can only map users whose authority it could have granted itself. Relinking to the same user is idempotent; linking an identity
  held by another user is `Taken`. Every change is audited (`StaffIdentityAuditEvent`: provider and user, never the subject) in the
  store transaction.
- **List**: own identities with `workspace:read`, anyone's with `staff-identity:manage`.
- **Resolver**: `IStaffIdentityResolver.ResolveUserAsync(scope, identity)` is a trusted, unaudited lookup for principal resolvers.
- **Profiles**: `StaffProfile(user, displayName, avatarRef, notificationEmail)` keyed by user (routing capacities and skills stay in
  `TicketFlow.Routing`). Reads need `team:read`; `staff.email` is masked by Access field policies applying to `team:read`, except for
  the user itself. Users edit their own profile (`workspace:read`); others need `staff-identity:manage`. Compare-and-set, audited.

### IdentityFlow integration point

`TicketFlow.Integrations.IdentityFlow` (owned by E11) currently maps the IdentityFlow subject to `UserId` by prefix in
`IdentityFlowPrincipalResolver`. To support multiple providers per agent it should, for user identities, call
`IStaffIdentityResolver.ResolveUserAsync(scope, new StaffIdentityRef(provider, subject))` first — `provider` from options (default
`identityflow`) or the upstream provider claim — and fall back to the prefix mapping when unmapped (configurable, for example
`RequireStaffIdentityMapping`), then build the principal with `ITeamMembershipResolver.ResolveUserPrincipalAsync(scope, userId)`.
For customer sign-ins (portal, widget token), hosts call `IContactLinkingService.ResolveCustomerAsync` with the IdentityFlow
subject as `ExternalAccountRef("identityflow", subject)` and the `email`/`email_verified`/`phone_number_verified` claims.

## Store contracts

| Contract | Guarantees |
| --- | --- |
| `IContactLinkingStore` | Retention of decided suggestions; scope-confined; policy create-at-1 then compare-and-set; one pending suggestion per (candidate, account), atomically (duplicates return the existing one); ≤ 100,000 pending per scope; CAS suggestion updates with immutable pair/evidence; filtered bounded listing over both contacts. |
| `IStaffIdentityStore` | Results `StaffIdentityWriteResult` (`StaffIdentityWriteApplied | StaffIdentityWriteTaken | StaffIdentityWriteNotFound | StaffIdentityWriteRejected`) and `StaffIdentityProfileWriteResult` (`StaffIdentityProfileWriteApplied | StaffIdentityWriteNotFound | StaffIdentityWriteConflict`). Scope-confined; atomic (provider, subject) uniqueness and per-user capacity; idempotent relink; unlink fenced on the expected user; CAS profiles; audit in the same transaction. |

`tests/Contacts/TicketFlow.Contacts.StoreContracts` (`ContactLinkingStoreContract`) and `tests/Identity/TicketFlow.Identity.StoreContracts`
verify both against the in-memory stores and PostgreSQL with independent clients.
