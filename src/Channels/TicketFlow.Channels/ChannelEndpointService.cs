using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>
/// Default <see cref="IChannelEndpointService"/>. Existence and revision fences are checked before authorization decisions on stored
/// endpoints; creates authorize the target inbox first. Endpoint addresses are never echoed in errors.
/// </summary>
public sealed class ChannelEndpointService(
    IChannelEndpointStore endpoints,
    IChannelDispatchStore dispatches,
    IConversationStore conversations,
    IInboxStore inboxes,
    IAccessEvaluator evaluator) : IChannelEndpointService
{
    private readonly IChannelEndpointStore endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
    private readonly IChannelDispatchStore dispatches = dispatches ?? throw new ArgumentNullException(nameof(dispatches));
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IInboxStore inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async Task<ChannelResult<StoredChannelEndpoint>> GetAsync(PrincipalRef actor, ChannelKind kind, string configurationId, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        var stored = await endpoints.GetAsync(scope, kind, configurationId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ChannelNotFound.Instance;
        if (!await PermittedAsync(actor, ChannelPermissions.Read, stored.Endpoint.Inbox, cancellationToken).ConfigureAwait(false))
            return ChannelRefused.Forbidden;
        return new ChannelValue<StoredChannelEndpoint>(stored);
    }

    public async Task<ChannelResult<StoredChannelEndpoint>> CreateAsync(PrincipalRef actor, ChannelEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!await PermittedAsync(actor, ChannelPermissions.Manage, endpoint.Inbox, cancellationToken).ConfigureAwait(false))
            return ChannelRefused.Forbidden;
        if (await ValidateInboxAsync(scope, endpoint, cancellationToken).ConfigureAwait(false) is { } invalid)
            return invalid;
        return Map(await endpoints.CreateAsync(scope, endpoint, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ChannelResult<StoredChannelEndpoint>> UpdateAsync(PrincipalRef actor, ChannelEndpoint endpoint, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        ArgumentNullException.ThrowIfNull(endpoint);
        var stored = await endpoints.GetAsync(scope, endpoint.Kind, endpoint.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ChannelNotFound.Instance;
        if (stored.Revision != expectedRevision)
            return new ChannelConflict(stored.Revision);
        if (!await PermittedAsync(actor, ChannelPermissions.Manage, stored.Endpoint.Inbox, cancellationToken).ConfigureAwait(false)
            || (endpoint.Inbox != stored.Endpoint.Inbox && !await PermittedAsync(actor, ChannelPermissions.Manage, endpoint.Inbox, cancellationToken).ConfigureAwait(false)))
            return ChannelRefused.Forbidden;
        if (await ValidateInboxAsync(scope, endpoint, cancellationToken).ConfigureAwait(false) is { } invalid)
            return invalid;
        return Map(await endpoints.UpdateAsync(scope, endpoint, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ChannelResult<StoredChannelEndpoint>> DeleteAsync(PrincipalRef actor, ChannelKind kind, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        var stored = await endpoints.GetAsync(scope, kind, configurationId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ChannelNotFound.Instance;
        if (stored.Revision != expectedRevision)
            return new ChannelConflict(stored.Revision);
        if (!await PermittedAsync(actor, ChannelPermissions.Manage, stored.Endpoint.Inbox, cancellationToken).ConfigureAwait(false))
            return ChannelRefused.Forbidden;
        return Map(await endpoints.DeleteAsync(scope, kind, configurationId, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ChannelResult<StoredChannelDispatch>> GetDispatchAsync(PrincipalRef actor, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ChannelNotFound.Instance;
        if (!await PermittedAsync(actor, ChannelPermissions.Read, stored.Conversation.Inbox, cancellationToken).ConfigureAwait(false))
            return ChannelRefused.Forbidden;
        var dispatch = await dispatches.GetAsync(scope, conversation, message, cancellationToken).ConfigureAwait(false);
        return dispatch is null ? ChannelNotFound.Instance : new ChannelValue<StoredChannelDispatch>(dispatch);
    }

    private static TicketScope Scope(PrincipalRef actor) =>
        actor.Value is null ? throw new ArgumentException("A principal is required.", nameof(actor)) : actor.Scope;

    private Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, InboxId inbox, CancellationToken cancellationToken) =>
        evaluator.PermittedAsync(actor, permission, WorkspaceResources.Inbox(actor.Scope, inbox), cancellationToken);

    private async Task<ChannelRefused?> ValidateInboxAsync(TicketScope scope, ChannelEndpoint endpoint, CancellationToken cancellationToken)
    {
        var inbox = await inboxes.GetAsync(scope, endpoint.Inbox, cancellationToken).ConfigureAwait(false);
        if (inbox is null)
            return ChannelRefused.Invalid("The inbox does not exist.");
        if (inbox.Inbox.Channel.Kind != endpoint.Kind || !string.Equals(inbox.Inbox.Channel.ConfigurationId, endpoint.ConfigurationId, StringComparison.Ordinal))
            return ChannelRefused.Invalid("The inbox's channel binding does not match the endpoint.");
        return null;
    }

    private static ChannelResult<StoredChannelEndpoint> Map(ChannelWriteResult<StoredChannelEndpoint> result) => result switch
    {
        ChannelWriteApplied<StoredChannelEndpoint> applied => new ChannelValue<StoredChannelEndpoint>(applied.Value),
        ChannelWriteNotFound => ChannelNotFound.Instance,
        ChannelWriteConflict conflict => new ChannelConflict(conflict.CurrentRevision),
        ChannelWriteRejected rejected => new ChannelRefused(ChannelRefusal.Rejected, [rejected.Reason == ChannelWriteRejection.AddressTaken
            ? "The endpoint address is already bound." : "The endpoint capacity is exhausted."]),
    };
}
