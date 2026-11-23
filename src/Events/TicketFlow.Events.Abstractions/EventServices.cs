using System.Buffers;
using System.Globalization;
using System.Text;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>An event rendered for one subscription: the stored event (for routing in trusted code) and its CloudEvents JSON at the subscription's privacy level.</summary>
public sealed record RenderedEvent(StoredIntegrationEvent Event, string Json)
{
    public override string ToString() => $"RenderedEvent {{ Sequence = {Event.Sequence}, Type = {Event.Event.Type} }}";
}

/// <summary>
/// One sink call: up to the subscription's batch size of events in workspace sequence order. <see cref="DeliveryId"/> is stable across
/// retries of the same batch (scope, subscription, subscription revision, replay <see cref="Generation"/>, first and last sequence), so
/// receivers can deduplicate retries while a replay or a changed subscription produces new delivery ids.
/// </summary>
public sealed record EventDelivery(StoredEventSubscription Subscription, IReadOnlyList<RenderedEvent> Events, int Attempt, long Generation = 0)
{
    public StoredEventSubscription Subscription { get; } = Subscription ?? throw new ArgumentNullException(nameof(Subscription));

    public IReadOnlyList<RenderedEvent> Events { get; } = Events is { Count: > 0 } ? Events : throw new ArgumentException("A delivery carries at least one event.", nameof(Events));

    public TicketScope Scope => Subscription.Scope;

    public string DeliveryId
    {
        get
        {
            var scope = Subscription.Scope;
            var subId = Subscription.Subscription.Id.Value;
            var maxBytes = 13 + Encoding.UTF8.GetMaxByteCount(scope.TenantId.Length + scope.ProjectId.Length + scope.EnvironmentId.Length + scope.AppId.Length + scope.WorkspaceId.Value.Length + subId.Length) + 120;
            byte[]? rented = null;
            Span<byte> buf = maxBytes <= 512 ? stackalloc byte[512] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
            try
            {
                var pos = 0;
                static void WritePart(Span<byte> dst, ref int p, string value)
                {
                    p += Encoding.UTF8.GetBytes(value, dst[p..]);
                    dst[p++] = (byte)'\n';
                }
                static void WriteLong(Span<byte> dst, ref int p, long value)
                {
                    value.TryFormat(dst[p..], out var written, default, CultureInfo.InvariantCulture);
                    p += written;
                    dst[p++] = (byte)'\n';
                }
                "tf-events-v1\n"u8.CopyTo(buf);
                pos += 13;
                WritePart(buf, ref pos, scope.TenantId);
                WritePart(buf, ref pos, scope.ProjectId);
                WritePart(buf, ref pos, scope.EnvironmentId);
                WritePart(buf, ref pos, scope.AppId);
                WritePart(buf, ref pos, scope.WorkspaceId.Value);
                WritePart(buf, ref pos, subId);
                WriteLong(buf, ref pos, Subscription.Revision);
                WriteLong(buf, ref pos, Generation);
                WriteLong(buf, ref pos, Events[0].Event.Sequence);
                WriteLong(buf, ref pos, Events[^1].Event.Sequence);
                "delivery"u8.CopyTo(buf[pos..]);
                pos += 8;
                var guid = EventText.HashToGuid(buf[..pos]);
                return string.Create(36, guid, static (chars, id) =>
                {
                    "evt_".CopyTo(chars);
                    id.TryFormat(chars[4..], out _, "N");
                });
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
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

/// <summary>
/// The batch is too large for the sink (for example over a request body limit). The engine retries immediately with the first half; a
/// single event that is still too large is dead-lettered (<c>payload-too-large</c>).
/// </summary>
public sealed record EventSinkSplit
{
    private EventSinkSplit()
    {
    }

    public static EventSinkSplit Instance { get; } = new();
}

/// <summary>Outcome of a sink call. Switch over every case without a discard arm.</summary>
public union EventSinkResult(EventSinkDelivered, EventSinkFailed, EventSinkSplit);

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

    /// <summary>True when <paramref name="target"/> is valid for subscriptions of <paramref name="scope"/> (named sinks apply their scope allow-lists).</summary>
    bool IsValidTarget(TicketScope scope, string target);

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

/// <summary>A subscription with its delivery state, as shown to managers.</summary>
public sealed record EventSubscriptionView(StoredEventSubscription Subscription, EventDeliveryState? Delivery, EventLogHead Head);

/// <summary>
/// Access-checked subscription management on the actor's workspace. Reads need <c>events:read</c>; writes, replay and dead letters need
/// <c>events:manage</c> (sensitive); configuring a <see cref="EventPrivacy.Content"/> subscription also needs <c>events:content</c>
/// (sensitive). Existence and revision fences are checked before authorization decisions on stored records.
/// </summary>
public interface IEventSubscriptionService
{
    Task<TicketResult<EventSubscriptionView>> CreateAsync(PrincipalRef actor, EventSubscription subscription, EventSubscriptionStart? start = null, CancellationToken cancellationToken = default);

    Task<TicketResult<EventSubscriptionView>> UpdateAsync(PrincipalRef actor, EventSubscription subscription, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<EventSubscriptionId>> DeleteAsync(PrincipalRef actor, EventSubscriptionId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<EventSubscriptionView>> GetAsync(PrincipalRef actor, EventSubscriptionId id, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<EventSubscriptionView>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    /// <summary>Redelivers events after <paramref name="afterSequence"/> (clears dead letters from there on).</summary>
    Task<TicketResult<EventSubscriptionView>> ReplayAsync(PrincipalRef actor, EventSubscriptionId id, long afterSequence, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<EventDeadLetter>>> ListDeadLettersAsync(PrincipalRef actor, EventSubscriptionId id, long afterSequence = 0, int limit = 50, CancellationToken cancellationToken = default);
}
