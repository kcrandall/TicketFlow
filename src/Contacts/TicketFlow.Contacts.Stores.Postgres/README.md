# TicketFlow.Contacts.Stores.Postgres

Optional durable PostgreSQL storage for TicketFlow contacts.

- `PostgresContactStore` implements `IContactStore`, `IContactAttributeDefinitionStore`, `IContactAuditSink` and `IContactLinkingStore` over an
  application-owned `NpgsqlDataSource`.
- Write batches run in one transaction: revision-fenced contact rows, identity rows, merge records, outbox change events (gapless
  sequence per scope) and audit events commit together or not at all.
- Verified identities are unique per scope, kind, namespace and normalized value through a partial unique index; claimed identities
  may repeat. Concurrent claims of the same verified identity admit exactly one winner.
- Search is keyset-paged over `(scope_key, name_folded, contact_id)` and `(scope_key, kind, value, contact_id)` indexes with the
  Access filter translated by `PostgresAccessFilterTranslator` in the same statement (untranslatable filters deny all).
- `IContactLinkingStore` (migration 3, `account-linking`): `tf_contact_linking_policies` (compare-and-set policy JSON) and
  `tf_contact_link_suggestions` with a partial unique index keeping one pending suggestion per (candidate, account) pair; listing
  scans at most 1000 rows per page and evaluates the translated access filter on both joined contacts.
- Keys use fixed-size `tf_key_v1` hashes; rows carry `format_version` and numeric discriminators; attribute values and merge
  contributions use `ContactJsonCodec` versioned JSON.

```csharp
services.AddTicketFlow()
    .AddAccessControl()
    .AddPostgresAccessStores(dataSource)
    .AddContacts()
    .AddPostgresContactStores(dataSource);
await new PostgresContactStore(dataSource).InitializeAsync();   // explicit schema creation
```

`InitializeAsync` applies the append-only, checksummed `PostgresContactMigrations.Catalog` with the shared migration runner.
