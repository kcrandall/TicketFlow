using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Routing;

/// <summary>
/// Default <see cref="IConversationCollisionService"/>. Only user principals take part. The conversation must exist in the actor's scope
/// (NotFound precedes authorization, like conversation commands); presence needs <c>conversation:read</c> and reply locks
/// <c>conversation:reply</c> on the current conversation. Changes are published to <see cref="IRoutingEventObserver"/>s.
/// </summary>
public sealed class ConversationCollisionService : IConversationCollisionService
{
    private readonly IAccessEvaluator access;
    private readonly IConversationStore conversations;
    private readonly IConversationPresenceStore presence;
    private readonly RoutingEventPublisher events;
    private readonly RoutingOptions options;
    private readonly TimeProvider time;

    public ConversationCollisionService(IAccessEvaluator access, IConversationStore conversations, IConversationPresenceStore presence,
        IEnumerable<IRoutingEventObserver> observers, IOptions<RoutingOptions> options, TimeProvider time)
    {
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.presence = presence ?? throw new ArgumentNullException(nameof(presence));
        events = new RoutingEventPublisher(observers ?? throw new ArgumentNullException(nameof(observers)));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<RoutingResult<ConversationPresence>> ReportActivityAsync(PrincipalRef actor, ConversationId conversation, ConversationActivity activity, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(activity))
            return RoutingRefused.Invalid("Unknown activity.");
        var check = await AuthorizeAsync(actor, conversation, RoutingPermissions.PresenceReport, cancellationToken).ConfigureAwait(false);
        if (check.User is not { } user)
            return check.Fail<ConversationPresence>();
        return await presence.TouchAsync(actor.Scope, conversation, user.UserId, activity, options.ConversationPresenceTtl, cancellationToken).ConfigureAwait(false) switch
        {
            PresenceTouched touched => await PublishedAsync(touched, actor.Scope, conversation, cancellationToken).ConfigureAwait(false),
            PresenceCapacityExceeded => RoutingRefused.Rejected("The conversation has the maximum number of present agents."),
        };
    }

    public async Task<RoutingResult<bool>> LeaveAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        var check = await AuthorizeAsync(actor, conversation, RoutingPermissions.PresenceReport, cancellationToken).ConfigureAwait(false);
        if (check.User is not { } user)
            return check.Fail<bool>();
        var left = await presence.LeaveAsync(actor.Scope, conversation, user.UserId, cancellationToken).ConfigureAwait(false);
        if (left)
            await events.PublishAsync(new ConversationPresenceChanged(actor.Scope, conversation, user.UserId, null, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return new RoutingValue<bool>(left);
    }

    public async Task<RoutingResult<IReadOnlyList<ConversationPresence>>> ListActivityAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        var check = await AuthorizeAsync(actor, conversation, RoutingPermissions.PresenceReport, cancellationToken).ConfigureAwait(false);
        if (check.User is not { } user)
            return check.Fail<IReadOnlyList<ConversationPresence>>();
        return new RoutingValue<IReadOnlyList<ConversationPresence>>(await presence.ListAsync(actor.Scope, conversation, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RoutingResult<ReplyLockResult>> AcquireReplyLockAsync(PrincipalRef actor, ConversationId conversation, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        var lease = duration ?? options.ReplyLockDuration;
        if (lease <= TimeSpan.Zero || lease > options.MaximumReplyLockDuration)
            return RoutingRefused.Invalid("The lock duration is out of range.");
        var check = await AuthorizeAsync(actor, conversation, RoutingPermissions.ReplyLock, cancellationToken).ConfigureAwait(false);
        if (check.User is not { } user)
            return check.Fail<ReplyLockResult>();
        var result = await presence.AcquireLockAsync(actor.Scope, conversation, user.UserId, lease, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case ReplyLockAcquired acquired:
                await events.PublishAsync(new ReplyLockChanged(actor.Scope, conversation, acquired.Lock.Holder, acquired.Lock.ExpiresAt, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                break;
            case ReplyLockHeld:
                break;
        }
        return new RoutingValue<ReplyLockResult>(result);
    }

    public async Task<RoutingResult<ReplyLock?>> GetReplyLockAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        var check = await AuthorizeAsync(actor, conversation, RoutingPermissions.PresenceReport, cancellationToken).ConfigureAwait(false);
        if (check.User is not { } user)
            return check.Fail<ReplyLock?>();
        return new RoutingValue<ReplyLock?>(await presence.GetLockAsync(actor.Scope, conversation, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RoutingResult<bool>> ReleaseReplyLockAsync(PrincipalRef actor, ConversationId conversation, long fence, CancellationToken cancellationToken = default)
    {
        var check = await AuthorizeAsync(actor, conversation, RoutingPermissions.ReplyLock, cancellationToken).ConfigureAwait(false);
        if (check.User is not { } user)
            return check.Fail<bool>();
        var released = await presence.ReleaseLockAsync(actor.Scope, conversation, user.UserId, fence, cancellationToken).ConfigureAwait(false);
        if (released)
            await events.PublishAsync(new ReplyLockChanged(actor.Scope, conversation, null, null, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return new RoutingValue<bool>(released);
    }

    private async Task<RoutingResult<ConversationPresence>> PublishedAsync(PresenceTouched touched, TicketScope scope, ConversationId conversation, CancellationToken cancellationToken)
    {
        if (touched.Changed)
            await events.PublishAsync(new ConversationPresenceChanged(scope, conversation, touched.Presence.User, touched.Presence.Activity, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        return new RoutingValue<ConversationPresence>(touched.Presence);
    }

    /// <summary>User principal → existence → authorization on the current conversation.</summary>
    private async Task<Check> AuthorizeAsync(PrincipalRef actor, ConversationId conversation, Permission permission, CancellationToken cancellationToken)
    {
        RoutingGuards.RequireActor(actor);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        if (actor.Value is not UserPrincipal user)
            return new Check(null, RoutingRefused.Rejected("Only user principals take part in collision detection."), false);
        if (await conversations.GetAsync(actor.Scope, conversation, cancellationToken).ConfigureAwait(false) is not { } stored)
            return new Check(null, null, true);
        if (!await RoutingGuards.PermittedAsync(access, actor, permission, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false))
            return new Check(null, RoutingRefused.Forbidden, false);
        return new Check(user, null, false);
    }

    private readonly record struct Check(UserPrincipal? User, RoutingRefused? Refused, bool NotFound)
    {
        public RoutingResult<T> Fail<T>() => NotFound ? RoutingNotFound.Instance : Refused!;
    }
}
