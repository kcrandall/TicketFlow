using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Realtime;

/// <summary>
/// Publishes every conversation outbox change (consumer <see cref="RealtimeOptions.OutboxConsumer"/>) as an identifier-only
/// <see cref="ConversationChangedEvent"/> to the conversation topic, the inbox queue topic and the current assignee's agent topic.
/// Delivery is filtered per subscriber by the registry. Redelivered entries publish again (clients deduplicate by revision).
/// </summary>
public sealed class RealtimeOutboxHandler(IRealtimePublisher publisher, IConversationStore conversations) : IConversationOutboxHandler
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
}
