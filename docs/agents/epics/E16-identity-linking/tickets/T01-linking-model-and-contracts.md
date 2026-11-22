# T01 — Account linking model, contracts, permissions and codec

Epic: [E16 — External identities and account linking](../PLAN.md)

Status: **Completed**

## Goal

Additive Contacts abstractions for account linking: `ExternalAccountRef`, `ContactLinkRequest`, the `ContactLinkResult` union
(`Linked | MergedInto | AlreadyLinked | Conflict | Refused`), `CustomerResolveRequest`/`CustomerClaim`/`CustomerResolution`, the
`ContactLinkingPolicy` union with `ContactAutoMergeRules`, suggestions (model, view, query, pages, accept result),
`IContactLinkingService`, `IContactLinkingStore` with write-result union, new permissions and audit kinds.

## Dependencies

E4.

## Acceptance criteria

- Inputs are bounded and validated (tokens, ≤ 8 claims, claim kinds, rule kinds, one subject); account ids are redacted in `ToString`.
- `contact:link` and `contact-linking:manage` are sensitive and registered by `AddContacts()`.
- Policies persist only through `ContactLinkingCodec` (`{"v":1,"k":...}`), strict decoding; unions never serialized.
- Audit kinds `Linked`, `Unlinked`, `LinkSuggested`, `LinkSuggestionAccepted`, `LinkSuggestionRejected`, `LinkingPolicyChanged`;
  `ContactAuditEvent.Suggestion` (init-only, additive).

## Verification

- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` (`RequestsValidateInputs`, `PolicyCodecRoundTripsAndRejectsUnknownShapes`, permissions test).

## Risks and boundaries

Public API is additive; `ContactPermissions.Definitions` gained two entries (existing permission test updated).

## Results

Implemented in `src/Contacts/TicketFlow.Contacts.Abstractions/ContactLinking.cs`, `ContactLinkingCodec.cs`, `ContactPermissions.cs`,
`ContactStores.cs`. Verified by the T02 command (96 passed).
