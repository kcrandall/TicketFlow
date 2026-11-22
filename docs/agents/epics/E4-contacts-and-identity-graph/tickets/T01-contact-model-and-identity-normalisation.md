# T01 — Contact model, identity normalisation and permissions

Epic: [E4 — Contacts and identity graph](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Contacts.Abstractions`: `Contact` (profile, typed custom attributes, labels, identities, blocked flag, timestamps), typed
attribute definitions with a `Sensitive` flag, `ContactIdentityAddress` with per-kind normalisation (email, E.164 phone, WhatsApp,
widget visitor, external user, social handle), the `IdentityVerification` union (`IdentityVerified` | `IdentityClaimed`), contact
field paths for Access masking and the contact permission set (`contact:create`, `contact:delete`, `contact-attribute:manage` added
to the existing `contact:*` built-ins).

## Dependencies

E2 (Access abstractions).

## Acceptance criteria

- Normalisation is bounded and dependency-free: email (IDN domain via `IdnMapping`, lowercase), E.164 (`+` or `00` prefix or an
  explicit default calling code, 7-15 digits), opaque identifiers validated with `TicketScope.RequireIdentifier` rules, social handles.
- Invalid input returns `ContactAddressInvalid` through the `ContactAddressNormalization` union; addresses redact `ToString`.
- Attribute values are a union (text/number/boolean/date/list) with a versioned JSON codec using explicit discriminators.

## Verification

- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` (normalisation, codec and model tests).

## Risks and boundaries

No libphonenumber: phone numbers are syntactically validated E.164, not carrier-validated. Addresses are correlation evidence,
never authentication.

## Results

Implemented in `TicketFlow.Contacts.Abstractions`:

- `Contact`, `ContactProfile`, `StoredContact`; canonical ordering of identities, attributes and labels; bounded limits; `Tombstone`/`Restore`.
- `ContactIdentityAddress.Normalize` returning the `ContactAddressNormalization` union (email with `IdnMapping`, E.164 phone/WhatsApp, opaque widget/external identifiers, social handles); redacted `ToString` and error messages.
- `IdentityVerification` union (`IdentityVerified` | `IdentityClaimed`), `ContactIdentity`, `ContactIdentityEvidence`.
- `ContactAttributeDefinition` (typed, `Sensitive`), `ContactAttributeValue` union, `ContactJsonCodec` (versioned, explicit discriminators, strict decoding).
- `ContactPermissions` (registers `contact:create`, `contact:delete` (sensitive), `contact-attribute:manage` (sensitive); reuses the Access `contact:*` built-ins), `ContactFieldPaths`, `ContactAccess`.
- Tests: `NormalizationTests`, `ModelAndCodecTests`.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E3 projects).
- `dotnet test tests/Contacts/TicketFlow.Contacts.Tests -c Release` — 72 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55474;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Contacts/TicketFlow.Contacts.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 12 passed, 0 failed.
- `bash scripts/verify-package-metadata.sh` — Validated 13 package projects and central versions.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
