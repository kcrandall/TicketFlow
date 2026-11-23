using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Realtime;

/// <summary>
/// Default <see cref="IRealtimeAgentActions"/>. The conversation must exist in the actor's scope (NotFound before authorization);
/// typing needs <c>conversation:reply</c>, read receipts <c>conversation:read</c>. Events go to the conversation topic and to every
/// linked visitor session (visitors see an anonymous support actor). Nothing is stored.
/// </summary>
public sealed class RealtimeAgentActions(IConversationStore conversations, IAccessEvaluator evaluator, IRealtimePublisher publisher, IEnumerable<IVisitorSessionDirectory> directories,
    TimeProvider time) : IRealtimeAgentActions
{
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly IRealtimePublisher publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IReadOnlyList<IVisitorSessionDirectory> directories = [.. directories ?? throw new ArgumentNullException(nameof(directories))];
    private readonly TimeProvider time = time ?? throw new ArgumentNullException(nameof(time));

    public async Task<RealtimeActionResult> SetTypingAsync(PrincipalRef actor, ConversationId conversation, bool typing, CancellationToken cancellationToken = default)
    {
        var (stored, refusal) = await AuthorizeAsync(actor, conversation, ConversationPermissions.Reply, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return new RealtimeSubscriptionRefused(refusal);
        await PublishAsync(stored, new TypingEvent(stored.Scope, conversation, Actor(actor), typing, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return RealtimeActionApplied.Instance;
    }

    public async Task<RealtimeActionResult> MarkReadAsync(PrincipalRef actor, ConversationId conversation, long upToSequence, CancellationToken cancellationToken = default)
    {
        var (stored, refusal) = await AuthorizeAsync(actor, conversation, ConversationPermissions.Read, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return new RealtimeSubscriptionRefused(refusal);
        if (upToSequence < 1 || upToSequence > stored.Conversation.LastSequence)
            return new RealtimeSubscriptionRefused(RealtimeRefusal.Invalid);
        await PublishAsync(stored, new ReadReceiptEvent(stored.Scope, conversation, Actor(actor), upToSequence, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return RealtimeActionApplied.Instance;
    }

    private async Task<(StoredConversation? Stored, RealtimeRefusal Refusal)> AuthorizeAsync(PrincipalRef actor, ConversationId conversation, Permission permission, CancellationToken cancellationToken)
    {
        if (actor.Value is null)
            throw new ArgumentException("A principal is required.", nameof(actor));
        if (conversation == default)
            return (null, RealtimeRefusal.Invalid);
        var stored = await conversations.GetAsync(actor.Scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return (null, RealtimeRefusal.NotFound);
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, ConversationAccess.Resource(stored)), cancellationToken).ConfigureAwait(false);
        return decision.IsPermitted ? (stored, default) : (null, RealtimeRefusal.Forbidden);
    }

    private async Task PublishAsync(StoredConversation stored, RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
    {
        var convTask = publisher.PublishAsync(new RealtimePublication(new ConversationTopic(stored.Scope, stored.Conversation.Id), realtimeEvent), cancellationToken);
        if (directories.Count == 0)
        {
            await convTask.ConfigureAwait(false);
            return;
        }
        List<Task>? tasks = null;
        for (var d = 0; d < directories.Count; d++)
        {
            var sessions = await directories[d].ListSessionsAsync(stored.Scope, stored.Conversation.Id, cancellationToken).ConfigureAwait(false);
            var limit = Math.Min(sessions.Count, IVisitorSessionDirectory.MaximumSessions);
            for (var i = 0; i < limit; i++)
                (tasks ??= [convTask]).Add(publisher.PublishAsync(new RealtimePublication(new VisitorSessionTopic(stored.Scope, sessions[i]), realtimeEvent), cancellationToken));
        }
        if (tasks is not null)
            await Task.WhenAll(tasks).ConfigureAwait(false);
        else
            await convTask.ConfigureAwait(false);
    }

    private static RealtimeActor Actor(PrincipalRef actor) => actor.Value is UserPrincipal user ? new AgentActor(user.UserId) : SupportActor.Instance;
}
