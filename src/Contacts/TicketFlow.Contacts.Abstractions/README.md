# TicketFlow.Contacts.Abstractions

Contracts for TicketFlow contacts and their identity graph.

- `Contact`: profile (display name, avatar reference, locale, time zone, company), typed custom attributes, labels, identities,
  blocked flag, timestamps and an optional `MergedInto` tombstone marker; `StoredContact` adds scope and revision.
- `ContactIdentityAddress.Normalize(kind, value, namespace?, defaultCallingCode?)` returns the `ContactAddressNormalization` union
  (`ContactIdentityAddress` | `ContactAddressInvalid`): lowercase email with IDN domains, E.164 phone/WhatsApp numbers, opaque widget
  visitor and external user identifiers, social handles. No phone metadata library: numbers are validated syntactically.
- `IdentityVerification = IdentityVerified(source, at) | IdentityClaimed(source, at)`. Verified identities are unique per scope,
  kind, namespace and value; claimed identities may repeat. Addresses are correlation evidence, never authentication.
- `ContactAttributeDefinition` (text, number, boolean, date, list) with a `Sensitive` flag; values are the `ContactAttributeValue`
  union. Sensitive values are masked through `contact.attribute.<key>` field policies and fail closed when no policy exists.
- `ContactFieldPaths` (`contact.email`, `contact.phone`, `contact.social`, `contact.external-id`, attributes) and
  `ContactPermissions` (`contact:create`, `contact:delete`, `contact-attribute:manage` plus the Access `contact:*` built-ins).
- `IContactService`, `IContactAttributeDefinitionService`, union results (`ContactResult<T>`, `ContactResolution`,
  `ContactSearchResult`, `ContactRevealResult`, `ContactExportResult`), the closed `ContactEdit` hierarchy and masked `ContactView`.
- Store contracts: `IContactStore` (atomic revision-fenced `ContactWriteBatch`, verified-identity uniqueness, identity lookup,
  `AccessFilter`-filtered keyset search, merge records, per-scope change outbox), `IContactAttributeDefinitionStore`,
  `IContactAuditSink`, and `IContactChangeObserver` for in-process change notification.
- `ContactJsonCodec`: versioned persisted JSON with explicit discriminators for attributes, snapshots and merge contributions.
- Account linking: `ExternalAccountRef`, `ContactLinkRequest` and `ContactLinkResult` (`ContactLinked | ContactMergedInto |
  ContactAlreadyLinked | ContactLinkConflict | ContactRefused`), `CustomerResolveRequest`/`CustomerResolution`, the workspace
  `ContactLinkingPolicy` union (`LinkingOff | LinkingSuggest | LinkingAutoMerge(ContactAutoMergeRules)`, persisted via
  `ContactLinkingCodec`), `ContactLinkSuggestion`, `IContactLinkingService` and `IContactLinkingStore`; permissions `contact:link`
  and `contact-linking:manage`. See `docs/identity-linking.md`.
