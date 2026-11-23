using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Realtime;

/// <summary>What a granted subscription keeps: the planned <c>conversation:read</c> filter for queue topics. Mutated only by the fan-out worker.</summary>
internal sealed class SubscriptionGrant(AccessFilter? filter, DateTimeOffset plannedAt)
{
    public AccessFilter? Filter { get; set; } = filter;
    public DateTimeOffset PlannedAt { get; set; } = plannedAt;
}

/// <summary>
/// A principal's cached Access decision context (<c>conversation:read</c>, <c>note:read</c>) shared by all of its subscriptions on this
/// node, reloaded after <see cref="RealtimeOptions.DecisionContextLifetime"/>. Owned by the single fan-out worker (no locking).
/// </summary>
internal sealed class PrincipalAuthorization(PrincipalRef principal)
{
    private static readonly Permission[] Loaded = [ConversationPermissions.Read, ConversationPermissions.NoteRead];

    private IAccessDecisionContext? context;
    private DateTimeOffset loadedAt;

    public PrincipalRef Principal { get; } = principal;

    public DateTimeOffset LoadedAt => loadedAt;

    public ValueTask<bool> PermittedAsync(Permission permission, StoredConversation conversation, IAccessEvaluator evaluator, TimeProvider time, TimeSpan lifetime,
        CancellationToken cancellationToken) =>
        PermittedAsync(permission, conversation.Scope, ConversationAccess.Resource(conversation), evaluator, time, lifetime, cancellationToken);

    public async ValueTask<bool> PermittedAsync(Permission permission, TicketScope scope, AccessResource resource, IAccessEvaluator evaluator, TimeProvider time, TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        if (context is null || context.Scope != scope || time.GetUtcNow() - loadedAt >= lifetime)
            await LoadAsync(scope, evaluator, time, cancellationToken).ConfigureAwait(false);
        try
        {
            return (await context!.DecideAsync(permission, resource, cancellationToken).ConfigureAwait(false)).IsPermitted;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A cached context may hold services of a finished scope (for example an audit sink); reload once in the current scope.
            await LoadAsync(scope, evaluator, time, cancellationToken).ConfigureAwait(false);
            return (await context!.DecideAsync(permission, resource, cancellationToken).ConfigureAwait(false)).IsPermitted;
        }
    }

    private async Task LoadAsync(TicketScope scope, IAccessEvaluator evaluator, TimeProvider time, CancellationToken cancellationToken)
    {
        context = null;
        context = await evaluator.LoadDecisionContextAsync(Principal, scope, Loaded, cancellationToken).ConfigureAwait(false);
        loadedAt = time.GetUtcNow();
    }
}

/// <summary>
/// Subscription authorization (point checks against current data) and per-subscriber delivery filtering. Agents never receive
/// visitor-only events; visitors never receive agent events or agent identities.
/// </summary>
internal static class RealtimeDeliveryPolicy
{
    public static async Task<(SubscriptionGrant? Grant, RealtimeRefusal Refusal)> AuthorizeAsync(RealtimeSubscriber subscriber, RealtimeTopic topic, IConversationStore conversations,
        IAccessEvaluator evaluator, IAccessQueryPlanner planner, TimeProvider time, CancellationToken cancellationToken)
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
                        var conversation = await conversations.GetAsync(conversationTopic.Scope, conversationTopic.Conversation, cancellationToken).ConfigureAwait(false);
                        if (conversation is null)
                            return (null, RealtimeRefusal.NotFound);
                        return (await evaluator.AuthorizeAsync(new AccessRequest(agent.Principal, ConversationPermissions.Read, ConversationAccess.Resource(conversation)), cancellationToken)
                            .ConfigureAwait(false)).IsPermitted
                            ? (new SubscriptionGrant(null, time.GetUtcNow()), default)
                            : (null, RealtimeRefusal.Forbidden);
                    case InboxQueueTopic queue:
                        if (!await InboxReadableAsync(agent.Principal, queue, evaluator, cancellationToken).ConfigureAwait(false))
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

    public static async Task<bool> InboxReadableAsync(PrincipalRef principal, InboxQueueTopic queue, IAccessEvaluator evaluator, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(principal, WorkspacePermissions.InboxRead, WorkspaceResources.Inbox(queue.Scope, queue.Inbox)), cancellationToken)
            .ConfigureAwait(false)).IsPermitted;

    public static RealtimeEvent? ForVisitor(VisitorSubscriber visitor, RealtimeEvent realtimeEvent)
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

    /// <summary>Agent events that need no conversation: returns the decided event, or <see langword="null"/> when the conversation must be consulted.</summary>
    public static (bool Decided, RealtimeEvent? Event) WithoutConversation(RealtimeTopic topic, RealtimeEvent realtimeEvent)
    {
        switch (realtimeEvent)
        {
            case VisitorMessageEvent:
            case VisitorSessionEndedEvent:
                return (true, null);
            case AgentStatusChangedEvent status:
                return (true, topic.Value is AgentTopic own && own.Agent == status.Agent ? realtimeEvent : (RealtimeEvent?)null);
            case ConversationChangedEvent:
            case PresenceChangedEvent:
            case ReplyLockChangedEvent:
            case ConversationAssignedEvent:
            case QueueChangedEvent:
            case TypingEvent:
            case ReadReceiptEvent:
                return (false, null);
        }
        return (true, null);
    }

    public static bool HasNotes(RealtimeEvent realtimeEvent)
    {
        if (realtimeEvent.Value is not ConversationChangedEvent changed)
            return false;
        var events = changed.Events;
        for (var i = 0; i < events.Count; i++)
            if (events[i].Kind == ConversationEventKind.Note)
                return true;
        return false;
    }

    public static RealtimeEvent? StripNotes(ConversationChangedEvent changed)
    {
        var events = changed.Events;
        var visible = new List<ConversationEventSummary>(events.Count);
        for (var i = 0; i < events.Count; i++)
            if (events[i].Kind != ConversationEventKind.Note)
                visible.Add(events[i]);
        if (visible.Count == 0 && changed.Kind == ConversationChangeKind.EventsAppended)
            return null;
        return changed.WithEvents(visible);
    }

    /// <summary>The event after note stripping for a principal already allowed to read the conversation.</summary>
    public static async ValueTask<RealtimeEvent?> StripNotesAsync(RealtimeEvent realtimeEvent, Func<ValueTask<bool>> noteReadable)
    {
        if (realtimeEvent.Value is ConversationChangedEvent changed && HasNotes(realtimeEvent)
            && !await noteReadable().ConfigureAwait(false))
            return StripNotes(changed);
        return realtimeEvent;
    }

    /// <summary>Support-side actors become anonymous for visitors; the visitor's own actions are not echoed.</summary>
    private static RealtimeActor? Anonymous(RealtimeActor actor) => actor switch
    {
        AgentActor => (RealtimeActor?)SupportActor.Instance,
        SupportActor => (RealtimeActor?)SupportActor.Instance,
        VisitorActor => null,
    };
}
