# T07 — IMAP mailbox channel (follow-up)

Epic: [E6 — Channels and EnvelopeFlow integration](../PLAN.md)

Status: **Planned** (follow-up; not part of the E6 completion gate)

## Goal

`TicketFlow.Channels.Mailbox`: IMAP polling via MailKit (central version 5.0.0, as in EnvelopeFlow) producing
`InboundChannelMessage`s with full MIME parsing (Message-Id, In-Reply-To, References, text/HTML parts, attachment metadata),
dedupe by Message-Id, UID/UIDVALIDITY tracking in a durable mailbox state store (in-memory + PostgreSQL), bounded fetch sizes.

## Dependencies

E6 T01–T03.

## Acceptance criteria

- UID checkpoints survive restarts and UIDVALIDITY changes trigger a safe rescan deduplicated by Message-Id.
- Attachments are stored through an application-owned storage seam; TicketFlow keeps references only.

## Verification

- Tests against a fake IMAP client plus an optional GreenMail/Dovecot container.

## Risks and boundaries

Deferred from E6 for time. The inbound pipeline already accepts `References` chains, so the mailbox channel only needs MIME
normalization and checkpointing.

## Results

Not started.
