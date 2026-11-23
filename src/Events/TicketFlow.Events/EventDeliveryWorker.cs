using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>
/// Counts from one delivery run. <see cref="Delivered"/> counts accepted sink calls (<see cref="EventsDelivered"/> their events),
/// <see cref="Advanced"/> claims that only skipped filtered or unauthorized events, <see cref="Suspended"/> subscriptions suspended after
/// repeated configuration failures, <see cref="Lost"/> completions refused because the lease or fence was lost and <see cref="Skipped"/>
/// claims left alone because their lease was about to expire.
/// </summary>
public sealed record EventDeliveryRunResult(int Delivered, int EventsDelivered, int Advanced, int Retried, int DeadLettered, int Lost, int Skipped, int Suspended = 0);

/// <summary>
/// The fan-out delivery engine. Each run claims due subscriptions (leased, fenced, ordered by claim time), reads the log after each cursor
/// (at most <see cref="EventsOptions.MaximumScan"/> events), keeps events that match the filter and that the subscription principal may
/// read (<c>events:read</c> on the workspace, then <c>conversation:read</c>/<c>contact:read</c> on the event resource, decided from one
/// <see cref="IAccessDecisionContext"/>; <see cref="EventPrivacy.Content"/> also requires <c>events:content</c>, audited once per batch),
/// renders them at the subscription's privacy level (content hydrated once per batch as the principal), calls the sink and advances the
/// cursor only after the sink accepted the batch — at-least-once, in workspace sequence order (so per conversation in timeline order).
/// <list type="bullet">
/// <item>Fairness: a completed subscription with more pending events is claimable again immediately but behind others (claims are ordered by
/// claim time); a caught-up one is pushed out by <see cref="EventsOptions.IdlePollInterval"/>.</item>
/// <item>Failures back off exponentially. After <see cref="EventsOptions.MaximumAttempts"/> (or a permanent failure) the failing batch is
/// dead-lettered and the cursor advances; an event whose content cannot be hydrated or rendered fails alone (events before it are delivered
/// first), so a poison event is dead-lettered by itself.</item>
/// <item>Configuration failures (sink not registered or not allowed, principal without <c>events:read</c> or <c>events:content</c>) suspend
/// the subscription after <see cref="EventsOptions.MaximumAttempts"/>; updating or replaying it resumes delivery. Infrastructure failures
/// (store or Access errors) keep retrying with backoff.</item>
/// <item><see cref="EventSinkSplit"/> halves the batch immediately; a single event that is still too large is dead-lettered.</item>
/// <item>Events pruned by retention before delivery are recorded as <c>events-pruned-&lt;from&gt;-&lt;to&gt;</c> in the delivery state.</item>
/// </list>
/// Sink exception details are not recorded. No hosted service is included: hosts call <see cref="RunOnceAsync"/>.
/// </summary>
public sealed class EventDeliveryWorker
{
    private const string EngineError = "engine-error";
    private const int DeliveryConcurrency = 8;

    private static readonly Permission[] BasePermissions = [EventPermissions.Read, AccessPermissions.ConversationRead, AccessPermissions.ContactRead];
    private static readonly Permission[] ContentPermissions = [EventPermissions.Read, AccessPermissions.ConversationRead, AccessPermissions.ContactRead, EventPermissions.Content];

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
        var counts = new int[6];
        int events = 0, skipped = 0;
        var claims = await subscriptions.ClaimAsync(time.GetUtcNow(), options.DeliveryClaimLimit, options.DeliveryLease, cancellationToken).ConfigureAwait(false);
        if (claims.Count == 0)
            return new EventDeliveryRunResult(0, 0, 0, 0, 0, 0, 0, 0);
        if (claims.Count == 1)
        {
            var claim = claims[0];
            cancellationToken.ThrowIfCancellationRequested();
            if (claim.LeaseUntil <= time.GetUtcNow() + options.LeaseSafetyMargin)
                skipped++;
            else
            {
                var outcome = await ExecuteClaimAsync(claim, logCache: null, renderCache: null, cancellationToken).ConfigureAwait(false);
                counts[(int)outcome.Kind]++;
                events += outcome.Events;
            }
            return new EventDeliveryRunResult(counts[(int)Outcome.Delivered], events, counts[(int)Outcome.Advanced], counts[(int)Outcome.Retried], counts[(int)Outcome.DeadLettered],
                counts[(int)Outcome.Lost], skipped, counts[(int)Outcome.Suspended]);
        }

