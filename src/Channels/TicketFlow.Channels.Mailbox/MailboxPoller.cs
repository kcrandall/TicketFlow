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

        var fallbackMessageId = $"uid:{uidValidity}:{uid}";
        var messageId = fallbackMessageId;
        try
        {
            using var stream = new MemoryStream(rawMimeBytes, writable: false);
            var mime = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);

            messageId = NormalizeMessageId(mime.MessageId) ?? fallbackMessageId;

            // Check seen Message-Id BEFORE uploading any attachments to blob storage so rescans/duplicates never leak blobs.
            if (isSeen is not null && await isSeen(messageId, cancellationToken).ConfigureAwait(false))
                return (messageId, null, null, AlreadySeen: true);

            // Skip bounces and automated replies BEFORE uploading attachments so mail loops never create conversations or orphan blobs.
            if (config.SuppressAutoReplies && TryDetectHeaderAutoReplyOrBounce(mime) is { } autoSkipReason)
                return (messageId, null, autoSkipReason, AlreadySeen: false);

            // Extract and validate sender & message metadata BEFORE uploading attachments so invalid/poison messages never orphan blobs.
            // Resolve sender identity exclusively from From (never Reply-To) so callers cannot spoof another contact's identity.
            var senderBox = mime.From.Mailboxes.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Address));
            if (senderBox is null)
                return (messageId, null, "missing-sender", AlreadySeen: false);

            if (config.SuppressAutoReplies && IsBounceSender(senderBox.Address))
                return (messageId, null, "bounce", AlreadySeen: false);

            var sender = new ChannelSenderEvidence(ContactIdentityKind.Email, senderBox.Address.Trim(), verified: false);

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

            var rawText = mime.TextBody is { } text ? ChannelText.SanitizeBody(text, ConversationMessageLimits.MaximumBodyBytes) : null;
            var bodyText = string.IsNullOrWhiteSpace(rawText) ? null : rawText;
            var rawHtml = mime.HtmlBody is { } html ? ChannelText.SanitizeBody(html, ConversationMessageLimits.MaximumBodyBytes * 2) : null;
            var bodyHtml = string.IsNullOrWhiteSpace(rawHtml) ? null : rawHtml;
            var subject = SanitizeHeaderText(mime.Subject, InboundChannelMessage.MaximumSubjectLength);
            var displayName = SanitizeHeaderText(senderBox.Name, InboundChannelMessage.MaximumDisplayNameLength);
            var receivedAt = mime.Date == default ? fallbackReceivedAt.ToUniversalTime() : mime.Date.ToUniversalTime();

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
                            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
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

            if (bodyText is null && bodyHtml is null && attachments.Count == 0)
                return (messageId, null, "empty-message", AlreadySeen: false);

            var inbound = new InboundChannelMessage(
                scope: config.Scope,
                channel: config.Channel,
                configurationId: config.ConfigurationId,
                externalMessageId: messageId,
                sender: sender,
                receivedAt: receivedAt,
                bodyText: bodyText,
                bodyHtml: bodyHtml,
                attachments: attachments,
                externalThreadId: null,
                inReplyTo: inReplyTo,
                references: references,
                subject: subject,
                senderDisplayName: displayName);
            return (messageId, inbound, null, AlreadySeen: false);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return (messageId, null, "invalid-message", AlreadySeen: false);
        }
    }

    /// <summary>Normalizes a RFC 5322 Message-Id (strips surrounding angle brackets, trims whitespace and enforces channel text rules).</summary>
    public static string? NormalizeMessageId(string? raw) => ChannelText.NormalizeReference(raw);

    private static string? TryDetectHeaderAutoReplyOrBounce(MimeMessage mime)
    {
        if (IsBounceContentType(mime.Body))
            return "bounce";

        foreach (var header in mime.Headers)
        {
            if (header.Id == HeaderId.ReturnPath || string.Equals(header.Field, "Return-Path", StringComparison.OrdinalIgnoreCase))
            {
                var value = header.Value.AsSpan().Trim();
                if (value.IsEmpty || (value.Length >= 2 && value[0] == '<' && value[^1] == '>' && value[1..^1].Trim().IsEmpty))
                    return "bounce";
            }
            else if (string.Equals(header.Field, "Auto-Submitted", StringComparison.OrdinalIgnoreCase))
            {
                var value = header.Value.AsSpan();
                var semicolon = value.IndexOf(';');
                if (semicolon >= 0)
                    value = value[..semicolon];
                value = value.Trim();
                if (!value.IsEmpty && !value.Equals("no", StringComparison.OrdinalIgnoreCase))
                    return "auto-submitted";
            }
            else if (string.Equals(header.Field, "Precedence", StringComparison.OrdinalIgnoreCase))
            {
                var value = header.Value.AsSpan().Trim();
                if (value.Equals("bulk", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("junk", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("list", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("auto_reply", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("auto-reply", StringComparison.OrdinalIgnoreCase))
                    return "auto-submitted";
            }
            else if (string.Equals(header.Field, "X-Auto-Response-Suppress", StringComparison.OrdinalIgnoreCase))
            {
                var remaining = header.Value.AsSpan();
                while (!remaining.IsEmpty)
                {
                    var comma = remaining.IndexOf(',');
                    var token = (comma >= 0 ? remaining[..comma] : remaining).Trim();
                    if (token.Equals("All", StringComparison.OrdinalIgnoreCase)
                        || token.Equals("OOF", StringComparison.OrdinalIgnoreCase)
                        || token.Equals("AutoReply", StringComparison.OrdinalIgnoreCase))
                        return "auto-submitted";
                    if (comma < 0)
                        break;
                    remaining = remaining[(comma + 1)..];
                }
            }
        }

        return null;
    }

    private static bool IsBounceContentType(MimeEntity? body)
    {
        if (body is null)
            return false;
        if (body.ContentType is { } contentType
            && (contentType.IsMimeType("multipart", "report") || contentType.IsMimeType("message", "delivery-status")))
            return true;
        if (body is Multipart multipart)
        {
            for (var i = 0; i < multipart.Count; i++)
                if (multipart[i].ContentType is { } partType && partType.IsMimeType("message", "delivery-status"))
                    return true;
        }
        return false;
    }

    private static bool IsBounceSender(string address)
    {
        var span = address.AsSpan().Trim();
        var at = span.IndexOf('@');
        var local = at >= 0 ? span[..at] : span;
        return local.Equals("mailer-daemon", StringComparison.OrdinalIgnoreCase)
            || local.Equals("postmaster", StringComparison.OrdinalIgnoreCase);
    }

    private static string? SanitizeHeaderText(string? raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw) || maxChars <= 0)
            return null;
        var span = raw.AsSpan();
        var builder = new StringBuilder(Math.Min(span.Length, maxChars));
        Span<char> encoded = stackalloc char[2];
        var pendingSpace = false;
        var index = 0;
        while (index < span.Length)
        {
            var status = Rune.DecodeFromUtf16(span[index..], out var rune, out var consumed);
            index += Math.Max(consumed, 1);
            if (status != OperationStatus.Done)
                continue;
            if (Rune.IsWhiteSpace(rune))
            {
                if (builder.Length > 0)
                    pendingSpace = true;
                continue;
            }
            if (rune.Value is < ' ' or (>= 0x7F and <= 0x9F))
                continue;
            var needed = rune.Utf16SequenceLength + (pendingSpace ? 1 : 0);
            if (builder.Length + needed > maxChars)
                break;
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            var written = rune.EncodeToUtf16(encoded);
            builder.Append(encoded[..written]);
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var lastSlash = name.LastIndexOfAny(['/', '\\']);
        var segment = lastSlash >= 0 ? name[(lastSlash + 1)..] : name;
        var sanitized = SanitizeHeaderText(segment, AttachmentRef.MaximumFileNameLength);
        return sanitized is null ? "attachment.bin" : sanitized is "." or ".." ? "attachment" : sanitized;
    }

    private static string SanitizeMediaType(string mediaType)
    {
        var trimmed = mediaType.AsSpan().Trim();
        if (trimmed.Length == 0 || !trimmed.Contains('/') || trimmed.Length > AttachmentRef.MaximumMediaTypeLength || !ChannelText.IsClean(trimmed))
            return "application/octet-stream";
        for (var i = 0; i < trimmed.Length; i++)
            if (char.IsWhiteSpace(trimmed[i]))
                return "application/octet-stream";
        return trimmed.Length == mediaType.Length ? mediaType : trimmed.ToString();
    }
}

/// <summary>
/// Polls an IMAP folder, maintains durable UID/UIDVALIDITY checkpoints and Message-Id deduplication, and delivers normalized
/// messages through <see cref="IInboundChannelPipeline"/>. UIDVALIDITY changes trigger a safe rescan from UID 1 deduplicated
/// by Message-Id. Checkpoints advance strictly after each message is processed so crashes never skip messages.
/// </summary>
public sealed partial class MailboxPoller(
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
        if (ingestor is null)
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
            if (logger is not null)
                LogUidValidityChanged(logger, config.ConfigurationId, config.Folder);
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
                if (logger is not null)
                    LogSkippedMessage(logger, uid, config.ConfigurationId, config.Folder, skipReason);
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
                        if (logger is not null)
                            LogStoppedOnRejection(logger, config.ConfigurationId, config.Folder, rejected.Reason);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "IMAP UIDVALIDITY changed for mailbox {Config}/{Folder}; rescanning safely with Message-Id deduplication.")]
    private static partial void LogUidValidityChanged(ILogger logger, string config, string folder);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped IMAP message UID {Uid} in {Config}/{Folder}: {Reason}.")]
    private static partial void LogSkippedMessage(ILogger logger, uint uid, string config, string folder, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopped IMAP poll for {Config}/{Folder} on pipeline rejection {Reason}; will retry next poll.")]
    private static partial void LogStoppedOnRejection(ILogger logger, string config, string folder, InboundRejection reason);
}
