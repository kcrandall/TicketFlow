using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.LiveChat;

/// <summary>Default <see cref="ILiveChatWidgetService"/>. Existence and revision fences are checked before authorization decisions on stored records.</summary>
public sealed class LiveChatWidgetService(ILiveChatWidgetStore widgets, IInboxStore inboxes, IAccessEvaluator evaluator, IOptions<LiveChatOptions> options) : ILiveChatWidgetService
{
    private readonly ILiveChatWidgetStore widgets = widgets ?? throw new ArgumentNullException(nameof(widgets));
    private readonly IInboxStore inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly ChannelKind kind = new((options ?? throw new ArgumentNullException(nameof(options))).Value.Kind);

    public async Task<TicketResult<StoredLiveChatWidget, LiveChatRefused>> GetAsync(PrincipalRef actor, string configurationId, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(configurationId);
        var stored = await widgets.GetAsync(actor.Scope, configurationId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return LiveChatRefused.Of(LiveChatRefusal.NotFound);
        return await PermittedAsync(actor, LiveChatPermissions.Read, stored.Widget.Inbox, cancellationToken).ConfigureAwait(false)
            ? new TicketValue<StoredLiveChatWidget>(stored)
            : LiveChatRefused.Of(LiveChatRefusal.Forbidden);
    }

    public async Task<TicketResult<StoredLiveChatWidget, LiveChatRefused>> CreateAsync(PrincipalRef actor, LiveChatWidget widget, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(widget);
        if (!await PermittedAsync(actor, LiveChatPermissions.Manage, widget.Inbox, cancellationToken).ConfigureAwait(false))
            return LiveChatRefused.Of(LiveChatRefusal.Forbidden);
        if (await BindingErrorAsync(actor.Scope, widget, cancellationToken).ConfigureAwait(false) is { } error)
            return LiveChatRefused.Of(LiveChatRefusal.Invalid, error);
        return Map(await widgets.CreateAsync(actor.Scope, widget, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredLiveChatWidget, LiveChatRefused>> UpdateAsync(PrincipalRef actor, LiveChatWidget widget, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(widget);
        var current = await widgets.GetAsync(actor.Scope, widget.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return LiveChatRefused.Of(LiveChatRefusal.NotFound);
        if (current.Revision != expectedRevision)
            return new LiveChatRefused(LiveChatRefusal.Conflict, [$"Current revision is {current.Revision}."]);
        if (!await PermittedAsync(actor, LiveChatPermissions.Manage, current.Widget.Inbox, cancellationToken).ConfigureAwait(false)
            || (widget.Inbox != current.Widget.Inbox && !await PermittedAsync(actor, LiveChatPermissions.Manage, widget.Inbox, cancellationToken).ConfigureAwait(false)))
            return LiveChatRefused.Of(LiveChatRefusal.Forbidden);
        if (await BindingErrorAsync(actor.Scope, widget, cancellationToken).ConfigureAwait(false) is { } error)
            return LiveChatRefused.Of(LiveChatRefusal.Invalid, error);
        return Map(await widgets.UpdateAsync(actor.Scope, widget, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredLiveChatWidget, LiveChatRefused>> DeleteAsync(PrincipalRef actor, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(configurationId);
        var current = await widgets.GetAsync(actor.Scope, configurationId, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return LiveChatRefused.Of(LiveChatRefusal.NotFound);
        if (current.Revision != expectedRevision)
            return new LiveChatRefused(LiveChatRefusal.Conflict, [$"Current revision is {current.Revision}."]);
        if (!await PermittedAsync(actor, LiveChatPermissions.Manage, current.Widget.Inbox, cancellationToken).ConfigureAwait(false))
            return LiveChatRefused.Of(LiveChatRefusal.Forbidden);
        return Map(await widgets.DeleteAsync(actor.Scope, configurationId, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private async Task<string?> BindingErrorAsync(TicketScope scope, LiveChatWidget widget, CancellationToken cancellationToken)
    {
        var inbox = await inboxes.GetAsync(scope, widget.Inbox, cancellationToken).ConfigureAwait(false);
        if (inbox is null)
            return "The inbox does not exist.";
        return inbox.Inbox.Channel.Kind == kind && string.Equals(inbox.Inbox.Channel.ConfigurationId, widget.ConfigurationId, StringComparison.Ordinal)
            ? null
            : "The inbox is not bound to this live-chat configuration.";
    }

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, InboxId inbox, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, WorkspaceResources.Inbox(actor.Scope, inbox)), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private static TicketResult<StoredLiveChatWidget, LiveChatRefused> Map(StoreWriteResult<StoredLiveChatWidget, LiveChatWriteRejection> result) => result switch
    {
        StoreApplied<StoredLiveChatWidget> applied => new TicketValue<StoredLiveChatWidget>(applied.Value),
        StoreNotFound => LiveChatRefused.Of(LiveChatRefusal.NotFound),
        StoreConflict conflict => new LiveChatRefused(LiveChatRefusal.Conflict, conflict.CurrentRevision is { } revision ? [$"Current revision is {revision}."] : []),
        StoreRejected<LiveChatWriteRejection> => LiveChatRefused.Of(LiveChatRefusal.Rejected, "Widget capacity reached."),
    };

    private static void RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("A principal is required.", nameof(actor));
    }
}
