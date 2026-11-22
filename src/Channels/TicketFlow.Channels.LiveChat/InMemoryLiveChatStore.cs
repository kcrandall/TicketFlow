using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Realtime;

namespace TicketFlow.Channels.LiveChat;

/// <summary>
/// Development-only <see cref="ILiveChatWidgetStore"/> and <see cref="ILiveChatSessionStore"/>: process-local, bounded (100 000
/// sessions), not durable. Operations are serialized by one lock.
/// </summary>
public sealed class InMemoryLiveChatStore : ILiveChatWidgetStore, ILiveChatSessionStore
{
    public const int MaximumSessions = 100_000;

    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, string Configuration), StoredLiveChatWidget> widgets = [];
    private readonly Dictionary<(AccessPartition Partition, VisitorSessionId Id), StoredVisitorSession> sessions = [];
    private readonly Dictionary<(TicketScope Scope, VisitorSessionId Session), List<VisitorConversationLink>> links = [];

    public Task<StoredLiveChatWidget?> GetAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        lock (gate)
            return Task.FromResult(widgets.GetValueOrDefault((scope, configurationId)));
    }

    public Task<LiveChatWriteResult<StoredLiveChatWidget>> CreateAsync(TicketScope scope, LiveChatWidget widget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(widget);
        lock (gate)
        {
            if (widgets.TryGetValue((scope, widget.ConfigurationId), out var existing))
                return Result<StoredLiveChatWidget>(new LiveChatWriteConflict(existing.Revision));
            if (widgets.Keys.Count(key => key.Scope == scope) >= ILiveChatWidgetStore.MaximumWidgetsPerScope)
                return Result<StoredLiveChatWidget>(new LiveChatWriteRejected(LiveChatWriteRejection.CapacityExceeded));
            var stored = new StoredLiveChatWidget(scope, widget, 1);
            widgets[(scope, widget.ConfigurationId)] = stored;
            return Result<StoredLiveChatWidget>(new LiveChatWriteApplied<StoredLiveChatWidget>(stored));
        }
    }

    public Task<LiveChatWriteResult<StoredLiveChatWidget>> UpdateAsync(TicketScope scope, LiveChatWidget widget, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(widget);
        lock (gate)
        {
            if (!widgets.TryGetValue((scope, widget.ConfigurationId), out var existing))
                return Result<StoredLiveChatWidget>(LiveChatWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredLiveChatWidget>(new LiveChatWriteConflict(existing.Revision));
            var stored = new StoredLiveChatWidget(scope, widget, existing.Revision + 1);
            widgets[(scope, widget.ConfigurationId)] = stored;
            return Result<StoredLiveChatWidget>(new LiveChatWriteApplied<StoredLiveChatWidget>(stored));
        }
    }

    public Task<LiveChatWriteResult<StoredLiveChatWidget>> DeleteAsync(TicketScope scope, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        lock (gate)
        {
            if (!widgets.TryGetValue((scope, configurationId), out var existing))
                return Result<StoredLiveChatWidget>(LiveChatWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredLiveChatWidget>(new LiveChatWriteConflict(existing.Revision));
            widgets.Remove((scope, configurationId));
            return Result<StoredLiveChatWidget>(new LiveChatWriteApplied<StoredLiveChatWidget>(existing));
        }
    }

    public Task<LiveChatWriteResult<StoredVisitorSession>> CreateAsync(TicketScope scope, VisitorSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        lock (gate)
        {
            var key = (AccessPartition.From(scope), session.Id);
            if (sessions.TryGetValue(key, out var existing))
                return Result<StoredVisitorSession>(new LiveChatWriteConflict(existing.Revision));
            if (sessions.Count >= MaximumSessions)
                return Result<StoredVisitorSession>(new LiveChatWriteRejected(LiveChatWriteRejection.CapacityExceeded));
            var stored = new StoredVisitorSession(scope, session, 1);
            sessions[key] = stored;
            return Result<StoredVisitorSession>(new LiveChatWriteApplied<StoredVisitorSession>(stored));
        }
    }

    public Task<StoredVisitorSession?> GetAsync(AccessPartition partition, VisitorSessionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        lock (gate)
            return Task.FromResult(sessions.GetValueOrDefault((partition, id)));
    }

    public Task<LiveChatWriteResult<StoredVisitorSession>> UpdateAsync(TicketScope scope, VisitorSession session, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        lock (gate)
        {
            var key = (AccessPartition.From(scope), session.Id);
            if (!sessions.TryGetValue(key, out var existing) || existing.Scope != scope)
                return Result<StoredVisitorSession>(LiveChatWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredVisitorSession>(new LiveChatWriteConflict(existing.Revision));
            var current = existing.Session;
            if (current.ConfigurationId != session.ConfigurationId || current.Inbox != session.Inbox || current.VisitorId != session.VisitorId || current.CreatedAt != session.CreatedAt)
                throw new ArgumentException("Session identity fields are immutable.", nameof(session));
            var stored = new StoredVisitorSession(scope, session, existing.Revision + 1);
            sessions[key] = stored;
            return Result<StoredVisitorSession>(new LiveChatWriteApplied<StoredVisitorSession>(stored));
        }
    }

    public Task<LiveChatWriteResult<VisitorConversationLink>> LinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, DateTimeOffset linkedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        lock (gate)
        {
            if (!sessions.TryGetValue((AccessPartition.From(scope), session), out var stored) || stored.Scope != scope)
                return Result<VisitorConversationLink>(LiveChatWriteNotFound.Instance);
            var list = links.TryGetValue((scope, session), out var existing) ? existing : links[(scope, session)] = [];
            if (list.FirstOrDefault(link => link.Conversation == conversation) is { } found)
                return Result<VisitorConversationLink>(new LiveChatWriteApplied<VisitorConversationLink>(found));
            if (list.Count >= ILiveChatSessionStore.MaximumLinksPerSession)
                return Result<VisitorConversationLink>(new LiveChatWriteRejected(LiveChatWriteRejection.CapacityExceeded));
            var link = new VisitorConversationLink(session, conversation, LiveChatText.Instant(linkedAt), 0);
            list.Add(link);
            return Result<VisitorConversationLink>(new LiveChatWriteApplied<VisitorConversationLink>(link));
        }
    }

    public Task<VisitorConversationLink?> GetLinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        lock (gate)
            return Task.FromResult(links.TryGetValue((scope, session), out var list) ? list.FirstOrDefault(link => link.Conversation == conversation) : null);
    }

    public Task<IReadOnlyList<VisitorConversationLink>> ListLinksAsync(TicketScope scope, VisitorSessionId session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<VisitorConversationLink>>(links.TryGetValue((scope, session), out var list) ? [.. Enumerable.Reverse(list)] : []);
    }

    public Task<IReadOnlyList<VisitorSessionId>> FindSessionsAsync(TicketScope scope, ConversationId conversation, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IVisitorSessionDirectory.MaximumSessions);
        lock (gate)
        {
            var found = links.Where(pair => pair.Key.Scope == scope)
                .SelectMany(pair => pair.Value.Where(link => link.Conversation == conversation))
                .OrderByDescending(link => link.LinkedAt).ThenBy(link => link.Session.Value, StringComparer.Ordinal)
                .Take(limit).Select(link => link.Session).ToList();
            return Task.FromResult<IReadOnlyList<VisitorSessionId>>(found);
        }
    }

    public Task<LiveChatWriteResult<VisitorConversationLink>> AdvanceCursorAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, long readSequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        ArgumentOutOfRangeException.ThrowIfNegative(readSequence);
        lock (gate)
        {
            var index = links.TryGetValue((scope, session), out var list) ? list.FindIndex(link => link.Conversation == conversation) : -1;
            if (list is null || index < 0)
                return Result<VisitorConversationLink>(LiveChatWriteNotFound.Instance);
            var link = list[index];
            if (readSequence > link.ReadSequence)
                list[index] = link = link with { ReadSequence = readSequence };
            return Result<VisitorConversationLink>(new LiveChatWriteApplied<VisitorConversationLink>(link));
        }
    }

    public Task<int> PruneExpiredAsync(DateTimeOffset expiredBefore, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ILiveChatSessionStore.MaximumPrune);
        lock (gate)
        {
            var expired = sessions.Where(pair => pair.Value.Session.ExpiresAt < expiredBefore).Take(limit).ToList();
            foreach (var (key, stored) in expired)
            {
                sessions.Remove(key);
                links.Remove((stored.Scope, stored.Session.Id));
            }
            return Task.FromResult(expired.Count);
        }
    }

    private static void RequireIds(VisitorSessionId session, ConversationId conversation)
    {
        if (!session.IsInitialized || conversation == default)
            throw new ArgumentException("Session and conversation identifiers are required.");
    }

    private static Task<LiveChatWriteResult<T>> Result<T>(LiveChatWriteResult<T> result) => Task.FromResult(result);
}

