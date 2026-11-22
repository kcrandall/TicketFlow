# TicketFlow.Channels.Mailbox

IMAP mailbox polling channel for TicketFlow using MailKit.

```csharp
services.AddTicketFlow()
    .AddChannels()
    .AddMailboxChannel();
```

- Polls IMAP folders through `IMailboxClientFactory` (default `MailKitMailboxClientFactory` with TLS required by default) in bounded batches.
- Parses MIME messages (`MimeKit`) into `InboundChannelMessage`s with `Message-Id`, `In-Reply-To`, `References`, `Subject`, plain-text and HTML bodies, and sender email evidence (always `Verified = false` — email headers are correlation evidence, never authentication).
- Stores attachment bytes only through the application-owned `IMailboxAttachmentStore` seam; TicketFlow keeps `AttachmentRef` metadata only.
- Tracks `(UidValidity, LastUid)` and seen `Message-Id`s in `IMailboxCheckpointStore` (use `TicketFlow.Channels.Mailbox.Stores.Postgres` in production). When `UIDVALIDITY` changes, the poller safely rescans from UID 1 and deduplicates already-ingested messages by `Message-Id`.
