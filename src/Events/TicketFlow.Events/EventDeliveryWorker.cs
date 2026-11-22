using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>
/// Counts from one delivery run. <see cref="Delivered"/> counts accepted sink calls (<see cref="EventsDelivered"/> their events),
/// <see cref="Advanced"/> claims that only skipped filtered or unauthorized events, <see cref="Lost"/> completions refused because the lease
/// or fence was lost and <see cref="Skipped"/> claims left alone because their lease was about to expire.
/// </summary>
public sealed record EventDeliveryRunResult(int Delivered, int EventsDelivered, int Advanced, int Retried, int DeadLettered, int Lost, int Skipped);

/// <summary>
/// The fan-out delivery engine. Each run claims due subscriptions (leased, fenced), reads the log after each cursor (at most
/// <see cref="EventsOptions.MaximumScan"/> events), keeps events that match the filter and that the subscription principal may read
/// (<c>events:read</c> on the workspace, then <c>conversation:read</c>/<c>contact:read</c> on the event resource, decided from one
/// <see cref="IAccessDecisionContext"/>; <see cref="EventPrivacy.Content"/> also requires <c>events:content</c>, audited once per batch),
/// renders them at the subscription's privacy level (hydrating content as the principal), calls the sink once and advances the cursor
/// only after the sink accepted the batch — at-least-once, in workspace sequence order (so per conversation in timeline order). Failures
/// back off exponentially; after <see cref="EventsOptions.MaximumAttempts"/> (or a permanent failure) the batch is dead-lettered and the
/// cursor advances. Sink exception details are not recorded. No hosted service is included: hosts call <see cref="RunOnceAsync"/>.
/// </summary>
public sealed class EventDeliveryWorker
{
    private readonly IEventSubscriptionStore subscriptions;
    private readonly IEventLog log;
    private readonly IAccessEvaluator access;
    private readonly IReadOnlyDictionary<string, IEventSink> sinks;
    private readonly EventContentHydrator hydrator;
    private readonly EventsOptions options;
    private readonly CloudEventRenderOptions render;
    private readonly TimeProvider time;

    public EventDeliveryWorker(IEventSubscriptionStore subscriptions, IEventLog log, IAccessEvaluator access, IEnumerable<IEventSink> sinks, EventContentHydrator hydrator,
        IOptions<EventsOptions> options, TimeProvider time)
    {
        this.subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.sinks = EventSinks.Index(sinks);
        this.hydrator = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        render = this.options.RenderOptions;
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<EventDeliveryRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        int delivered = 0, events = 0, advanced = 0, retried = 0, deadLettered = 0, lost = 0, skipped = 0;
        var claims = await subscriptions.ClaimAsync(time.GetUtcNow(), options.DeliveryClaimLimit, options.DeliveryLease, cancellationToken).ConfigureAwait(false);
        foreach (var claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claim.LeaseUntil <= time.GetUtcNow() + options.LeaseSafetyMargin)
            {
                skipped++;
                continue;
            }
            (Outcome Kind, int Events) outcome;
            try
            {
                outcome = await DeliverAsync(claim, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Store, Access or hydration failures: back off this subscription and keep serving the others. Details are not recorded.
                outcome = await FailAsync(claim, [], claim.State.Cursor, new EventSinkFailed("engine-error"), cancellationToken).ConfigureAwait(false);
            }
            switch (outcome.Kind)
            {
                case Outcome.Delivered:
                    delivered++;
                    events += outcome.Events;
                    break;
                case Outcome.Advanced:
                    advanced++;
                    break;
                case Outcome.Retried:
                    retried++;
                    break;
                case Outcome.DeadLettered:
                    deadLettered++;
                    break;
                case Outcome.Lost:
                    lost++;
                    break;
            }
        }
        return new EventDeliveryRunResult(delivered, events, advanced, retried, deadLettered, lost, skipped);
    }

    private enum Outcome
    {
        Delivered,
        Advanced,
        Retried,
        DeadLettered,
        Lost,
    }