/// <summary>
/// Development-only <see cref="IAttachmentStore"/> keeping uploads in memory (bounded: 1000 files). Storage references are random
/// and bound to the uploading session.
/// </summary>
public sealed class InMemoryAttachmentStore : IAttachmentStore
{
    public const int MaximumFiles = 1000;

    private readonly Lock gate = new();
    private readonly Dictionary<string, (TicketScope Scope, VisitorSessionId Session, AttachmentRef Attachment, byte[] Content)> files = new(StringComparer.Ordinal);

    public async Task<StoredAttachment> StoreAsync(AttachmentUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        using var buffer = new MemoryStream();
        await upload.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var reference = "mem:" + RealtimeText.RandomToken(16);
        lock (gate)
        {
            if (files.Count >= MaximumFiles)
                throw new InvalidOperationException("The in-memory attachment store is full.");
            files[reference] = (upload.Scope, upload.Session, new AttachmentRef(reference, upload.FileName, upload.MediaType, buffer.Length), buffer.ToArray());
        }
        return new StoredAttachment(reference, buffer.Length);
    }

    public Task<AttachmentRef?> FindAsync(TicketScope scope, VisitorSessionId session, string storageRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(storageRef);
        lock (gate)
            return Task.FromResult(files.TryGetValue(storageRef, out var file) && file.Scope == scope && file.Session == session ? file.Attachment : null);
    }

    /// <summary>The stored bytes (tests and development hosts serving downloads).</summary>
    public byte[]? Content(string storageRef)
    {
        lock (gate)
            return files.TryGetValue(storageRef, out var file) ? file.Content : null;
    }
}
