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
    private readonly Dictionary<TicketScope, Dictionary<string, StoredLiveChatWidget>> widgetsByScope = [];
    private readonly Dictionary<(AccessPartition Partition, VisitorSessionId Id), StoredVisitorSession> sessions = [];
    private readonly Dictionary<(TicketScope Scope, VisitorSessionId Session), List<VisitorConversationLink>> links = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation), List<VisitorConversationLink>> linksByConversation = [];

    public Task<StoredLiveChatWidget?> GetAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        lock (gate)
            return Task.FromResult(widgetsByScope.TryGetValue(scope, out var bucket) ? bucket.GetValueOrDefault(configurationId) : null);
    }

    public Task<StoreWriteResult<StoredLiveChatWidget, LiveChatWriteRejection>> CreateAsync(TicketScope scope, LiveChatWidget widget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(widget);
        lock (gate)
        {
            widgetsByScope.TryGetValue(scope, out var bucket);
            if (bucket is not null && bucket.TryGetValue(widget.ConfigurationId, out var existing))
                return Result<StoredLiveChatWidget>(new StoreConflict(existing.Revision));
            if ((bucket?.Count ?? 0) >= ILiveChatWidgetStore.MaximumWidgetsPerScope)
                return Result<StoredLiveChatWidget>(new StoreRejected<LiveChatWriteRejection>(LiveChatWriteRejection.CapacityExceeded));
            var stored = new StoredLiveChatWidget(scope, widget, 1);
            if (bucket is null)
                widgetsByScope[scope] = bucket = new Dictionary<string, StoredLiveChatWidget>(StringComparer.Ordinal);
            bucket[widget.ConfigurationId] = stored;
            return Result<StoredLiveChatWidget>(new StoreApplied<StoredLiveChatWidget>(stored));
        }
    }

    public Task<StoreWriteResult<StoredLiveChatWidget, LiveChatWriteRejection>> UpdateAsync(TicketScope scope, LiveChatWidget widget, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(widget);
        lock (gate)
        {
            if (!widgetsByScope.TryGetValue(scope, out var bucket) || !bucket.TryGetValue(widget.ConfigurationId, out var existing))
                return Result<StoredLiveChatWidget>(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredLiveChatWidget>(new StoreConflict(existing.Revision));
            var stored = new StoredLiveChatWidget(scope, widget, existing.Revision + 1);
            bucket[widget.ConfigurationId] = stored;
            return Result<StoredLiveChatWidget>(new StoreApplied<StoredLiveChatWidget>(stored));
        }
    }

    public Task<StoreWriteResult<StoredLiveChatWidget, LiveChatWriteRejection>> DeleteAsync(TicketScope scope, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        lock (gate)
        {
            if (!widgetsByScope.TryGetValue(scope, out var bucket) || !bucket.TryGetValue(configurationId, out var existing))
                return Result<StoredLiveChatWidget>(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredLiveChatWidget>(new StoreConflict(existing.Revision));
            bucket.Remove(configurationId);
            if (bucket.Count == 0)
                widgetsByScope.Remove(scope);
            return Result<StoredLiveChatWidget>(new StoreApplied<StoredLiveChatWidget>(existing));
        }
    }

    public Task<StoreWriteResult<StoredVisitorSession, LiveChatWriteRejection>> CreateAsync(TicketScope scope, VisitorSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        lock (gate)
        {
            var key = (AccessPartition.From(scope), session.Id);
            if (sessions.TryGetValue(key, out var existing))
                return Result<StoredVisitorSession>(new StoreConflict(existing.Revision));
            if (sessions.Count >= MaximumSessions)
                return Result<StoredVisitorSession>(new StoreRejected<LiveChatWriteRejection>(LiveChatWriteRejection.CapacityExceeded));
            var stored = new StoredVisitorSession(scope, session, 1);
            sessions[key] = stored;
            return Result<StoredVisitorSession>(new StoreApplied<StoredVisitorSession>(stored));
        }
    }

    public Task<StoredVisitorSession?> GetAsync(AccessPartition partition, VisitorSessionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        lock (gate)
            return Task.FromResult(sessions.GetValueOrDefault((partition, id)));
    }

    public Task<StoreWriteResult<StoredVisitorSession, LiveChatWriteRejection>> UpdateAsync(TicketScope scope, VisitorSession session, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(session);
        lock (gate)
        {
            var key = (AccessPartition.From(scope), session.Id);
            if (!sessions.TryGetValue(key, out var existing) || existing.Scope != scope)
                return Result<StoredVisitorSession>(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredVisitorSession>(new StoreConflict(existing.Revision));
            var current = existing.Session;
            if (current.ConfigurationId != session.ConfigurationId || current.Inbox != session.Inbox || current.VisitorId != session.VisitorId || current.CreatedAt != session.CreatedAt)
                throw new ArgumentException("Session identity fields are immutable.", nameof(session));
            var stored = new StoredVisitorSession(scope, session, existing.Revision + 1);
            sessions[key] = stored;
            return Result<StoredVisitorSession>(new StoreApplied<StoredVisitorSession>(stored));
        }
    }

    public Task<StoreWriteResult<VisitorConversationLink, LiveChatWriteRejection>> LinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, DateTimeOffset linkedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        lock (gate)
        {
            if (!sessions.TryGetValue((AccessPartition.From(scope), session), out var stored) || stored.Scope != scope)
                return Result<VisitorConversationLink>(StoreNotFound.Instance);
            if (!links.TryGetValue((scope, session), out var list))
                links[(scope, session)] = list = [];
            for (var i = 0; i < list.Count; i++)
                if (list[i].Conversation == conversation)
                    return Result<VisitorConversationLink>(new StoreApplied<VisitorConversationLink>(list[i]));
            if (list.Count >= ILiveChatSessionStore.MaximumLinksPerSession)
                return Result<VisitorConversationLink>(new StoreRejected<LiveChatWriteRejection>(LiveChatWriteRejection.CapacityExceeded));
            var link = new VisitorConversationLink(session, conversation, LiveChatText.Instant(linkedAt), 0);
            list.Add(link);
            if (!linksByConversation.TryGetValue((scope, conversation), out var byConv))
                linksByConversation[(scope, conversation)] = byConv = [];
            byConv.Add(link);
            return Result<VisitorConversationLink>(new StoreApplied<VisitorConversationLink>(link));
        }
    }

    public Task<VisitorConversationLink?> GetLinkAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        lock (gate)
        {
            if (!links.TryGetValue((scope, session), out var list))
                return Task.FromResult<VisitorConversationLink?>(null);
            for (var i = 0; i < list.Count; i++)
                if (list[i].Conversation == conversation)
                    return Task.FromResult<VisitorConversationLink?>(list[i]);
            return Task.FromResult<VisitorConversationLink?>(null);
        }
    }

    public Task<IReadOnlyList<VisitorConversationLink>> ListLinksAsync(TicketScope scope, VisitorSessionId session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
        {
            if (!links.TryGetValue((scope, session), out var list) || list.Count == 0)
                return Task.FromResult<IReadOnlyList<VisitorConversationLink>>([]);
            var result = new VisitorConversationLink[list.Count];
            for (var i = 0; i < list.Count; i++)
                result[i] = list[list.Count - 1 - i];
            return Task.FromResult<IReadOnlyList<VisitorConversationLink>>(result);
        }
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
            if (!linksByConversation.TryGetValue((scope, conversation), out var byConv) || byConv.Count == 0)
                return Task.FromResult<IReadOnlyList<VisitorSessionId>>([]);
            var sorted = new List<VisitorConversationLink>(byConv);
            if (sorted.Count > 1)
                sorted.Sort(CompareLinkDesc);
            var take = Math.Min(limit, sorted.Count);
            var found = new VisitorSessionId[take];
            for (var i = 0; i < take; i++)
                found[i] = sorted[i].Session;
            return Task.FromResult<IReadOnlyList<VisitorSessionId>>(found);
        }
    }

    public Task<IReadOnlyList<VisitorSessionId>> FindLiveSessionsAsync(TicketScope scope, ConversationId conversation, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IVisitorSessionDirectory.MaximumSessions);
        lock (gate)
        {
            if (!linksByConversation.TryGetValue((scope, conversation), out var byConv) || byConv.Count == 0)
                return Task.FromResult<IReadOnlyList<VisitorSessionId>>([]);
            var partition = AccessPartition.From(scope);
            var candidates = new List<VisitorConversationLink>(byConv.Count);
            for (var i = 0; i < byConv.Count; i++)
            {
                var link = byConv[i];
                if (sessions.TryGetValue((partition, link.Session), out var stored) && stored.Scope == scope && stored.Session.IsLive(now))
                    candidates.Add(link);
            }
            if (candidates.Count == 0)
                return Task.FromResult<IReadOnlyList<VisitorSessionId>>([]);
            if (candidates.Count > 1)
                candidates.Sort(CompareLinkDesc);
            var take = Math.Min(limit, candidates.Count);
            var found = new VisitorSessionId[take];
            for (var i = 0; i < take; i++)
                found[i] = candidates[i].Session;
            return Task.FromResult<IReadOnlyList<VisitorSessionId>>(found);
        }
    }

    private static int CompareLinkDesc(VisitorConversationLink a, VisitorConversationLink b)
    {
        var cmp = b.LinkedAt.CompareTo(a.LinkedAt);
        return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Session.Value, b.Session.Value);
    }

    public Task<StoreWriteResult<VisitorConversationLink, LiveChatWriteRejection>> AdvanceCursorAsync(TicketScope scope, VisitorSessionId session, ConversationId conversation, long readSequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireIds(session, conversation);
        ArgumentOutOfRangeException.ThrowIfNegative(readSequence);
        lock (gate)
        {
            var index = links.TryGetValue((scope, session), out var list) ? list.FindIndex(link => link.Conversation == conversation) : -1;
            if (list is null || index < 0)
                return Result<VisitorConversationLink>(StoreNotFound.Instance);
            var link = list[index];
            if (readSequence > link.ReadSequence)
            {
                list[index] = link = link with { ReadSequence = readSequence };
                if (linksByConversation.TryGetValue((scope, conversation), out var byConv))
                {
                    for (var i = 0; i < byConv.Count; i++)
                        if (byConv[i].Session == session)
                        {
                            byConv[i] = link;
                            break;
                        }
                }
            }
            return Result<VisitorConversationLink>(new StoreApplied<VisitorConversationLink>(link));
        }
    }

    public Task<int> PruneExpiredAsync(DateTimeOffset expiredBefore, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ILiveChatSessionStore.MaximumPrune);
        lock (gate)
        {
            var expired = new List<((AccessPartition Partition, VisitorSessionId Id) Key, StoredVisitorSession Stored)>();
            foreach (var pair in sessions)
            {
                if (pair.Value.Session.ExpiresAt < expiredBefore)
                {
                    expired.Add((pair.Key, pair.Value));
                    if (expired.Count == limit)
                        break;
                }
            }
            for (var i = 0; i < expired.Count; i++)
            {
                var (key, stored) = expired[i];
                sessions.Remove(key);
                if (links.Remove((stored.Scope, stored.Session.Id), out var sessionLinks))
                {
                    for (var j = 0; j < sessionLinks.Count; j++)
                    {
                        var convKey = (stored.Scope, sessionLinks[j].Conversation);
                        if (linksByConversation.TryGetValue(convKey, out var byConv))
                        {
                            byConv.RemoveAll(l => l.Session == stored.Session.Id);
                            if (byConv.Count == 0)
                                linksByConversation.Remove(convKey);
                        }
                    }
                }
            }
            return Task.FromResult(expired.Count);
        }
    }

    private static void RequireIds(VisitorSessionId session, ConversationId conversation)
    {
        if (!session.IsInitialized || conversation == default)
            throw new ArgumentException("Session and conversation identifiers are required.");
    }

    private static Task<StoreWriteResult<T, LiveChatWriteRejection>> Result<T>(StoreWriteResult<T, LiveChatWriteRejection> result) => Task.FromResult(result);
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
