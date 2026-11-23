using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using MimeKit;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Channels.Mailbox;

/// <summary>Summary of one IMAP mailbox poll cycle.</summary>
public sealed record MailboxPollResult(int Fetched, int Ingested, int Deduplicated, int Skipped, bool Rescanned, uint UidValidity, uint LastUid);

/// <summary>
/// Parses raw MIME email messages into TicketFlow <see cref="InboundChannelMessage"/>s: extracts normalized <c>Message-Id</c>,
/// <c>In-Reply-To</c>, ordered <c>References</c>, sender email (unverified correlation evidence), subject, plain-text/HTML body
/// and stores attachments through <see cref="IMailboxAttachmentStore"/>.
/// </summary>
public sealed class MailboxMimeNormalizer(IMailboxAttachmentStore attachmentStore)
{
    private readonly IMailboxAttachmentStore attachmentStore = attachmentStore ?? throw new ArgumentNullException(nameof(attachmentStore));

    public async Task<(string MessageId, InboundChannelMessage? Message, string? SkipReason)> NormalizeAsync(
        MailboxEndpointConfig config, uint uidValidity, uint uid, byte[] rawMimeBytes, DateTimeOffset fallbackReceivedAt, CancellationToken cancellationToken = default)
    {
        var (messageId, message, skipReason, _) = await NormalizeInternalAsync(config, uidValidity, uid, rawMimeBytes, fallbackReceivedAt, isSeen: null, cancellationToken).ConfigureAwait(false);
        return (messageId, message, skipReason);
    }

    internal async Task<(string MessageId, InboundChannelMessage? Message, string? SkipReason, bool AlreadySeen)> NormalizeInternalAsync(
        MailboxEndpointConfig config, uint uidValidity, uint uid, byte[] rawMimeBytes, DateTimeOffset fallbackReceivedAt,
        Func<string, CancellationToken, Task<bool>>? isSeen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rawMimeBytes);

