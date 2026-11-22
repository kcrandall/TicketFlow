using TicketFlow.Conversations;
using TicketFlow.Routing;

namespace TicketFlow.Realtime;

/// <summary>
/// Adapts E8 <see cref="RoutingEvent"/>s to realtime publications: agent status to the agent's topic; presence and reply locks to
/// the conversation topic; routed assignments to the agent and conversation topics; overflow and wait-exceeded to the conversation
/// and the affected inbox queue. Routing swallows observer failures, so fan-out never fails routing.
/// </summary>
public sealed class RealtimeRoutingEventObserver(IRealtimePublisher publisher, IConversationStore conversations) : IRoutingEventObserver
{
    private readonly IRealtimePublisher publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));

    public async Task OnRoutingEventAsync(RoutingEvent routingEvent, CancellationToken cancellationToken = default)
    {
        switch (routingEvent)
        {
            case AgentStatusChanged status:
                await PublishAsync(new AgentTopic(status.Scope, status.Agent), new AgentStatusChangedEvent(status.Scope, status.Agent, Map(status.Status), status.At), cancellationToken).ConfigureAwait(false);
                break;
            case ConversationPresenceChanged presence:
                await PublishAsync(new ConversationTopic(presence.Scope, presence.Conversation),
                    new PresenceChangedEvent(presence.Scope, presence.Conversation, presence.User, presence.Activity is { } activity ? Map(activity) : null, presence.At), cancellationToken).ConfigureAwait(false);
                break;
            case ReplyLockChanged replyLock:
                await PublishAsync(new ConversationTopic(replyLock.Scope, replyLock.Conversation),
                    new ReplyLockChangedEvent(replyLock.Scope, replyLock.Conversation, replyLock.Holder, replyLock.ExpiresAt, replyLock.At), cancellationToken).ConfigureAwait(false);
                break;
            case ConversationRouted routed:
                var assigned = new ConversationAssignedEvent(routed.Scope, routed.Conversation, routed.Agent, routed.Revision, routed.At);
                await PublishAsync(new AgentTopic(routed.Scope, routed.Agent), assigned, cancellationToken).ConfigureAwait(false);
                await PublishAsync(new ConversationTopic(routed.Scope, routed.Conversation), assigned, cancellationToken).ConfigureAwait(false);
                break;
            case ConversationOverflowed overflowed:
                var target = overflowed.Target switch
                {
                    OverflowToInbox inbox => inbox.Inbox,
                    OverflowToTeam => (await conversations.GetAsync(overflowed.Scope, overflowed.Conversation, cancellationToken).ConfigureAwait(false))?.Conversation.Inbox,
                };
                if (target is { } targetInbox)
                    await QueueAsync(new QueueChangedEvent(overflowed.Scope, overflowed.Conversation, targetInbox, QueueChange.Overflowed, overflowed.At), cancellationToken).ConfigureAwait(false);
                break;
            case QueueWaitExceeded exceeded:
                await QueueAsync(new QueueChangedEvent(exceeded.Scope, exceeded.Conversation, exceeded.TargetInbox, QueueChange.WaitExceeded, exceeded.At), cancellationToken).ConfigureAwait(false);
                break;
            case ConversationQueued queued:
                await QueueAsync(new QueueChangedEvent(queued.Scope, queued.Conversation, queued.TargetInbox, QueueChange.Queued, queued.At), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task QueueAsync(QueueChangedEvent queue, CancellationToken cancellationToken)
    {
        await PublishAsync(new ConversationTopic(queue.Scope, queue.Conversation), queue, cancellationToken).ConfigureAwait(false);
        await PublishAsync(new InboxQueueTopic(queue.Scope, queue.Inbox), queue, cancellationToken).ConfigureAwait(false);
    }

    internal static RealtimeAgentStatus Map(AgentStatus status) => status switch
    {
        AgentStatus.Online => RealtimeAgentStatus.Online,
        AgentStatus.Busy => RealtimeAgentStatus.Busy,
        AgentStatus.Away => RealtimeAgentStatus.Away,
        AgentStatus.Offline => RealtimeAgentStatus.Offline,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    internal static RealtimeActivity Map(ConversationActivity activity) => activity switch
    {
        ConversationActivity.Viewing => RealtimeActivity.Viewing,
        ConversationActivity.Composing => RealtimeActivity.Composing,
        _ => throw new ArgumentOutOfRangeException(nameof(activity)),
    };

    private Task PublishAsync(RealtimeTopic topic, RealtimeEvent realtimeEvent, CancellationToken cancellationToken) =>
        publisher.PublishAsync(new RealtimePublication(topic, realtimeEvent), cancellationToken);
}
