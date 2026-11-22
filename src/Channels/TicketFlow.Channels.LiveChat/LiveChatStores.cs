using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Realtime;

namespace TicketFlow.Channels.LiveChat;

public sealed record LiveChatWriteApplied<T>(T Value);

public sealed record LiveChatWriteNotFound
{
    private LiveChatWriteNotFound()
    {
    }

    public static LiveChatWriteNotFound Instance { get; } = new();
}

/// <summary>The revision did not match (or a create found an existing record: <see cref="CurrentRevision"/> is its revision).</summary>
public sealed record LiveChatWriteConflict(long? CurrentRevision);

public enum LiveChatWriteRejection
{
    /// <summary>A store capacity limit (widgets per scope, links per session) would be exceeded.</summary>
    CapacityExceeded = 1,
}

public sealed record LiveChatWriteRejected(LiveChatWriteRejection Reason);

/// <summary>Outcome of a live-chat store write. Switch over every case without a discard arm.</summary>
public union LiveChatWriteResult<T>(LiveChatWriteApplied<T>, LiveChatWriteNotFound, LiveChatWriteConflict, LiveChatWriteRejected);

/// <summary>
/// Widget configurations keyed by (scope, configuration id): scope-confined; create at revision 1 (conflict when present);
/// compare-and-set update and delete; at most <see cref="MaximumWidgetsPerScope"/> per scope, checked atomically.
/// </summary>
public interface ILiveChatWidgetStore
{
    public const int MaximumWidgetsPerScope = 1000;

    Task<StoredLiveChatWidget?> GetAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default);

    Task<LiveChatWriteResult<StoredLiveChatWidget>> CreateAsync(TicketScope scope, LiveChatWidget widget, CancellationToken cancellationToken = default);

    Task<LiveChatWriteResult<StoredLiveChatWidget>> UpdateAsync(TicketScope scope, LiveChatWidget widget, long expectedRevision, CancellationToken cancellationToken = default);

    Task<LiveChatWriteResult<StoredLiveChatWidget>> DeleteAsync(TicketScope scope, string configurationId, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Visitor sessions, their conversation links and read cursors. Contract:
/// <list type="bullet">
/// <item>Session ids are unique per partition (tenant, project, environment, app); <see cref="GetAsync"/> is the only partition-wide
/// read and returns the session's full scope. Every other operation is confined to one scope.</item>
/// <item>Only token hashes are stored. Creates start at revision 1 (conflict when the id exists in the partition); updates are
/// compare-and-set and keep id, configuration, inbox, visitor id and creation time immutable.</item>
/// <item>Links are idempotent per (session, conversation), at most <see cref="MaximumLinksPerSession"/> per session (atomic); read
/// cursors only move forward.</item>
/// <item><see cref="PruneExpiredAsync"/> deletes sessions (and their links) that expired before the cutoff, store-wide.</item>
/// </list>
/// </summary>
public interface ILiveChatSessionStore
{
    public const int MaximumLinksPerSession = 256;
    public const int MaximumPrune = 10_000;

    Task<LiveChatWriteResult<StoredVisitorSession>> CreateAsync(TicketScope scope, VisitorSession session, CancellationToken cancellationToken = default);

    Task<StoredVisitorSession?> GetAsync(AccessPartition partition, VisitorSessionId id, CancellationToken cancellationToken = default);

    Task<LiveChatWriteResult<StoredVisitorSession>> UpdateAsync(TicketScope scope, VisitorSession session, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Links (idempotently) a conversation to an existing session. NotFound when the session does not exist in the scope.</summary>
    Task<LiveChatWriteResult<VisitorConversationLink>> LinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, DateTimeOffset linkedAt, CancellationToken cancellationToken = default);

    Task<VisitorConversationLink?> GetLinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, CancellationToken cancellationToken = default);

    /// <summary>The session's links, most recently linked first.</summary>
    Task<IReadOnlyList<VisitorConversationLink>> ListLinksAsync(TicketScope scope, VisitorSessionId session, CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="limit"/> (1-32) sessions linked to a conversation, most recently linked first.</summary>
    Task<IReadOnlyList<VisitorSessionId>> FindSessionsAsync(TicketScope scope, ConversationId conversation, int limit, CancellationToken cancellationToken = default);

    /// <summary>Moves the read cursor forward (never back); returns the stored link, or NotFound when not linked.</summary>
    Task<LiveChatWriteResult<VisitorConversationLink>> AdvanceCursorAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, long readSequence, CancellationToken cancellationToken = default);

    Task<int> PruneExpiredAsync(DateTimeOffset expiredBefore, int limit, CancellationToken cancellationToken = default);
}

/// <summary>A visitor upload passed to <see cref="IAttachmentStore"/>. <see cref="Content"/> enforces <see cref="MaximumBytes"/> while read.</summary>
public sealed record AttachmentUpload(TicketScope Scope, InboxId Inbox, VisitorSessionId Session, string FileName, string MediaType, long MaximumBytes, Stream Content)
{
    public override string ToString() => $"AttachmentUpload {{ MediaType = {MediaType} }}";
}

/// <summary>
/// Application-provided blob storage for visitor attachments. TicketFlow keeps only <c>AttachmentRef</c>s. Contract:
/// <see cref="StoreAsync"/> persists the content (reading at most the declared limit; the stream throws
/// <see cref="AttachmentTooLargeException"/> beyond it) and returns an opaque storage reference (≤ 1024 characters) and the stored size;
/// <see cref="FindAsync"/> returns the attachment only when it was stored for that exact scope and session, so a visitor can attach
/// only its own uploads.
/// </summary>
public interface IAttachmentStore
{
    Task<StoredAttachment> StoreAsync(AttachmentUpload upload, CancellationToken cancellationToken = default);

    Task<TicketFlow.Conversations.AttachmentRef?> FindAsync(TicketScope scope, VisitorSessionId session, string storageRef, CancellationToken cancellationToken = default);
}

public sealed record StoredAttachment(string StorageRef, long SizeBytes);

/// <summary>Thrown by the upload stream when more than the allowed bytes are read.</summary>
public sealed class AttachmentTooLargeException() : IOException("The attachment exceeds the allowed size.");
