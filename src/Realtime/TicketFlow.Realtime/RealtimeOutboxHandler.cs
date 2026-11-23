using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Realtime;

/// <summary>
/// Publishes every conversation outbox change (consumer <see cref="RealtimeOptions.OutboxConsumer"/>) as an identifier-only
/// <see cref="ConversationChangedEvent"/> to the conversation topic, the inbox queue topic and the current assignee's agent topic.
/// Delivery is filtered per subscriber by the registry. Redelivered entries publish again (clients deduplicate by revision).
/// </summary>
public sealed class RealtimeOutboxHandler(IRealtimePublisher publisher, IConversationStore conversations) : IConversationOutboxHandler, IConversationOutboxBatchHandler
{
    private readonly IRealtimePublisher publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));

    public string Consumer => RealtimeOptions.OutboxConsumer;

    public async Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var change = entry.Change;
        var notification = ConversationChangedEvent.From(change);
        await publisher.PublishAsync(new RealtimePublication(new ConversationTopic(change.Scope, change.Conversation), notification), cancellationToken).ConfigureAwait(false);
        await publisher.PublishAsync(new RealtimePublication(new InboxQueueTopic(change.Scope, change.Inbox), notification), cancellationToken).ConfigureAwait(false);
        if (change.Kind == ConversationChangeKind.Deleted)
            return;
        if (await conversations.GetAsync(change.Scope, change.Conversation, cancellationToken).ConfigureAwait(false) is { Conversation.Assignee: UserId assignee })
            await publisher.PublishAsync(new RealtimePublication(new AgentTopic(change.Scope, assignee), notification), cancellationToken).ConfigureAwait(false);
    }

    public async Task HandleBatchAsync(IReadOnlyList<ConversationOutboxEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;
        if (entries.Count == 1)
        {
            await HandleAsync(entries[0], cancellationToken).ConfigureAwait(false);
            return;
        }
        // Batch-fetch distinct non-deleted conversations per scope once instead of N individual GetAsync calls.
        var idsByScope = new Dictionary<TicketScope, HashSet<ConversationId>>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i] ?? throw new ArgumentNullException(nameof(entries));
            if (entry.Change.Kind == ConversationChangeKind.Deleted)
                continue;
            if (!idsByScope.TryGetValue(entry.Change.Scope, out var set))
                idsByScope[entry.Change.Scope] = set = [];
            set.Add(entry.Change.Conversation);
        }
        var snapshotsByScope = new Dictionary<TicketScope, IReadOnlyDictionary<ConversationId, StoredConversation>>(idsByScope.Count);
        foreach (var (scope, set) in idsByScope)
            snapshotsByScope[scope] = await conversations.GetManyAsync(scope, [.. set], cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < entries.Count; i++)
        {
            var change = entries[i].Change;
            var notification = ConversationChangedEvent.From(change);
            await publisher.PublishAsync(new RealtimePublication(new ConversationTopic(change.Scope, change.Conversation), notification), cancellationToken).ConfigureAwait(false);
            await publisher.PublishAsync(new RealtimePublication(new InboxQueueTopic(change.Scope, change.Inbox), notification), cancellationToken).ConfigureAwait(false);
            if (change.Kind != ConversationChangeKind.Deleted
                && snapshotsByScope.TryGetValue(change.Scope, out var map)
                && map.TryGetValue(change.Conversation, out var stored)
                && stored.Conversation.Assignee is { } assignee)
                await publisher.PublishAsync(new RealtimePublication(new AgentTopic(change.Scope, assignee), notification), cancellationToken).ConfigureAwait(false);
        }
    }
}
