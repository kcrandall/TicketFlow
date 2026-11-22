using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Realtime;

/// <summary>What a granted subscription keeps: the planned <c>conversation:read</c> filter for queue topics.</summary>
internal sealed class SubscriptionGrant(AccessFilter? filter, DateTimeOffset plannedAt)
{
    public AccessFilter? Filter { get; set; } = filter;
    public DateTimeOffset PlannedAt { get; set; } = plannedAt;
}

/// <summary>
/// Subscription authorization and per-subscriber delivery filtering. One instance serves one publication (or one subscription
/// request) inside a DI scope and caches conversation reads and decisions for that unit only, so every delivery is decided against
/// current data. Agents never receive visitor-only events; visitors never receive agent events or agent identities.
/// </summary>
internal sealed class RealtimeDeliveryPolicy(IConversationStore conversations, IAccessEvaluator evaluator, IAccessQueryPlanner planner, TimeProvider time, RealtimeOptions options)
{
    private readonly Dictionary<ConversationId, StoredConversation?> stored = [];
    private readonly Dictionary<(object Principal, Permission Permission, ConversationId Conversation), bool> decisions = [];

    public async Task<(SubscriptionGrant? Grant, RealtimeRefusal Refusal)> AuthorizeAsync(RealtimeSubscriber subscriber, RealtimeTopic topic, CancellationToken cancellationToken)
    {
        if (subscriber.Scope != topic.Scope)
            return (null, RealtimeRefusal.Invalid);
        switch (subscriber)
        {
            case VisitorSubscriber visitor:
                return topic.Value is VisitorSessionTopic own && own.Session == visitor.Session
                    ? (new SubscriptionGrant(null, time.GetUtcNow()), default)
                    : (null, RealtimeRefusal.Forbidden);
            case AgentSubscriber agent:
                switch (topic)
                {
                    case ConversationTopic conversationTopic:
                        var conversation = await GetAsync(conversationTopic.Scope, conversationTopic.Conversation, cancellationToken).ConfigureAwait(false);
                        if (conversation is null)
                            return (null, RealtimeRefusal.NotFound);
                        return await PermittedAsync(agent.Principal, ConversationPermissions.Read, conversation, cancellationToken).ConfigureAwait(false)
                            ? (new SubscriptionGrant(null, time.GetUtcNow()), default)
                            : (null, RealtimeRefusal.Forbidden);
                    case InboxQueueTopic queue:
                        var inbox = await evaluator.AuthorizeAsync(new AccessRequest(agent.Principal, WorkspacePermissions.InboxRead, WorkspaceResources.Inbox(queue.Scope, queue.Inbox)),
                            cancellationToken).ConfigureAwait(false);
                        if (!inbox.IsPermitted)
                            return (null, RealtimeRefusal.Forbidden);
                        var filter = await planner.PlanAsync(new AccessQuery(agent.Principal, ConversationPermissions.Read, queue.Scope), cancellationToken).ConfigureAwait(false);
                        return (new SubscriptionGrant(filter, time.GetUtcNow()), default);
                    case AgentTopic agentTopic:
                        return agent.Principal.Value is UserPrincipal user && user.UserId == agentTopic.Agent
                            ? (new SubscriptionGrant(null, time.GetUtcNow()), default)
                            : (null, RealtimeRefusal.Forbidden);
                    case VisitorSessionTopic:
                        return (null, RealtimeRefusal.Forbidden);
                }
                break;
        }
        throw new InvalidOperationException("Unknown subscriber or topic.");
    }