    private async Task<(Outcome Kind, int Events)> DeliverAsync(EventDeliveryClaim claim, CancellationToken cancellationToken)
    {
        var stored = claim.Subscription;
        var subscription = stored.Subscription;
        var scope = stored.Scope;
        if (!sinks.TryGetValue(subscription.Sink.Kind, out var sink))
            return await FailAsync(claim, [], claim.State.Cursor, new EventSinkFailed("sink-missing"), cancellationToken).ConfigureAwait(false);

        var principal = stored.PrincipalRef;
        List<Permission> permissions = [EventPermissions.Read, AccessPermissions.ConversationRead, AccessPermissions.ContactRead];
        if (subscription.Privacy == EventPrivacy.Content)
            permissions.Add(EventPermissions.Content);
        var context = await access.LoadDecisionContextAsync(principal, scope, permissions, cancellationToken).ConfigureAwait(false);
        var workspace = EventPermissions.Workspace(scope);
        if (!(await context.DecideAsync(EventPermissions.Read, workspace, cancellationToken).ConfigureAwait(false)).IsPermitted)
            return await FailAsync(claim, [], claim.State.Cursor, new EventSinkFailed("principal-forbidden"), cancellationToken).ConfigureAwait(false);
        if (subscription.Privacy == EventPrivacy.Content && !(await context.DecideAsync(EventPermissions.Content, workspace, cancellationToken).ConfigureAwait(false)).IsPermitted)
            return await FailAsync(claim, [], claim.State.Cursor, new EventSinkFailed("content-forbidden"), cancellationToken).ConfigureAwait(false);

        var limit = Math.Min(subscription.BatchSize, sink.MaximumBatchSize);
        var page = await log.ReadAsync(scope, claim.State.Cursor, options.MaximumScan, cancellationToken).ConfigureAwait(false);
        var cursor = claim.State.Cursor;
        if (page.Count == 0)
        {
            // Everything after the cursor was pruned by retention: move to the head.
            var head = await log.GetHeadAsync(scope, cancellationToken).ConfigureAwait(false);
            return await CompleteAsync(claim, Math.Max(cursor, head.Last), null, Outcome.Advanced, 0, cancellationToken).ConfigureAwait(false);
        }
        var selected = new List<StoredIntegrationEvent>();
        foreach (var item in page)
        {
            cursor = item.Sequence;
            if (!subscription.Filter.Matches(item))
                continue;
            var (permission, resource) = item.Resource.AccessCheck(scope);
            if (permission != EventPermissions.Read && !(await context.DecideAsync(permission, resource, cancellationToken).ConfigureAwait(false)).IsPermitted)
                continue;
            selected.Add(item);
            if (selected.Count == limit)
                break;
        }
        if (selected.Count == 0)
            return await CompleteAsync(claim, cursor, null, Outcome.Advanced, 0, cancellationToken).ConfigureAwait(false);

        var rendered = new List<RenderedEvent>(selected.Count);
        foreach (var item in selected)
        {
            var content = subscription.Privacy == EventPrivacy.Content ? await hydrator.HydrateAsync(item, principal, cancellationToken).ConfigureAwait(false) : null;
            rendered.Add(new RenderedEvent(item, IntegrationEventCodec.Render(item, subscription.Privacy, content, render)));
        }
        EventSinkResult result;
        try
        {
            result = await sink.DeliverAsync(new EventDelivery(stored, rendered, claim.State.Attempts + 1), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Sink exception details may contain customer data; only a code is recorded.
            result = new EventSinkFailed("sink-error");
        }
        return result switch
        {
            EventSinkDelivered => await CompleteAsync(claim, cursor, time.GetUtcNow(), Outcome.Delivered, selected.Count, cancellationToken).ConfigureAwait(false),
            EventSinkFailed failed => await FailAsync(claim, selected, cursor, failed, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<(Outcome, int)> CompleteAsync(EventDeliveryClaim claim, long cursor, DateTimeOffset? deliveredAt, Outcome outcome, int events, CancellationToken cancellationToken) =>
        await subscriptions.CompleteAsync(claim.Subscription.Scope, claim.Subscription.Subscription.Id, claim.Fence, cursor, deliveredAt, cancellationToken).ConfigureAwait(false)
            ? (outcome, events)
            : (Outcome.Lost, 0);

    private async Task<(Outcome, int)> FailAsync(EventDeliveryClaim claim, IReadOnlyList<StoredIntegrationEvent> batch, long cursor, EventSinkFailed failure, CancellationToken cancellationToken)
    {
        var scope = claim.Subscription.Scope;
        var id = claim.Subscription.Subscription.Id;
        var attempts = claim.State.Attempts + 1;
        var now = time.GetUtcNow();
        if (batch.Count > 0 && (failure.Permanent || attempts >= options.MaximumAttempts))
        {
            var letters = batch.Select(item => new EventDeadLetter(id, item.Sequence, item.Event.Id, item.Event.Type, attempts, failure.Code, now)).ToList();
            return await subscriptions.DeadLetterAsync(scope, id, claim.Fence, cursor, letters, cancellationToken).ConfigureAwait(false) ? (Outcome.DeadLettered, 0) : (Outcome.Lost, 0);
        }
        var delay = Backoff(attempts);
        if (failure.RetryAfter is { } requested && requested > delay)
            delay = requested;
        return await subscriptions.FailAsync(scope, id, claim.Fence, attempts, now + delay, failure.Code, cancellationToken).ConfigureAwait(false) ? (Outcome.Retried, 0) : (Outcome.Lost, 0);
    }

    private TimeSpan Backoff(int attempt)
    {
        var delay = options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30));
        return delay >= options.MaximumRetryDelay.TotalMilliseconds ? options.MaximumRetryDelay : TimeSpan.FromMilliseconds(delay);
    }
}

/// <summary>
/// The built-in <c>in-process</c> sink: delivers to the registered <see cref="IEventSubscriber"/> named by the subscription target. A
/// throwing subscriber fails the delivery (<c>subscriber-error</c>); a missing one is <c>subscriber-missing</c>.
/// </summary>
public sealed class InProcessEventSink : IEventSink
{
    public const string SinkKind = "in-process";

    private readonly IReadOnlyDictionary<string, IEventSubscriber> subscribers;

    public InProcessEventSink(IEnumerable<IEventSubscriber> subscribers)
    {
        ArgumentNullException.ThrowIfNull(subscribers);
        var index = new Dictionary<string, IEventSubscriber>(StringComparer.Ordinal);
        foreach (var subscriber in subscribers)
            if (!index.TryAdd(EventText.RequireToken(subscriber.Name, 64, nameof(subscribers)), subscriber))
                throw new ArgumentException("Subscriber names must be unique.", nameof(subscribers));
        this.subscribers = index;
    }

    public string Kind => SinkKind;

    public int MaximumBatchSize => EventSubscription.MaximumBatchSize;

    public bool IsValidTarget(string target) => target is not null && subscribers.ContainsKey(target);

    public async Task<EventSinkResult> DeliverAsync(EventDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!subscribers.TryGetValue(delivery.Subscription.Subscription.Sink.Target, out var subscriber))
            return new EventSinkFailed("subscriber-missing");
        try
        {
            await subscriber.HandleAsync(delivery, cancellationToken).ConfigureAwait(false);
            return EventSinkDelivered.Instance;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new EventSinkFailed("subscriber-error");
        }
    }
}

/// <summary>Counts from one retention run.</summary>
public sealed record EventRetentionResult(int EventsPruned, int DeadLettersPruned);

/// <summary>
/// Applies <see cref="EventsOptions.Retention"/> and <see cref="EventsOptions.DeadLetterRetention"/> store-wide in bounded batches. Hosts call
/// <see cref="RunOnceAsync"/> in their loop (a run with full batches means more is due).
/// </summary>
public sealed class EventRetentionWorker
{
    private readonly IEventLog log;
    private readonly IEventSubscriptionStore subscriptions;
    private readonly EventsOptions options;
    private readonly TimeProvider time;

    public EventRetentionWorker(IEventLog log, IEventSubscriptionStore subscriptions, IOptions<EventsOptions> options, TimeProvider time)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<EventRetentionResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var events = await PruneAsync(log.PruneAsync, now - options.Retention, cancellationToken).ConfigureAwait(false);
        var letters = await PruneAsync(subscriptions.PruneDeadLettersAsync, now - options.DeadLetterRetention, cancellationToken).ConfigureAwait(false);
        return new EventRetentionResult(events, letters);
    }

    private async Task<int> PruneAsync(Func<DateTimeOffset, int, CancellationToken, Task<int>> prune, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < options.MaximumPruneBatches; batch++)
        {
            var deleted = await prune(olderThan, options.PruneBatchSize, cancellationToken).ConfigureAwait(false);
            total += deleted;
            if (deleted < options.PruneBatchSize)
                break;
        }
        return total;
    }
}
