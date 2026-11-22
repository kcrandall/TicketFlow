using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>An event rendered for one subscription: the stored event (for routing in trusted code) and its CloudEvents JSON at the subscription's privacy level.</summary>
public sealed record RenderedEvent(StoredIntegrationEvent Event, string Json)
{
    public override string ToString() => $"RenderedEvent {{ Sequence = {Event.Sequence}, Type = {Event.Event.Type} }}";
}

/// <summary>
/// One sink call: up to the subscription's batch size of events in workspace sequence order. <see cref="DeliveryId"/> is stable across
/// retries of the same batch (subscription, first and last sequence), so receivers can deduplicate.
/// </summary>
public sealed record EventDelivery(StoredEventSubscription Subscription, IReadOnlyList<RenderedEvent> Events, int Attempt)
{
    public StoredEventSubscription Subscription { get; } = Subscription ?? throw new ArgumentNullException(nameof(Subscription));

    public IReadOnlyList<RenderedEvent> Events { get; } = Events is { Count: > 0 } ? Events : throw new ArgumentException("A delivery carries at least one event.", nameof(Events));

    public TicketScope Scope => Subscription.Scope;

    public string DeliveryId
    {
        get
        {
            var scope = Subscription.Scope;
            var source = string.Join('\n', scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value, Subscription.Subscription.Id.Value,
                Events[0].Event.Sequence, Events[^1].Event.Sequence);
            return "evt_" + EventText.EventId(source, "delivery").ToString("N");
        }
    }
}

/// <summary>The sink accepted every event of the delivery durably (or as durably as it declares).</summary>
public sealed record EventSinkDelivered
{
    private EventSinkDelivered()
    {
    }

    public static EventSinkDelivered Instance { get; } = new();
}

/// <summary>
/// The sink did not accept the delivery. <see cref="Code"/> is a short token (never payload content). <see cref="Permanent"/> failures
/// are dead-lettered without retrying; <see cref="RetryAfter"/> asks for a later retry than the backoff.
/// </summary>
public sealed record EventSinkFailed
{
    public EventSinkFailed(string code, bool permanent = false, TimeSpan? retryAfter = null)
    {
        Code = IEventSubscriptionStore.RequireError(code);
        Permanent = permanent;
        RetryAfter = retryAfter is { } delay && delay > TimeSpan.Zero ? delay : null;
    }

    public string Code { get; }
    public bool Permanent { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Outcome of a sink call. Switch over every case without a discard arm.</summary>
public union EventSinkResult(EventSinkDelivered, EventSinkFailed);

/// <summary>
/// A delivery destination kind. Registered sinks are selected by <see cref="Kind"/>; <see cref="IsValidTarget"/> validates subscription
/// targets when they are created. Sinks must not log payloads.
/// </summary>
public interface IEventSink
{
    /// <summary>The sink kind token (unique per registration).</summary>
    string Kind { get; }

    /// <summary>Events per call this sink accepts (1-100); the engine uses the smaller of this and the subscription batch size.</summary>
    int MaximumBatchSize { get; }

    bool IsValidTarget(string target);

    Task<EventSinkResult> DeliverAsync(EventDelivery delivery, CancellationToken cancellationToken = default);
}

/// <summary>
/// A host-application subscriber for the built-in <c>in-process</c> sink (subscription target = <see cref="Name"/>). Throwing fails the
/// delivery (retried with backoff, then dead-lettered). Deliveries are at least once: handlers must be idempotent by event id.
/// </summary>
public interface IEventSubscriber
{
    /// <summary>A unique token naming the subscriber (1-64 characters of a-z, 0-9, '-', '_' or '.').</summary>
    string Name { get; }

    Task HandleAsync(EventDelivery delivery, CancellationToken cancellationToken = default);
}

/// <summary>
/// Appends integration events produced outside the built-in projections (for example an identity-linking feature publishing
/// <see cref="ContactIdentityLinkedData"/>). Trusted: callers own authorization. Appends are idempotent by event id.
/// </summary>
public interface IEventPublisher
{
    Task<EventAppendResult> PublishAsync(TicketScope scope, IReadOnlyList<IntegrationEventDraft> drafts, CancellationToken cancellationToken = default);
}

public enum EventRefusal
{
    /// <summary>The actor lacks the required permission.</summary>
    Forbidden = 1,

    /// <summary>The request is invalid (see errors).</summary>
    Invalid = 2,

    /// <summary>The workspace subscription limit was reached or the identifier exists.</summary>
    Rejected = 3,
}

public sealed record EventSucceeded<T>(T Value);

public sealed record EventNotFound
{
    private EventNotFound()
    {
    }

    public static EventNotFound Instance { get; } = new();
}

public sealed record EventConflict(long CurrentRevision);

public sealed record EventRefused(EventRefusal Reason, IReadOnlyList<string> Errors)
{
    public EventRefused(EventRefusal reason, params string[] errors)
        : this(reason, (IReadOnlyList<string>)errors)
    {
    }
}

/// <summary>Outcome of a subscription command or read. Switch over every case without a discard arm.</summary>
public union EventResult<T>(EventSucceeded<T>, EventNotFound, EventConflict, EventRefused);

/// <summary>A subscription with its delivery state, as shown to managers.</summary>
public sealed record EventSubscriptionView(StoredEventSubscription Subscription, EventDeliveryState? Delivery, EventLogHead Head);

/// <summary>
/// Access-checked subscription management on the actor's workspace. Reads need <c>events:read</c>; writes, replay and dead letters need
/// <c>events:manage</c> (sensitive); configuring a <see cref="EventPrivacy.Content"/> subscription also needs <c>events:content</c>
/// (sensitive). Existence and revision fences are checked before authorization decisions on stored records.
/// </summary>
public interface IEventSubscriptionService
{
    Task<EventResult<EventSubscriptionView>> CreateAsync(PrincipalRef actor, EventSubscription subscription, EventSubscriptionStart? start = null, CancellationToken cancellationToken = default);

    Task<EventResult<EventSubscriptionView>> UpdateAsync(PrincipalRef actor, EventSubscription subscription, long expectedRevision, CancellationToken cancellationToken = default);

    Task<EventResult<EventSubscriptionId>> DeleteAsync(PrincipalRef actor, EventSubscriptionId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<EventResult<EventSubscriptionView>> GetAsync(PrincipalRef actor, EventSubscriptionId id, CancellationToken cancellationToken = default);

    Task<EventResult<IReadOnlyList<EventSubscriptionView>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    /// <summary>Redelivers events after <paramref name="afterSequence"/> (clears dead letters from there on).</summary>
    Task<EventResult<EventSubscriptionView>> ReplayAsync(PrincipalRef actor, EventSubscriptionId id, long afterSequence, CancellationToken cancellationToken = default);

    Task<EventResult<IReadOnlyList<EventDeadLetter>>> ListDeadLettersAsync(PrincipalRef actor, EventSubscriptionId id, long afterSequence = 0, int limit = 50, CancellationToken cancellationToken = default);
}
