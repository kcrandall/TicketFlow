using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>
/// The default <see cref="IEventSubscriptionService"/>: authorizes on the actor's workspace through <see cref="IAccessEvaluator"/>, validates
/// sink kinds and targets against the registered <see cref="IEventSink"/>s and writes through <see cref="IEventSubscriptionStore"/>.
/// </summary>
public sealed class EventSubscriptionService : IEventSubscriptionService
{
    private readonly IEventSubscriptionStore store;
    private readonly IEventLog log;
    private readonly IAccessEvaluator access;
    private readonly IReadOnlyDictionary<string, IEventSink> sinks;
    private readonly TimeProvider time;

    public EventSubscriptionService(IEventSubscriptionStore store, IEventLog log, IAccessEvaluator access, IEnumerable<IEventSink> sinks, TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.sinks = EventSinks.Index(sinks);
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<EventResult<EventSubscriptionView>> CreateAsync(PrincipalRef actor, EventSubscription subscription, EventSubscriptionStart? start = null,
        CancellationToken cancellationToken = default)
    {
        var scope = actor.Scope;
        ArgumentNullException.ThrowIfNull(subscription);
        if (await RefuseAsync(actor, subscription.Privacy, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (Invalid(subscription) is { } invalid)
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

    public async Task<EventResult<EventSubscriptionView>> UpdateAsync(PrincipalRef actor, EventSubscription subscription, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = actor.Scope;
        ArgumentNullException.ThrowIfNull(subscription);
        var current = await store.GetAsync(scope, subscription.Id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return EventNotFound.Instance;
        if (current.Revision != expectedRevision)
            return new EventConflict(current.Revision);
        if (await RefuseAsync(actor, subscription.Privacy, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (Invalid(subscription) is { } invalid)
            return invalid;
        return await ViewAsync(await store.UpdateAsync(scope, subscription, expectedRevision, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<EventResult<EventSubscriptionId>> DeleteAsync(PrincipalRef actor, EventSubscriptionId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = actor.Scope;
        var current = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return EventNotFound.Instance;
        if (current.Revision != expectedRevision)
            return new EventConflict(current.Revision);
        if (!await PermittedAsync(actor, EventPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return new EventRefused(EventRefusal.Forbidden);
        return await store.DeleteAsync(scope, id, expectedRevision, cancellationToken).ConfigureAwait(false) switch
        {
            EventWriteApplied<StoredEventSubscription> => new EventSucceeded<EventSubscriptionId>(id),
            EventWriteNotFound => EventNotFound.Instance,
            EventWriteConflict conflict => new EventConflict(conflict.CurrentRevision),
            EventWriteRejected rejected => new EventRefused(EventRefusal.Rejected, rejected.Reason.ToString()),
        };
    }

    public async Task<EventResult<EventSubscriptionView>> GetAsync(PrincipalRef actor, EventSubscriptionId id, CancellationToken cancellationToken = default)
    {
        var current = await store.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return EventNotFound.Instance;
        if (!await PermittedAsync(actor, EventPermissions.Read, cancellationToken).ConfigureAwait(false))
            return new EventRefused(EventRefusal.Forbidden);
        return new EventSucceeded<EventSubscriptionView>(await ViewAsync(current, cancellationToken).ConfigureAwait(false));
    }

    public async Task<EventResult<IReadOnlyList<EventSubscriptionView>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        if (!await PermittedAsync(actor, EventPermissions.Read, cancellationToken).ConfigureAwait(false))
            return new EventRefused(EventRefusal.Forbidden);
        var views = new List<EventSubscriptionView>();
        foreach (var stored in await store.ListAsync(actor.Scope, cancellationToken).ConfigureAwait(false))
            views.Add(await ViewAsync(stored, cancellationToken).ConfigureAwait(false));
        return new EventSucceeded<IReadOnlyList<EventSubscriptionView>>(views);
    }

    public async Task<EventResult<EventSubscriptionView>> ReplayAsync(PrincipalRef actor, EventSubscriptionId id, long afterSequence, CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0)
            return new EventRefused(EventRefusal.Invalid, "The replay position cannot be negative.");
        var current = await store.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return EventNotFound.Instance;
        if (!await PermittedAsync(actor, EventPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return new EventRefused(EventRefusal.Forbidden);
        var head = await log.GetHeadAsync(actor.Scope, cancellationToken).ConfigureAwait(false);
        if (!await store.ReplayAsync(actor.Scope, id, Math.Min(afterSequence, head.Last), time.GetUtcNow(), cancellationToken).ConfigureAwait(false))
            return EventNotFound.Instance;
        return new EventSucceeded<EventSubscriptionView>(await ViewAsync(current, cancellationToken).ConfigureAwait(false));
    }

    public async Task<EventResult<IReadOnlyList<EventDeadLetter>>> ListDeadLettersAsync(PrincipalRef actor, EventSubscriptionId id, long afterSequence = 0, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0 || limit is < 1 or > IEventSubscriptionStore.MaximumDeadLetterRead)
            return new EventRefused(EventRefusal.Invalid, "Dead letters are read after a non-negative sequence, 1-200 at a time.");
        var current = await store.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
            return EventNotFound.Instance;
        if (!await PermittedAsync(actor, EventPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return new EventRefused(EventRefusal.Forbidden);
        return new EventSucceeded<IReadOnlyList<EventDeadLetter>>(await store.ListDeadLettersAsync(actor.Scope, id, afterSequence, limit, cancellationToken).ConfigureAwait(false));
    }

    private async Task<EventRefused?> RefuseAsync(PrincipalRef actor, EventPrivacy privacy, CancellationToken cancellationToken)
    {
        if (!await PermittedAsync(actor, EventPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return new EventRefused(EventRefusal.Forbidden);
        if (privacy == EventPrivacy.Content && !await PermittedAsync(actor, EventPermissions.Content, cancellationToken).ConfigureAwait(false))
            return new EventRefused(EventRefusal.Forbidden, "Content subscriptions require events:content.");
        return null;
    }

    private EventRefused? Invalid(EventSubscription subscription)
    {
        if (!sinks.TryGetValue(subscription.Sink.Kind, out var sink))
            return new EventRefused(EventRefusal.Invalid, "The sink kind is not registered.");
        return sink.IsValidTarget(subscription.Sink.Target) ? null : new EventRefused(EventRefusal.Invalid, "The sink target is invalid.");
    }

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, CancellationToken cancellationToken) =>
        (await access.AuthorizeAsync(new AccessRequest(actor, permission, EventPermissions.Workspace(actor.Scope)), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private async Task<EventResult<EventSubscriptionView>> ViewAsync(EventWriteResult<StoredEventSubscription> result, CancellationToken cancellationToken) => result switch
    {
        EventWriteApplied<StoredEventSubscription> applied => new EventSucceeded<EventSubscriptionView>(await ViewAsync(applied.Value, cancellationToken).ConfigureAwait(false)),
        EventWriteNotFound => EventNotFound.Instance,
        EventWriteConflict conflict => new EventConflict(conflict.CurrentRevision),
        EventWriteRejected rejected => new EventRefused(EventRefusal.Rejected, rejected.Reason.ToString()),
    };

    private async Task<EventSubscriptionView> ViewAsync(StoredEventSubscription stored, CancellationToken cancellationToken)
    {
        var fresh = await store.GetAsync(stored.Scope, stored.Subscription.Id, cancellationToken).ConfigureAwait(false) ?? stored;
        return new EventSubscriptionView(fresh, await store.GetStateAsync(stored.Scope, stored.Subscription.Id, cancellationToken).ConfigureAwait(false),
            await log.GetHeadAsync(stored.Scope, cancellationToken).ConfigureAwait(false));
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
