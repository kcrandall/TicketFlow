using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>
/// The default <see cref="IEventSubscriptionService"/>: authorizes on the actor's workspace through <see cref="IAccessEvaluator"/> before any
/// existence or revision outcome is revealed, validates sink kinds and targets against the registered <see cref="IEventSink"/>s (for the
/// actor's scope) and writes through <see cref="IEventSubscriptionStore"/>. Creating or updating a subscription also requires that the actor
/// could confer every live allow grant of the subscription's API-client principal (<see cref="IAccessAdministrator.CheckCanConferAsync"/>),
/// so <c>events:manage</c> cannot be used to receive data through a more privileged principal (confused deputy).
/// </summary>
public sealed class EventSubscriptionService : IEventSubscriptionService
{
    private readonly IEventSubscriptionStore store;
    private readonly IEventLog log;
    private readonly IAccessEvaluator access;
    private readonly IAccessAdministrator administrator;
    private readonly IAccessGrantStore grants;
    private readonly IReadOnlyDictionary<string, IEventSink> sinks;
    private readonly TimeProvider time;

    public EventSubscriptionService(IEventSubscriptionStore store, IEventLog log, IAccessEvaluator access, IAccessAdministrator administrator, IAccessGrantStore grants,
        IEnumerable<IEventSink> sinks, TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.administrator = administrator ?? throw new ArgumentNullException(nameof(administrator));
        this.grants = grants ?? throw new ArgumentNullException(nameof(grants));
        this.sinks = EventSinks.Index(sinks);
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<TicketResult<EventSubscriptionView>> CreateAsync(PrincipalRef actor, EventSubscription subscription, EventSubscriptionStart? start = null,
        CancellationToken cancellationToken = default)
    {
        var scope = actor.Scope;
        ArgumentNullException.ThrowIfNull(subscription);
        if (await RefuseAsync(actor, subscription, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (Invalid(scope, subscription) is { } invalid)
            return invalid;
        var head = await log.GetHeadAsync(scope, cancellationToken).ConfigureAwait(false);
        var cursor = (start ?? EventStartAtLatest.Instance) switch
        {
            EventStartAtLatest => head.Last,
            EventStartAtEarliest => head.First is { } first ? first - 1 : head.Last,
            EventStartAfterSequence after => Math.Min(after.Sequence, head.Last),
        };
        return await ViewAsync(await store.CreateAsync(scope, subscription, cursor, time.GetUtcNow(), cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TicketResult<EventSubscriptionView>> UpdateAsync(PrincipalRef actor, EventSubscription subscription, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = actor.Scope;
        ArgumentNullException.ThrowIfNull(subscription);
        if (await RefuseAsync(actor, subscription, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        var current = await store.GetAsync(scope, subscription.Id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return TicketFailure.NotFound;
        if (current.Revision != expectedRevision)
            return TicketFailure.Conflict(current.Revision);
        if (Invalid(scope, subscription) is { } invalid)
            return invalid;
        return await ViewAsync(await store.UpdateAsync(scope, subscription, expectedRevision, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketResult<EventSubscriptionId>> DeleteAsync(PrincipalRef actor, EventSubscriptionId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = actor.Scope;
        if (!await PermittedAsync(actor, EventPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var current = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return TicketFailure.NotFound;
        if (current.Revision != expectedRevision)
            return TicketFailure.Conflict(current.Revision);
        return await store.DeleteAsync(scope, id, expectedRevision, cancellationToken).ConfigureAwait(false) switch
        {
            StoreApplied<StoredEventSubscription> => new TicketValue<EventSubscriptionId>(id),
            StoreNotFound => TicketFailure.NotFound,
            StoreConflict conflict => TicketFailure.Conflict(conflict.CurrentRevision),
            StoreRejected<EventWriteRejection> rejected => TicketFailure.Rejected(rejected.Reason.ToString()),
        };
    }

    public async Task<TicketResult<EventSubscriptionView>> GetAsync(PrincipalRef actor, EventSubscriptionId id, CancellationToken cancellationToken = default)
    {
        if (!await PermittedAsync(actor, EventPermissions.Read, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var rowTask = store.GetWithStateAsync(actor.Scope, id, cancellationToken);
        var headTask = log.GetHeadAsync(actor.Scope, cancellationToken);
        await Task.WhenAll(rowTask, headTask).ConfigureAwait(false);
        if (rowTask.Result is not { } row)
            return TicketFailure.NotFound;
        return new TicketValue<EventSubscriptionView>(new EventSubscriptionView(row.Subscription, row.State, headTask.Result));
    }

    public async Task<TicketResult<IReadOnlyList<EventSubscriptionView>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        if (!await PermittedAsync(actor, EventPermissions.Read, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var listTask = store.ListWithStateAsync(actor.Scope, cancellationToken);
        var headTask = log.GetHeadAsync(actor.Scope, cancellationToken);
        await Task.WhenAll(listTask, headTask).ConfigureAwait(false);
        var rows = listTask.Result;
        var head = headTask.Result;
        var views = new EventSubscriptionView[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            views[i] = new EventSubscriptionView(rows[i].Subscription, rows[i].State, head);
        return new TicketValue<IReadOnlyList<EventSubscriptionView>>(views);
    }

    public async Task<TicketResult<EventSubscriptionView>> ReplayAsync(PrincipalRef actor, EventSubscriptionId id, long afterSequence, CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0)
            return TicketFailure.Invalid("The replay position cannot be negative.");
        if (!await PermittedAsync(actor, EventPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var current = await store.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return TicketFailure.NotFound;
        var head = await log.GetHeadAsync(actor.Scope, cancellationToken).ConfigureAwait(false);
        if (!await store.ReplayAsync(actor.Scope, id, Math.Min(afterSequence, head.Last), time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            return TicketFailure.NotFound;
        return new TicketValue<EventSubscriptionView>(await ViewAsync(current, head, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<IReadOnlyList<EventDeadLetter>>> ListDeadLettersAsync(PrincipalRef actor, EventSubscriptionId id, long afterSequence = 0, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0 || limit is < 1 or > IEventSubscriptionStore.MaximumDeadLetterRead)
            return TicketFailure.Invalid("Dead letters are read after a non-negative sequence, 1-200 at a time.");
        if (!await PermittedAsync(actor, EventPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        if (await store.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is null)
            return TicketFailure.NotFound;
        return new TicketValue<IReadOnlyList<EventDeadLetter>>(await store.ListDeadLettersAsync(actor.Scope, id, afterSequence, limit, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TicketFailure?> RefuseAsync(PrincipalRef actor, EventSubscription subscription, CancellationToken cancellationToken)
    {
        if (!await PermittedAsync(actor, EventPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        if (subscription.Privacy == EventPrivacy.Content && !await PermittedAsync(actor, EventPermissions.Content, cancellationToken).ConfigureAwait(false))
            return TicketFailure.ForbiddenWith(["Content subscriptions require events:content."]);
        // Confused deputy: the actor must be able to confer every live allow grant of the principal it delegates delivery to.
        var scope = actor.Scope;
        GrantSubject subject = subscription.Principal;
        var principalGrants = await grants.ListForSubjectsAsync(AccessPartition.From(scope), scope.WorkspaceId, [subject], cancellationToken).ConfigureAwait(false);
        if (principalGrants.Count == 0)
            return null;
        return await administrator.CheckCanConferAsync(actor, principalGrants.Select(stored => new GrantConferral(stored.Grant)), cancellationToken).ConfigureAwait(false) switch
        {
            AccessCheckPassed => null,
            TicketFailure => TicketFailure.ForbiddenWith(["The actor cannot delegate the access of the subscription principal."]),
        };
    }

    private TicketFailure? Invalid(TicketScope scope, EventSubscription subscription)
    {
        if (!sinks.TryGetValue(subscription.Sink.Kind, out var sink))
            return TicketFailure.Invalid("The sink kind is not registered.");
        return sink.IsValidTarget(scope, subscription.Sink.Target) ? null : TicketFailure.Invalid("The sink target is invalid for this workspace.");
    }

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, CancellationToken cancellationToken) =>
        (await access.AuthorizeAsync(new AccessRequest(actor, permission, EventPermissions.Workspace(actor.Scope)), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private async Task<TicketResult<EventSubscriptionView>> ViewAsync(StoreWriteResult<StoredEventSubscription, EventWriteRejection> result, CancellationToken cancellationToken) => result switch
    {
        StoreApplied<StoredEventSubscription> applied => new TicketValue<EventSubscriptionView>(await ViewAsync(applied.Value, head: null, cancellationToken).ConfigureAwait(false)),
        StoreNotFound => TicketFailure.NotFound,
        StoreConflict conflict => TicketFailure.Conflict(conflict.CurrentRevision),
        StoreRejected<EventWriteRejection> rejected => TicketFailure.Rejected(rejected.Reason.ToString()),
    };

    private async Task<EventSubscriptionView> ViewAsync(StoredEventSubscription stored, EventLogHead? head, CancellationToken cancellationToken)
    {
        var rowTask = store.GetWithStateAsync(stored.Scope, stored.Subscription.Id, cancellationToken);
        var headTask = head is null ? log.GetHeadAsync(stored.Scope, cancellationToken) : Task.FromResult(head);
        await Task.WhenAll(rowTask, headTask).ConfigureAwait(false);
        var row = rowTask.Result;
        return new EventSubscriptionView(row?.Subscription ?? stored, row?.State, headTask.Result);
    }
}

internal static class EventSinks
{
    public static IReadOnlyDictionary<string, IEventSink> Index(IEnumerable<IEventSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        var index = new Dictionary<string, IEventSink>(StringComparer.Ordinal);
        foreach (var sink in sinks)
        {
            ArgumentNullException.ThrowIfNull(sink, nameof(sinks));
            EventText.RequireToken(sink.Kind, EventSinkRef.MaximumKindLength, nameof(sinks));
            if (sink.MaximumBatchSize is < 1 or > EventSubscription.MaximumBatchSize)
                throw new ArgumentException("Sinks accept 1-100 events per call.", nameof(sinks));
            if (!index.TryAdd(sink.Kind, sink))
                throw new ArgumentException("Event sink kinds must be unique.", nameof(sinks));
        }
        return index;
    }
}
