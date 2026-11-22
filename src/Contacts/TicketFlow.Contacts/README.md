# TicketFlow.Contacts

The TicketFlow contact service.

- `ContactService` (`IContactService`): create, get, update (closed `ContactEdit` hierarchy applied atomically), delete, inbound
  resolve-or-create, search, reveal, export, merge and unmerge. Every read and mutation authorizes through `IAccessEvaluator`
  (mutations against both the current and the resulting contact); search and export put `IAccessQueryPlanner` filters into the
  store query. Revision fences and existence are checked before authorization decisions, like the access administrator.
- Masking: `contact.email`, `contact.phone`, `contact.social`, `contact.external-id` and `contact.attribute.<key>` are removed from
  `ContactView`s per Access field policies. Sensitive attributes with no applicable policy fail closed (masked, not revealable).
  `RevealAsync` goes through `IAccessEvaluator.RevealAsync` (always audited). Identity-prefix search on a masked kind is refused.
- Resolve-or-create: verified evidence matching a verified identity wins (`ContactMatched` / `Verified`); otherwise claimed holders
  match per `ClaimedIdentityMatching` (`WhenUnique` by default); several candidates yield `ContactAmbiguous`; otherwise a contact
  is created (`ContactCreated`), retrying when a concurrent writer takes a verified identity. Channel ingestion runs as a
  grant-holding principal (for example an API client with `contact:read` and `contact:create`).
- Merge/unmerge (`ContactMerger` documents the conflict rules) are single atomic store batches with a merge record, audit event
  (`IContactAuditSink`) and outbox change events; `IContactChangeObserver`s are notified after commit.
- `ContactLinkingService` (`IContactLinkingService`): link verified external accounts (merging anonymous/email-only contacts into
  the account contact through `MergeAsync`), unlink, the `ResolveCustomerAsync` login helper, the workspace linking policy and
  link suggestions (list/accept/reject). `InMemoryContactLinkingStore` is the development store.
- Export requires `contact:export` and a justification, is audited before streaming, and is bounded by
  `ContactsOptions.MaximumExportContacts`.

```csharp
services.AddTicketFlow()
    .AddAccessControl()
    .AddInMemoryAccessStores()      // development only
    .AddContacts(options => options.ClaimedIdentityMatching = ClaimedIdentityMatching.Never)
    .AddInMemoryContactStores();    // development only
```

Registrations use TryAdd and preserve application services; the services are scoped. The in-memory stores are bounded,
process-local development examples with no durability; use `TicketFlow.Contacts.Stores.Postgres` or an application-owned
implementation in production.