        using var stream = new MemoryStream(rawMimeBytes, writable: false);
        var mime = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);

        var messageId = NormalizeMessageId(mime.MessageId) ?? $"uid:{uidValidity}:{uid}";

        // Check seen Message-Id BEFORE uploading any attachments to blob storage so rescans/duplicates never leak blobs.
        if (isSeen is not null && await isSeen(messageId, cancellationToken).ConfigureAwait(false))
            return (messageId, null, null, AlreadySeen: true);

        // Extract sender from Reply-To (when present and valid) else From; email headers are never verified authentication.
        var senderBox = mime.ReplyTo.Mailboxes.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Address))
            ?? mime.From.Mailboxes.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Address));
        if (senderBox is null)
            return (messageId, null, "missing-sender", AlreadySeen: false);

        var inReplyTo = NormalizeMessageId(mime.InReplyTo);
        var references = new List<string>();
        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawRef in mime.References)
        {
            var normalized = NormalizeMessageId(rawRef);
            if (normalized is not null && seenRefs.Add(normalized))
            {
                references.Add(normalized);
                if (references.Count >= InboundChannelMessage.MaximumReferences)
                    break;
            }
        }
        if (inReplyTo is not null && references.Count < InboundChannelMessage.MaximumReferences && seenRefs.Add(inReplyTo))
            references.Add(inReplyTo);

        var attachments = new List<AttachmentRef>();
        byte[]? chunk = null;
        try
        {
            foreach (var entity in mime.Attachments)
            {
                if (attachments.Count >= config.MaximumAttachments)
                    break;
                if (entity is MimePart { Content: not null } part)
                {
                    chunk ??= ArrayPool<byte>.Shared.Rent(8192);
                    await using var decoded = part.Content.Open();
                    using var buffer = new MemoryStream();
                    long total = 0;
                    var oversized = false;
                    int read;
                    while ((read = await decoded.ReadAsync(chunk.AsMemory(0, 8192), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > config.MaximumAttachmentBytes)
                        {
                            oversized = true;
                            break;
                        }
                        buffer.Write(chunk, 0, read);
                    }
                    if (oversized || total == 0)
                        continue;
                    buffer.Position = 0;
                    var fileName = SanitizeFileName(part.FileName ?? "attachment.bin");
                    var mediaType = SanitizeMediaType(part.ContentType?.MimeType ?? "application/octet-stream");
                    var stored = await attachmentStore.StoreAsync(config.Scope, config.ConfigurationId,
                        new MailboxAttachmentUpload(fileName, mediaType, total, buffer), cancellationToken).ConfigureAwait(false);
                    attachments.Add(stored);
                }
            }
        }
        finally
        {
            if (chunk is not null)
                ArrayPool<byte>.Shared.Return(chunk);
        }

        var bodyText = TruncateUtf8(mime.TextBody, ConversationMessageLimits.MaximumBodyBytes);
        var bodyHtml = TruncateUtf8(mime.HtmlBody, ConversationMessageLimits.MaximumBodyBytes * 2);
        if (string.IsNullOrWhiteSpace(bodyText) && string.IsNullOrWhiteSpace(bodyHtml) && attachments.Count == 0)
            return (messageId, null, "empty-message", AlreadySeen: false);

        var subject = string.IsNullOrWhiteSpace(mime.Subject) ? null : TruncateChars(mime.Subject.Trim(), InboundChannelMessage.MaximumSubjectLength);
        var displayName = string.IsNullOrWhiteSpace(senderBox.Name) ? null : TruncateChars(senderBox.Name.Trim(), InboundChannelMessage.MaximumDisplayNameLength);
        var receivedAt = mime.Date == default ? fallbackReceivedAt.ToUniversalTime() : mime.Date.ToUniversalTime();

        var inbound = new InboundChannelMessage(
            scope: config.Scope,
            channel: config.Channel,
            configurationId: config.ConfigurationId,
            externalMessageId: messageId,
            sender: new ChannelSenderEvidence(ContactIdentityKind.Email, senderBox.Address.Trim(), verified: false),
            receivedAt: receivedAt,
            bodyText: string.IsNullOrWhiteSpace(bodyText) ? null : bodyText,
            bodyHtml: string.IsNullOrWhiteSpace(bodyHtml) ? null : bodyHtml,
            attachments: attachments,
            externalThreadId: null,
            inReplyTo: inReplyTo,
            references: references,
            subject: subject,
            senderDisplayName: displayName);
        return (messageId, inbound, null, AlreadySeen: false);
    }

    /// <summary>Normalizes a RFC 5322 Message-Id (strips surrounding angle brackets and trims whitespace; bounded to channel limits).</summary>
    public static string? NormalizeMessageId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var span = raw.AsSpan().Trim();
        if (span.Length > 2 && span[0] == '<' && span[^1] == '>')
            span = span[1..^1].Trim();
        if (span.Length == 0)
            return null;
        if (span.Length > ChannelText.MaximumExternalIdLength)
            span = span[..ChannelText.MaximumExternalIdLength];
        return span.Length == raw.Length ? raw : span.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var trimmed = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(trimmed))
            trimmed = "attachment.bin";
        return trimmed.Length <= AttachmentRef.MaximumFileNameLength ? trimmed : trimmed[..AttachmentRef.MaximumFileNameLength];
    }

    private static string SanitizeMediaType(string mediaType)
    {
        var trimmed = mediaType.AsSpan().Trim();
        if (trimmed.Length == 0 || !trimmed.Contains('/') || trimmed.Length > AttachmentRef.MaximumMediaTypeLength)
            return "application/octet-stream";
        for (var i = 0; i < trimmed.Length; i++)
            if (char.IsWhiteSpace(trimmed[i]))
                return "application/octet-stream";
        return trimmed.Length == mediaType.Length ? mediaType : trimmed.ToString();
    }

    private static string? TruncateUtf8(string? text, int maxBytes)
    {
        if (text is null)
            return null;
        if (text.Length <= maxBytes / 3 || Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;
        var span = text.AsSpan();
        var bytes = 0;
        var index = 0;
        while (index < span.Length)
        {
            var status = Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed);
            var size = status == OperationStatus.Done ? rune.Utf8SequenceLength : 3;
            if (bytes + size > maxBytes)
                break;
            bytes += size;
            index += Math.Max(consumed, 1);
        }
        return text[..index];
    }

    private static string TruncateChars(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars];
}