        // Share log reads and non-content CloudEvent renders across claims in the same run, with bounded delivery concurrency.
        var logCache = new ConcurrentDictionary<(TicketScope Scope, long Cursor), Lazy<Task<IReadOnlyList<StoredIntegrationEvent>>>>();
        var renderCache = new ConcurrentDictionary<(TicketScope Scope, long Sequence, EventPrivacy Privacy), string>();
        using var gate = new SemaphoreSlim(Math.Min(DeliveryConcurrency, claims.Count));
        var tasks = new Task<(Outcome Kind, int Events, bool Skipped)>[claims.Count];
        for (var i = 0; i < claims.Count; i++)
        {
            var claim = claims[i];
            tasks[i] = RunClaimBoundedAsync(claim, gate, logCache, renderCache, cancellationToken);
        }
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            if (r.Skipped)
                skipped++;
            else
            {
                counts[(int)r.Kind]++;
                events += r.Events;
            }
        }
        return new EventDeliveryRunResult(counts[(int)Outcome.Delivered], events, counts[(int)Outcome.Advanced], counts[(int)Outcome.Retried], counts[(int)Outcome.DeadLettered],
            counts[(int)Outcome.Lost], skipped, counts[(int)Outcome.Suspended]);
    }

    private async Task<(Outcome Kind, int Events, bool Skipped)> RunClaimBoundedAsync(EventDeliveryClaim claim, SemaphoreSlim gate,
        ConcurrentDictionary<(TicketScope Scope, long Cursor), Lazy<Task<IReadOnlyList<StoredIntegrationEvent>>>> logCache,
        ConcurrentDictionary<(TicketScope Scope, long Sequence, EventPrivacy Privacy), string> renderCache, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (claim.LeaseUntil <= time.GetUtcNow() + options.LeaseSafetyMargin)
                return (default, 0, true);
            var outcome = await ExecuteClaimAsync(claim, logCache, renderCache, cancellationToken).ConfigureAwait(false);
            return (outcome.Kind, outcome.Events, false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(Outcome Kind, int Events)> ExecuteClaimAsync(EventDeliveryClaim claim,
        ConcurrentDictionary<(TicketScope Scope, long Cursor), Lazy<Task<IReadOnlyList<StoredIntegrationEvent>>>>? logCache,
        ConcurrentDictionary<(TicketScope Scope, long Sequence, EventPrivacy Privacy), string>? renderCache, CancellationToken cancellationToken)
    {
        try
        {
            return await DeliverAsync(claim, logCache, renderCache, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Store or Access failures: back off this subscription and keep serving the others. Details are not recorded.
            return await FailAsync(claim, [], claim.State.Cursor, new EventSinkFailed(EngineError), cancellationToken).ConfigureAwait(false);
        }
    }

    private enum Outcome
    {
        Delivered,
        Advanced,
        Retried,
        DeadLettered,
        Lost,
        Suspended,
    }

    private async Task<(Outcome Kind, int Events)> DeliverAsync(EventDeliveryClaim claim,
        ConcurrentDictionary<(TicketScope Scope, long Cursor), Lazy<Task<IReadOnlyList<StoredIntegrationEvent>>>>? logCache,
        ConcurrentDictionary<(TicketScope Scope, long Sequence, EventPrivacy Privacy), string>? renderCache, CancellationToken cancellationToken)
    {
        var stored = claim.Subscription;
        var subscription = stored.Subscription;
        var scope = stored.Scope;
        if (!sinks.TryGetValue(subscription.Sink.Kind, out var sink) || !sink.IsValidTarget(scope, subscription.Sink.Target))
            return await FailAsync(claim, [], claim.State.Cursor, new EventSinkFailed("sink-missing"), cancellationToken).ConfigureAwait(false);

        var principal = stored.PrincipalRef;
        var permissions = subscription.Privacy == EventPrivacy.Content ? ContentPermissions : BasePermissions;
        var context = await access.LoadDecisionContextAsync(principal, scope, permissions, cancellationToken).ConfigureAwait(false);
        var workspace = EventPermissions.Workspace(scope);
        if (!(await context.DecideAsync(EventPermissions.Read, workspace, cancellationToken).ConfigureAwait(false)).IsPermitted)
            return await FailAsync(claim, [], claim.State.Cursor, new EventSinkFailed("principal-forbidden"), cancellationToken).ConfigureAwait(false);
        if (subscription.Privacy == EventPrivacy.Content && !(await context.DecideAsync(EventPermissions.Content, workspace, cancellationToken).ConfigureAwait(false)).IsPermitted)
            return await FailAsync(claim, [], claim.State.Cursor, new EventSinkFailed("content-forbidden"), cancellationToken).ConfigureAwait(false);

        var head = claim.HeadSequence ?? (await log.GetHeadAsync(scope, cancellationToken).ConfigureAwait(false)).Last;
        var limit = Math.Min(subscription.BatchSize, sink.MaximumBatchSize);
        var start = claim.State.Cursor;
        IReadOnlyList<StoredIntegrationEvent> page;
        if (logCache is not null)
        {
            var lazy = logCache.GetOrAdd((scope, start), key => new Lazy<Task<IReadOnlyList<StoredIntegrationEvent>>>(() => log.ReadAsync(key.Scope, key.Cursor, options.MaximumScan, cancellationToken)));
            page = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
            page = await log.ReadAsync(scope, start, options.MaximumScan, cancellationToken).ConfigureAwait(false);
        if (page.Count == 0)
            // Everything after the cursor was pruned by retention: record the gap and move to the head.
            return await CompleteAsync(claim, Math.Max(start, head), null, head, head > start ? Gap(start + 1, head) : null, Outcome.Advanced, 0, cancellationToken).ConfigureAwait(false);
        var gap = page[0].Sequence > start + 1 ? Gap(start + 1, page[0].Sequence - 1) : null;
        var cursor = start;
        var selected = new List<StoredIntegrationEvent>(Math.Min(limit, page.Count));
        Dictionary<(Permission Permission, AccessResource Resource), bool>? decisionMemo = null;
        for (var i = 0; i < page.Count; i++)
        {
            var item = page[i];
            cursor = item.Sequence;
            if (!subscription.Filter.Matches(item))
                continue;
            var (permission, resource) = item.Resource.AccessCheck(scope);
            if (permission != EventPermissions.Read)
            {
                decisionMemo ??= [];
                var key = (permission, resource);
                if (!decisionMemo.TryGetValue(key, out var permitted))
                {
                    permitted = (await context.DecideAsync(permission, resource, cancellationToken).ConfigureAwait(false)).IsPermitted;
                    decisionMemo[key] = permitted;
                }
                if (!permitted)
                    continue;
            }
            selected.Add(item);
            if (selected.Count == limit)
                break;
        }
        if (selected.Count == 0)
            return await CompleteAsync(claim, cursor, null, head, gap, Outcome.Advanced, 0, cancellationToken).ConfigureAwait(false);

        var (rendered, poison) = await RenderAsync(selected, subscription.Privacy, principal, renderCache, cancellationToken).ConfigureAwait(false);
        if (rendered.Count == 0)
            // The first selected event cannot be rendered: it fails alone and is dead-lettered after the maximum attempts.
            return await FailAsync(claim, [poison!], poison!.Sequence, new EventSinkFailed("render-error"), cancellationToken).ConfigureAwait(false);
        if (poison is not null)
            cursor = rendered[^1].Event.Sequence;   // deliver the events before the poison event first

        while (true)
        {
            EventSinkResult result;
            try
            {
                result = await sink.DeliverAsync(new EventDelivery(stored, rendered, claim.State.Attempts + 1, claim.State.Generation), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Sink exception details may contain customer data; only a code is recorded.
                result = new EventSinkFailed("sink-error");
            }
            switch (result)
            {
                case EventSinkDelivered:
                    return await CompleteAsync(claim, cursor, time.GetUtcNow(), head, gap, Outcome.Delivered, rendered.Count, cancellationToken).ConfigureAwait(false);
                case EventSinkFailed failed:
                    var failedEvents = new StoredIntegrationEvent[rendered.Count];
                    for (var i = 0; i < rendered.Count; i++)
                        failedEvents[i] = rendered[i].Event;
                    return await FailAsync(claim, failedEvents, cursor, failed, cancellationToken).ConfigureAwait(false);
                case EventSinkSplit:
                    if (rendered.Count == 1)
                        return await FailAsync(claim, [rendered[0].Event], rendered[0].Event.Sequence, new EventSinkFailed("payload-too-large", permanent: true), cancellationToken)
                            .ConfigureAwait(false);
                    rendered = rendered.GetRange(0, rendered.Count / 2);
                    cursor = rendered[^1].Event.Sequence;
                    break;
            }
        }
    }

    /// <summary>Renders the batch; when hydration or rendering fails for an event, returns the events before it and that event as poison.</summary>
    private async Task<(List<RenderedEvent> Rendered, StoredIntegrationEvent? Poison)> RenderAsync(IReadOnlyList<StoredIntegrationEvent> selected, EventPrivacy privacy,
        ApiClientPrincipal principal, ConcurrentDictionary<(TicketScope Scope, long Sequence, EventPrivacy Privacy), string>? renderCache, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<long, EventContent>? contents = null;
        if (privacy == EventPrivacy.Content)
        {
            try
            {
                contents = await hydrator.HydrateAsync(selected, principal, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                contents = null;   // locate the failing event below, one at a time
            }
        }
        var rendered = new List<RenderedEvent>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            var item = selected[i];
            try
            {
                string json;
                if (privacy == EventPrivacy.Content)
                {
                    var single = contents ?? await hydrator.HydrateAsync([item], principal, cancellationToken).ConfigureAwait(false);
                    var content = single.GetValueOrDefault(item.Sequence);
                    json = IntegrationEventCodec.Render(item, privacy, content, render);
                }
                else if (renderCache is not null)
                {
                    var cacheKey = (item.Scope, item.Sequence, privacy);
                    if (!renderCache.TryGetValue(cacheKey, out json!))
                    {
                        json = IntegrationEventCodec.Render(item, privacy, null, render);
                        renderCache.TryAdd(cacheKey, json);
                    }
                }
                else
                {
                    json = IntegrationEventCodec.Render(item, privacy, null, render);
                }
                rendered.Add(new RenderedEvent(item, json));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return (rendered, item);
            }
        }
        return (rendered, null);
    }

    private static string Gap(long from, long to) => $"events-pruned-{from}-{to}";

    private async Task<(Outcome, int)> CompleteAsync(EventDeliveryClaim claim, long cursor, DateTimeOffset? deliveredAt, long head, string? note, Outcome outcome, int events,
        CancellationToken cancellationToken)
    {
        // Round-robin: claimable again now (behind subscriptions waiting longer) while events remain; caught-up subscriptions poll later.
        var now = time.GetUtcNow();
        var next = cursor >= head ? now + options.IdlePollInterval : now;
        return await subscriptions.CompleteAsync(claim.Subscription.Scope, claim.Subscription.Subscription.Id, claim.Fence, cursor, deliveredAt, next, note, cancellationToken)
            .ConfigureAwait(false)
            ? (outcome, events)
            : (Outcome.Lost, 0);
    }

    private async Task<(Outcome, int)> FailAsync(EventDeliveryClaim claim, IReadOnlyList<StoredIntegrationEvent> batch, long cursor, EventSinkFailed failure, CancellationToken cancellationToken)
    {
        var scope = claim.Subscription.Scope;
        var id = claim.Subscription.Subscription.Id;
        var attempts = claim.State.Attempts + 1;
        var now = time.GetUtcNow();
        if (batch.Count > 0 && (failure.Permanent || attempts >= options.MaximumAttempts))
        {
            var letters = batch.Select(item => new EventDeadLetter(id, item.Sequence, item.Event.Id, item.Event.Type, attempts, failure.Code, now)).ToList();
            return await subscriptions.DeadLetterAsync(scope, id, claim.Fence, cursor, letters, now, cancellationToken).ConfigureAwait(false) ? (Outcome.DeadLettered, 0) : (Outcome.Lost, 0);
        }
        if (batch.Count == 0 && failure.Code != EngineError && attempts >= options.MaximumAttempts)
            return await subscriptions.SuspendAsync(scope, id, claim.Fence, failure.Code, cancellationToken).ConfigureAwait(false) ? (Outcome.Suspended, 0) : (Outcome.Lost, 0);
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

    public bool IsValidTarget(TicketScope scope, string target) => target is not null && subscribers.ContainsKey(target);

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