    /// <summary>The event as <paramref name="subscriber"/> may see it on <paramref name="topic"/>, or <see langword="null"/> to drop it.</summary>
    public async Task<RealtimeEvent?> FilterAsync(RealtimeSubscriber subscriber, RealtimeTopic topic, SubscriptionGrant grant, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
    {
        switch (subscriber)
        {
            case VisitorSubscriber visitor:
                return ForVisitor(visitor, realtimeEvent);
            case AgentSubscriber agent:
                return await ForAgentAsync(agent.Principal, topic, grant, realtimeEvent, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Unknown subscriber.");
    }

    private static RealtimeEvent? ForVisitor(VisitorSubscriber visitor, RealtimeEvent realtimeEvent)
    {
        switch (realtimeEvent)
        {
            case VisitorMessageEvent message:
                return message.Session == visitor.Session ? realtimeEvent : (RealtimeEvent?)null;
            case VisitorSessionEndedEvent ended:
                return ended.Session == visitor.Session ? realtimeEvent : (RealtimeEvent?)null;
            case TypingEvent typing:
                return Anonymous(typing.Actor) is { } typingActor ? new TypingEvent(typing.Scope, typing.Conversation, typingActor, typing.Typing, typing.At) : (RealtimeEvent?)null;
            case ReadReceiptEvent read:
                return Anonymous(read.Actor) is { } readActor ? new ReadReceiptEvent(read.Scope, read.Conversation, readActor, read.UpToSequence, read.At) : (RealtimeEvent?)null;
            case ConversationChangedEvent:
            case PresenceChangedEvent:
            case ReplyLockChangedEvent:
            case AgentStatusChangedEvent:
            case ConversationAssignedEvent:
            case QueueChangedEvent:
                return null;
        }
        return null;
    }

    /// <summary>Support-side actors become anonymous for visitors; the visitor's own actions are not echoed.</summary>
    private static RealtimeActor? Anonymous(RealtimeActor actor) => actor switch
    {
        AgentActor => (RealtimeActor?)SupportActor.Instance,
        SupportActor => (RealtimeActor?)SupportActor.Instance,
        VisitorActor => null,
    };

    private async Task<RealtimeEvent?> ForAgentAsync(PrincipalRef principal, RealtimeTopic topic, SubscriptionGrant grant, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
    {
        switch (realtimeEvent)
        {
            case VisitorMessageEvent:
            case VisitorSessionEndedEvent:
                return null;
            case AgentStatusChangedEvent status:
                return topic.Value is AgentTopic own && own.Agent == status.Agent ? realtimeEvent : (RealtimeEvent?)null;
            case ConversationChangedEvent:
            case PresenceChangedEvent:
            case ReplyLockChangedEvent:
            case ConversationAssignedEvent:
            case QueueChangedEvent:
            case TypingEvent:
            case ReadReceiptEvent:
                break;
        }

        var conversationId = realtimeEvent.Conversation!.Value;
        var conversation = await GetAsync(realtimeEvent.Scope, conversationId, cancellationToken).ConfigureAwait(false);
        if (conversation is null)
        {
            // A deleted conversation can no longer be authorized: only subscribers already granted the conversation topic learn that it was deleted.
            return realtimeEvent.Value is ConversationChangedEvent { Kind: ConversationChangeKind.Deleted } && topic.Value is ConversationTopic ? realtimeEvent : (RealtimeEvent?)null;
        }

        if (topic.Value is InboxQueueTopic)
        {
            if (time.GetUtcNow() - grant.PlannedAt >= options.QueueFilterLifetime || grant.Filter is null)
            {
                grant.Filter = await planner.PlanAsync(new AccessQuery(principal, ConversationPermissions.Read, conversation.Scope), cancellationToken).ConfigureAwait(false);
                grant.PlannedAt = time.GetUtcNow();
            }
            if (!AccessFilterEvaluator.Matches(grant.Filter, ConversationAccess.Resource(conversation)))
                return null;
        }
        else if (!await PermittedAsync(principal, ConversationPermissions.Read, conversation, cancellationToken).ConfigureAwait(false))
            return null;

        if (realtimeEvent.Value is ConversationChangedEvent changed && changed.Events.Any(summary => summary.Kind == ConversationEventKind.Note)
            && !await PermittedAsync(principal, ConversationPermissions.NoteRead, conversation, cancellationToken).ConfigureAwait(false))
        {
            var visible = changed.Events.Where(summary => summary.Kind != ConversationEventKind.Note).ToList();
            if (visible.Count == 0 && changed.Kind == ConversationChangeKind.EventsAppended)
                return null;
            return changed.WithEvents(visible);
        }
        return realtimeEvent;
    }

    private async Task<StoredConversation?> GetAsync(TicketScope scope, ConversationId id, CancellationToken cancellationToken)
    {
        if (stored.TryGetValue(id, out var cached))
            return cached;
        var conversation = await conversations.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        stored[id] = conversation;
        return conversation;
    }

    private async Task<bool> PermittedAsync(PrincipalRef principal, Permission permission, StoredConversation conversation, CancellationToken cancellationToken)
    {
        var key = (principal.Value!, permission, conversation.Conversation.Id);
        if (decisions.TryGetValue(key, out var permitted))
            return permitted;
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(principal, permission, ConversationAccess.Resource(conversation)), cancellationToken).ConfigureAwait(false);
        decisions[key] = decision.IsPermitted;
        return decision.IsPermitted;
    }
}