/// <summary>
/// Polls an IMAP folder, maintains durable UID/UIDVALIDITY checkpoints and Message-Id deduplication, and delivers normalized
/// messages through <see cref="IInboundChannelPipeline"/>. UIDVALIDITY changes trigger a safe rescan from UID 1 deduplicated
/// by Message-Id. Checkpoints advance strictly after each message is processed so crashes never skip messages.
/// </summary>
public sealed class MailboxPoller(
    IMailboxClientFactory clientFactory,
    IMailboxCredentialProvider credentials,
    IMailboxCheckpointStore checkpoints,
    MailboxMimeNormalizer normalizer,
    IInboundChannelPipeline pipeline,
    TimeProvider time,
    ILogger<MailboxPoller>? logger = null)
{
    private readonly IMailboxClientFactory clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    private readonly IMailboxCredentialProvider credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    private readonly IMailboxCheckpointStore checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
    private readonly MailboxMimeNormalizer normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
    private readonly IInboundChannelPipeline pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    private readonly TimeProvider time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger<MailboxPoller>? logger = logger;

    public async Task<MailboxPollResult> PollAsync(PrincipalRef ingestor, MailboxEndpointConfig config, CancellationToken cancellationToken = default)
    {
        if (ingestor.Value is null)
            throw new ArgumentException("An ingestor principal is required.", nameof(ingestor));
        ArgumentNullException.ThrowIfNull(config);

        var creds = await credentials.GetCredentialsAsync(config.Scope, config.ConfigurationId, cancellationToken).ConfigureAwait(false);
        await using var client = await clientFactory.ConnectAsync(config, creds, cancellationToken).ConfigureAwait(false);
        var state = await client.OpenFolderAsync(config.Folder, cancellationToken).ConfigureAwait(false);

        var existing = await checkpoints.GetCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder, cancellationToken).ConfigureAwait(false);
        var rescanned = existing is not null && existing.UidValidity != state.UidValidity;
        uint lastUid = rescanned ? 0 : (existing?.LastUid ?? 0);
        if (rescanned)
        {
            logger?.LogInformation("IMAP UIDVALIDITY changed for mailbox {Config}/{Folder}; rescanning safely with Message-Id deduplication.", config.ConfigurationId, config.Folder);
            await checkpoints.SaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder,
                new MailboxCheckpoint(state.UidValidity, 0, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }

        var uids = await client.ListUidsAfterAsync(lastUid, config.MaximumMessagesPerPoll, cancellationToken).ConfigureAwait(false);
        int fetched = 0, ingested = 0, deduplicated = 0, skipped = 0;

        // Batch-fetch IMAP metadata summaries and seen Message-Ids when supported by the client so already-seen or oversized
        // messages are skipped without downloading MIME bodies.
        Dictionary<uint, MailboxMessageSummary>? summaryByUid = null;
        IReadOnlySet<string>? batchSeen = null;
        if (uids.Count > 0 && await client.FetchSummariesAsync(uids, cancellationToken).ConfigureAwait(false) is { } summaries)
        {
            summaryByUid = new Dictionary<uint, MailboxMessageSummary>(summaries.Count);
            var candidateIds = new List<string>(summaries.Count);
            for (var i = 0; i < summaries.Count; i++)
            {
                var summary = summaries[i];
                summaryByUid[summary.Uid] = summary;
                if (summary.SizeBytes <= config.MaximumMessageBytes)
                    candidateIds.Add(MailboxMimeNormalizer.NormalizeMessageId(summary.MessageId) ?? $"uid:{state.UidValidity}:{summary.Uid}");
            }
            if (candidateIds.Count > 0)
                batchSeen = await checkpoints.FindSeenMessageIdsAsync(config.Scope, config.ConfigurationId, config.Folder, candidateIds, cancellationToken).ConfigureAwait(false);
        }

        foreach (var uid in uids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (summaryByUid is not null)
            {
                if (!summaryByUid.TryGetValue(uid, out var summary))
                {
                    skipped++;
                    continue;
                }
                if (summary.SizeBytes > config.MaximumMessageBytes)
                {
                    fetched++;
                    skipped++;
                    lastUid = uid;
                    await checkpoints.SaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder,
                        new MailboxCheckpoint(state.UidValidity, lastUid, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var summaryMsgId = MailboxMimeNormalizer.NormalizeMessageId(summary.MessageId) ?? $"uid:{state.UidValidity}:{uid}";
                if (batchSeen is not null && batchSeen.Contains(summaryMsgId))
                {
                    fetched++;
                    deduplicated++;
                    lastUid = uid;
                    await checkpoints.SaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder,
                        new MailboxCheckpoint(state.UidValidity, lastUid, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            var raw = await client.FetchMessageAsync(uid, config.MaximumMessageBytes, cancellationToken).ConfigureAwait(false);
            if (raw is null)
            {
                skipped++;
                continue;
            }
            fetched++;
            if (raw.TooLarge || raw.RawMimeBytes is null)
            {
                skipped++;
                lastUid = uid;
                await checkpoints.SaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder,
                    new MailboxCheckpoint(state.UidValidity, lastUid, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var now = time.GetUtcNow();
            var (messageId, inbound, skipReason, alreadySeen) = await normalizer.NormalizeInternalAsync(
                config, state.UidValidity, uid, raw.RawMimeBytes, now,
                (id, ct) => batchSeen is not null && batchSeen.Contains(id)
                    ? Task.FromResult(true)
                    : checkpoints.HasSeenMessageIdAsync(config.Scope, config.ConfigurationId, config.Folder, id, ct),
                cancellationToken).ConfigureAwait(false);
            if (alreadySeen)
            {
                deduplicated++;
                lastUid = uid;
                await checkpoints.SaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder,
                    new MailboxCheckpoint(state.UidValidity, lastUid, now), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (inbound is null)
            {
                logger?.LogDebug("Skipped IMAP message UID {Uid} in {Config}/{Folder}: {Reason}.", uid, config.ConfigurationId, config.Folder, skipReason);
                skipped++;
                lastUid = uid;
                await checkpoints.RecordAndSaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder, messageId,
                    new MailboxCheckpoint(state.UidValidity, lastUid, now), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var result = await pipeline.ReceiveAsync(ingestor, inbound, cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case InboundRecorded recorded:
                    if (recorded.Replayed)
                        deduplicated++;
                    else
                        ingested++;
                    lastUid = uid;
                    await checkpoints.RecordAndSaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder, messageId,
                        new MailboxCheckpoint(state.UidValidity, lastUid, now), cancellationToken).ConfigureAwait(false);
                    break;
                case InboundRejected rejected:
                    // Permanent rejections (blocked/invalid) advance checkpoint so they don't wedge the folder; transient/rejected stop here to retry next poll.
                    if (rejected.Reason is InboundRejection.ContactBlocked or InboundRejection.InvalidSender or InboundRejection.Invalid)
                    {
                        skipped++;
                        lastUid = uid;
                        await checkpoints.RecordAndSaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder, messageId,
                            new MailboxCheckpoint(state.UidValidity, lastUid, now), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        logger?.LogWarning("Stopped IMAP poll for {Config}/{Folder} on pipeline rejection {Reason}; will retry next poll.",
                            config.ConfigurationId, config.Folder, rejected.Reason);
                        return new MailboxPollResult(fetched, ingested, deduplicated, skipped, rescanned, state.UidValidity, lastUid);
                    }
                    break;
            }
        }

        if (existing is null)
            await checkpoints.SaveCheckpointAsync(config.Scope, config.ConfigurationId, config.Folder,
                new MailboxCheckpoint(state.UidValidity, lastUid, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);

        return new MailboxPollResult(fetched, ingested, deduplicated, skipped, rescanned, state.UidValidity, lastUid);
    }
}
