# T07 — IMAP mailbox channel (follow-up)

Epic: [E6 — Channels and EnvelopeFlow integration](../PLAN.md)

Status: **Completed**

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

Implemented `TicketFlow.Channels.Mailbox` and `TicketFlow.Channels.Mailbox.Stores.Postgres` (2026-11-22): MailKit 5.0.0 `MailKitMailboxClientFactory` with bounded streaming fetch, `MailboxMimeNormalizer` (`Message-Id`, `In-Reply-To`, `References`, text/HTML parts, unverified sender email evidence, bounded attachment extraction through `IMailboxAttachmentStore`), `MailboxPoller` with UID/UIDVALIDITY checkpointing and safe UIDVALIDITY rescan deduplicated by `Message-Id`, plus durable `PostgresMailboxCheckpointStore`. Verified with `TicketFlow.Channels.Mailbox.Tests` (3 passed) and `TicketFlow.Channels.Mailbox.Stores.Postgres.Tests` (1 passed on PostgreSQL 18).
