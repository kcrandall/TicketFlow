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
    IAccessEvaluator evaluator,
    IChannelAddressOwnershipValidator ownership) : IChannelEndpointService
{
    private readonly IChannelEndpointStore endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
    private readonly IChannelDispatchStore dispatches = dispatches ?? throw new ArgumentNullException(nameof(dispatches));
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IInboxStore inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly IChannelAddressOwnershipValidator ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));

    public async Task<TicketResult<StoredChannelEndpoint>> GetAsync(PrincipalRef actor, ChannelKind kind, string configurationId, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        var stored = await endpoints.GetAsync(scope, kind, configurationId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return TicketFailure.NotFound;
        if (!await PermittedAsync(actor, ChannelPermissions.Read, stored.Endpoint.Inbox, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return new TicketValue<StoredChannelEndpoint>(stored);
    }

    public async Task<TicketResult<StoredChannelEndpoint>> CreateAsync(PrincipalRef actor, ChannelEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!await PermittedAsync(actor, ChannelPermissions.Manage, endpoint.Inbox, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        if (await ValidateInboxAsync(scope, endpoint, cancellationToken).ConfigureAwait(false) is { } invalid)
            return invalid;
        if (!await ownership.MayBindAsync(actor, scope, endpoint, cancellationToken).ConfigureAwait(false))
            return NotOwned;
        return Map(await endpoints.CreateAsync(scope, endpoint, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredChannelEndpoint>> UpdateAsync(PrincipalRef actor, ChannelEndpoint endpoint, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        ArgumentNullException.ThrowIfNull(endpoint);
        var stored = await endpoints.GetAsync(scope, endpoint.Kind, endpoint.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return TicketFailure.NotFound;
        if (stored.Revision != expectedRevision)
            return TicketFailure.Conflict(stored.Revision);
        if (!await PermittedAsync(actor, ChannelPermissions.Manage, stored.Endpoint.Inbox, cancellationToken).ConfigureAwait(false)
            || (endpoint.Inbox != stored.Endpoint.Inbox && !await PermittedAsync(actor, ChannelPermissions.Manage, endpoint.Inbox, cancellationToken).ConfigureAwait(false)))
            return TicketFailure.Forbidden;
        if (await ValidateInboxAsync(scope, endpoint, cancellationToken).ConfigureAwait(false) is { } invalid)
            return invalid;
        if (endpoint.Address != stored.Endpoint.Address && !await ownership.MayBindAsync(actor, scope, endpoint, cancellationToken).ConfigureAwait(false))
            return NotOwned;
        return Map(await endpoints.UpdateAsync(scope, endpoint, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredChannelEndpoint>> DeleteAsync(PrincipalRef actor, ChannelKind kind, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        var stored = await endpoints.GetAsync(scope, kind, configurationId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return TicketFailure.NotFound;
        if (stored.Revision != expectedRevision)
            return TicketFailure.Conflict(stored.Revision);
        if (!await PermittedAsync(actor, ChannelPermissions.Manage, stored.Endpoint.Inbox, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return Map(await endpoints.DeleteAsync(scope, kind, configurationId, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredChannelDispatch>> GetDispatchAsync(PrincipalRef actor, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default)
    {
        var scope = Scope(actor);
        var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return TicketFailure.NotFound;
        if (!await PermittedAsync(actor, ChannelPermissions.Read, stored.Conversation.Inbox, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var dispatch = await dispatches.GetAsync(scope, conversation, message, cancellationToken).ConfigureAwait(false);
        return dispatch is null ? TicketFailure.NotFound : new TicketValue<StoredChannelDispatch>(dispatch);
    }

    private static TicketFailure NotOwned { get; } = TicketFailure.Rejected("The endpoint address overlaps a provider address bound by another scope.", code: "address-not-owned");

    private static TicketScope Scope(PrincipalRef actor) =>
        actor is null ? throw new ArgumentException("A principal is required.", nameof(actor)) : actor.Scope;

    private Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, InboxId inbox, CancellationToken cancellationToken) =>
        evaluator.PermittedAsync(actor, permission, WorkspaceResources.Inbox(actor.Scope, inbox), cancellationToken);

    private async Task<TicketFailure?> ValidateInboxAsync(TicketScope scope, ChannelEndpoint endpoint, CancellationToken cancellationToken)
    {
        var inbox = await inboxes.GetAsync(scope, endpoint.Inbox, cancellationToken).ConfigureAwait(false);
        if (inbox is null)
            return TicketFailure.Invalid("The inbox does not exist.");
        if (inbox.Inbox.Channel.Kind != endpoint.Kind || !string.Equals(inbox.Inbox.Channel.ConfigurationId, endpoint.ConfigurationId, StringComparison.Ordinal))
            return TicketFailure.Invalid("The inbox's channel binding does not match the endpoint.");
        return null;
    }

    private static TicketResult<StoredChannelEndpoint> Map(StoreWriteResult<StoredChannelEndpoint, ChannelWriteRejection> result) => result switch
    {
        StoreApplied<StoredChannelEndpoint> applied => new TicketValue<StoredChannelEndpoint>(applied.Value),
        StoreNotFound => TicketFailure.NotFound,
        StoreConflict conflict => TicketFailure.Conflict(conflict.CurrentRevision),
        StoreRejected<ChannelWriteRejection> rejected => new TicketFailure(TicketFailureKind.Rejected, [rejected.Reason == ChannelWriteRejection.AddressTaken
            ? "The endpoint address is already bound." : "The endpoint capacity is exhausted."]),
    };
}
