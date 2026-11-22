# TicketFlow.Channels.Mailbox.Stores.Postgres

Durable PostgreSQL implementation of `IMailboxCheckpointStore` for `TicketFlow.Channels.Mailbox`.

- Stores IMAP `(uid_validity, last_uid)` checkpoints per `(scope, configuration_id, folder)` in `tf_mailbox_checkpoints`.
- Stores seen normalized `Message-Id`s in `tf_mailbox_seen_messages` with bounded pruning (`PruneSeenMessageIdsAsync`) so UIDVALIDITY resets rescan without duplicating conversations or messages.
- Tables are created only by calling `InitializeAsync()` (advisory-locked, checksum-verified migrations).
