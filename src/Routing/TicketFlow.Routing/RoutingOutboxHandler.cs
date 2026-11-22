using TicketFlow.Conversations;

namespace TicketFlow.Routing;

/// <summary>
/// The routing consumer of the conversation outbox (<see cref="ConsumerName"/>). It runs the engine only for changes that can affect
/// routing: started and deleted conversations, and appended batches containing an incoming message or a status, assignment, team,
/// label or attribute change (attributes feed skill rules and Access conditions; the conversation inbox is immutable). Notes, outgoing
/// replies, priority, participant, contact, system and extension events and delivery updates are skipped without touching any store.
/// Idempotent: the engine reconciles from the current conversation snapshot.
/// </summary>
public sealed class RoutingOutboxHandler(IRoutingEngine engine) : IConversationOutboxHandler
{
    public const string ConsumerName = "routing";

    public string Consumer => ConsumerName;

    public async Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!IsRelevant(entry.Change))
            return;
        await engine.RouteAsync(entry.Change.Scope, entry.Change.Conversation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when <paramref name="change"/> can change whether or to whom the conversation is routed.</summary>
    public static bool IsRelevant(ConversationChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.Kind switch
        {
            ConversationChangeKind.Started or ConversationChangeKind.Deleted => true,
            ConversationChangeKind.EventsAppended => change.Events.Any(summary => summary.Kind switch
            {
                ConversationEventKind.Message => summary.Direction == MessageDirection.Incoming,
                ConversationEventKind.Status or ConversationEventKind.Assignment or ConversationEventKind.Team or ConversationEventKind.Labels
                    or ConversationEventKind.Attribute => true,
                _ => false,
            }),
            _ => false,
        };
    }
}
