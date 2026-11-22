using TicketFlow.Conversations;

namespace TicketFlow.Routing;

/// <summary>
/// The routing consumer of the conversation outbox (<see cref="ConsumerName"/>). Every change except delivery updates re-runs the
/// engine for its conversation: started and reopened conversations, incoming messages on unassigned conversations and unassignments
/// are queued and routed; assignments, resolutions and deletions update the load projection and free capacity. Idempotent: the engine
/// reconciles from the current conversation snapshot.
/// </summary>
public sealed class RoutingOutboxHandler(IRoutingEngine engine) : IConversationOutboxHandler
{
    public const string ConsumerName = "routing";

    public string Consumer => ConsumerName;

    public async Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Change.Kind == ConversationChangeKind.DeliveryUpdated)
            return;
        await engine.RouteAsync(entry.Change.Scope, entry.Change.Conversation, cancellationToken).ConfigureAwait(false);
    }
}
