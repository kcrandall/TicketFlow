using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>
/// Keeps per-conversation SLA trackers in sync with conversations. As an <see cref="IConversationOutboxHandler"/> (consumer
/// <c>sla</c>) it re-synchronizes the conversation of every change; <see cref="SlaTimerScheduler"/> calls <see cref="SyncAsync(TicketScope, ConversationId, SlaTimerClaim?, CancellationToken)"/> for
/// due timers. A sync reads the conversation snapshot and the timeline after the tracker's last applied sequence through the trusted
/// <see cref="IConversationStore"/> (the tracker mirrors the exact Access resource), replays the facts through <see cref="SlaEngine"/>,
/// advances to now, writes the tracker revision-fenced (retrying on conflicts) and then appends pending emissions through
/// <see cref="IConversationService.AppendExtensionAsync"/> as the configured API client — skipping any already present on the
/// timeline after the emission's recorded head, so redelivery never duplicates an event. Idempotent.
/// </summary>
public sealed class SlaConversationTracker : IConversationOutboxHandler
{
    private const int ReadPage = IConversationStore.MaximumTimelineRead;

    private readonly IConversationStore conversations;
    private readonly IConversationService service;
    private readonly IInboxStore inboxes;
    private readonly IBusinessCalendarStore calendars;
    private readonly ISlaPolicyStore policies;
    private readonly ISlaTrackerStore trackers;
    private readonly SlaOptions options;
    private readonly TimeProvider time;

    public SlaConversationTracker(IConversationStore conversations, IConversationService service, IInboxStore inboxes, IBusinessCalendarStore calendars,
        ISlaPolicyStore policies, ISlaTrackerStore trackers, IOptions<SlaOptions> options, TimeProvider time)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.trackers = trackers ?? throw new ArgumentNullException(nameof(trackers));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public string Consumer => SlaOptions.Consumer;

    public Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var change = entry.Change;
        switch (change.Kind)
        {
            case ConversationChangeKind.DeliveryUpdated:
                return Task.CompletedTask;
            case ConversationChangeKind.EventsAppended:
                var allIgnored = true;
                for (var i = 0; i < change.Events.Count; i++)
                {
                    if (change.Events[i].Kind is not (ConversationEventKind.Extension or ConversationEventKind.Note))
                    {
                        allIgnored = false;
                        break;
                    }
                }
                return allIgnored ? Task.CompletedTask : SyncAsync(change.Scope, change.Conversation, cancellationToken);
            case ConversationChangeKind.Started:
            case ConversationChangeKind.Deleted:
                return SyncAsync(change.Scope, change.Conversation, cancellationToken);
            default:
                throw new InvalidOperationException("Unknown conversation change kind.");
        }
    }

    /// <summary>Synchronizes the tracker of one conversation (deleting it when the conversation no longer exists) and flushes its emissions.</summary>
    /// <exception cref="SlaFlushDeferredException">Emissions could not be flushed; the tracker was rescheduled for a retry.</exception>
    public Task SyncAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default) =>
        SyncInternalAsync(scope, conversation, null, cache: null, cancellationToken);

    /// <summary>
    /// Synchronizes as <see cref="SyncAsync(TicketScope, ConversationId, CancellationToken)"/> on behalf of the timer
    /// <paramref name="claim"/>: the final tracker write releases that claim's lease (and only that one).
    /// </summary>
    public Task SyncAsync(TicketScope scope, ConversationId conversation, SlaTimerClaim? claim, CancellationToken cancellationToken = default) =>
        SyncInternalAsync(scope, conversation, claim, cache: null, cancellationToken);

    internal async Task SyncInternalAsync(TicketScope scope, ConversationId conversation, SlaTimerClaim? claim, SlaRunCache? cache, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        if (claim is not null && (claim.Scope != scope || claim.Conversation != conversation))
            throw new ArgumentException("The claim belongs to a different tracker.", nameof(claim));
        for (var attempt = 1; ; attempt++)
        {
            var storedTask = trackers.GetAsync(scope, conversation, cancellationToken);
            var snapshotTask = conversations.GetAsync(scope, conversation, cancellationToken);
            await Task.WhenAll(storedTask, snapshotTask).ConfigureAwait(false);
            var stored = storedTask.Result;
            var snapshot = snapshotTask.Result;
            StoreWriteResult<StoredSlaTracker, SlaWriteRejection> result;
            SlaEngine? engine = null;
            if (snapshot is null)
            {
                if (stored is null)
                    return;
                result = await trackers.DeleteAsync(scope, conversation, stored.Revision, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                engine = await BuildAsync(scope, snapshot.Conversation, stored?.Tracker, cache, cancellationToken).ConfigureAwait(false);
                var tracker = engine.ToTracker(SlaAccessSnapshot.From(snapshot.Conversation));
                // Keep the lease while emissions remain to be flushed; the acknowledgement (or back-off) write releases it.
                result = await trackers.WriteAsync(scope, tracker, stored?.Revision, tracker.Pending.Count == 0 ? claim : null, cancellationToken).ConfigureAwait(false);
            }
            switch (result)
            {
                case StoreApplied<StoredSlaTracker> applied:
                    if (engine is not null && applied.Value.Tracker.Pending.Count > 0)
                        await FlushAsync(scope, applied.Value, engine, snapshot!, claim, cancellationToken).ConfigureAwait(false);
                    return;
                case StoreNotFound or StoreConflict when attempt < options.MaximumWriteAttempts:
                    continue;
                case StoreNotFound or StoreConflict:
                    throw new InvalidOperationException("Concurrent SLA tracker writers kept winning.");
                case StoreRejected<SlaWriteRejection> rejected:
                    throw new InvalidOperationException($"The SLA tracker store rejected the write: {rejected.Reason}.");
            }
        }
    }

    private async Task<SlaEngine> BuildAsync(TicketScope scope, Conversation conversation, SlaTracker? tracker, SlaRunCache? cache, CancellationToken cancellationToken)
    {
        var inbox = await GetInboxAsync(scope, conversation.Inbox, cache, cancellationToken).ConfigureAwait(false);
        var policyId = inbox?.SlaPolicy;
        var inboxCalId = inbox?.Calendar;
        var appliedCalId = tracker?.Applied?.Calendar;
        var needAppliedCal = appliedCalId is { } aId && aId != inboxCalId;

        Task<StoredSlaPolicy?>? policyTask = policyId is { } pId ? GetPolicyAsync(scope, pId, cache, cancellationToken) : null;
        Task<SlaClockTime?>? inboxCalTask = inboxCalId is { } iId ? GetCalendarAsync(scope, iId, cache, cancellationToken) : null;
        Task<SlaClockTime?>? appliedCalTask = needAppliedCal ? GetCalendarAsync(scope, appliedCalId!.Value, cache, cancellationToken) : null;

        if (policyTask is not null && inboxCalTask is not null && appliedCalTask is not null)
            await Task.WhenAll(policyTask, inboxCalTask, appliedCalTask).ConfigureAwait(false);
        else if (policyTask is not null && inboxCalTask is not null)
            await Task.WhenAll(policyTask, inboxCalTask).ConfigureAwait(false);
        else if (policyTask is not null && appliedCalTask is not null)
            await Task.WhenAll(policyTask, appliedCalTask).ConfigureAwait(false);
        else if (inboxCalTask is not null && appliedCalTask is not null)
            await Task.WhenAll(inboxCalTask, appliedCalTask).ConfigureAwait(false);

        var policy = policyTask is not null ? await policyTask.ConfigureAwait(false) : null;
        var inboxCalTime = inboxCalTask is not null ? await inboxCalTask.ConfigureAwait(false) : null;
        var appliedCalTime = needAppliedCal ? await appliedCalTask!.ConfigureAwait(false) : (appliedCalId == inboxCalId ? inboxCalTime : null);

        SlaClockTime? Resolve(BusinessCalendarId? id)
        {
            if (id is not { } key)
                return null;
            if (inboxCalId == key)
                return inboxCalTime;
            if (appliedCalId == key)
                return appliedCalTime;
            return null;
        }

        SlaEngine engine;
        var now = time.GetUtcNow();
        // A resumed tracker keeps its own label set (as of LastSequence) and replays later label changes; only a new tracker starts
        // from the snapshot labels (start requests set labels without timeline events).
        if (tracker is not null)
            engine = SlaEngine.From(tracker, Resolve);
        else
            engine = SlaEngine.Create(conversation.Id, conversation.Inbox,
                new SlaConversationFacts(ConversationStatusKind.Open, null, conversation.Priority, null, replied: false), conversation.Labels, conversation.CreatedAt, 0, Resolve);
        engine.Policy = policy;
        engine.InboxCalendar = inboxCalId;
        engine.Head = conversation.LastSequence;
        if (tracker is null)
        {
            if (conversation.LastSequence <= options.MaximumReplayEvents)
                engine.Rematch(conversation.CreatedAt);
            else
                engine.Adopt(conversation);
        }
        while (engine.LastSequence < conversation.LastSequence)
        {
            var events = await conversations.ReadTimelineAsync(scope, conversation.Id, engine.LastSequence, ReadPage, includeNotes: false, cancellationToken).ConfigureAwait(false);
            var before = engine.LastSequence;
            for (var i = 0; i < events.Count; i++)
            {
                var item = events[i];
                if (item.Sequence <= conversation.LastSequence)
                    engine.Apply(item);
            }
            if (events.Count < ReadPage || engine.LastSequence == before)
                break;
        }
        engine.AdvanceTo(now);
        return engine;
    }

    private async Task<Inbox?> GetInboxAsync(TicketScope scope, InboxId id, SlaRunCache? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.Inboxes.TryGetValue(scope, id, out var cached))
            return cached;
        var inbox = (await inboxes.GetAsync(scope, id, cancellationToken).ConfigureAwait(false))?.Inbox;
        if (cache is not null)
            cache.Inboxes[scope, id] = inbox;
        return inbox;
    }

    private async Task<StoredSlaPolicy?> GetPolicyAsync(TicketScope scope, SlaPolicyId id, SlaRunCache? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.Policies.TryGetValue(scope, id, out var cached))
            return cached;
        var policy = await policies.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (cache is not null)
            cache.Policies[scope, id] = policy;
        return policy;
    }

    private async Task<SlaClockTime?> GetCalendarAsync(TicketScope scope, BusinessCalendarId id, SlaRunCache? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.Calendars.TryGetValue(scope, id, out var cached))
            return cached;
        var clock = await calendars.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } calendar
            && WorkspaceTimeZones.TryResolve(calendar.Calendar.TimeZoneId, out var zone)
            ? SlaClockTime.Business(new BusinessCalendarCalculator(calendar.Calendar, zone))
            : null;
        if (cache is not null)
            cache.Calendars[scope, id] = clock;
        return clock;
    }

    /// <summary>
    /// Appends pending emissions exactly once. Each round reads the conversation revision (reusing <paramref name="initialSnapshot"/>
    /// on round 1), scans the timeline once from the oldest pending emission's recorded head for SLA events written by the worker
    /// principal (events by any other actor never count, so a forged event cannot suppress a real one), then appends the missing
    /// emissions fenced on that revision.
    /// </summary>
    private async Task FlushAsync(TicketScope scope, StoredSlaTracker stored, SlaEngine engine, StoredConversation initialSnapshot, SlaTimerClaim? claim, CancellationToken cancellationToken)
    {
        var actor = new ApiClientPrincipal(scope, options.WorkerClientId);
        var worker = AccessActor.From(actor);
        var id = stored.Conversation;
        var pending = stored.Tracker.Pending;
        var minAfter = pending[0].AfterSequence;
        for (var i = 1; i < pending.Count; i++)
            if (pending[i].AfterSequence < minAfter)
                minAfter = pending[i].AfterSequence;
        var flushed = new HashSet<string>(pending.Count, StringComparer.Ordinal);
        string? failure = null;
        for (var round = 1; failure is null && flushed.Count < pending.Count; round++)
        {
            if (round > options.MaximumWriteAttempts)
            {
                failure = "Concurrent conversation writers kept winning.";
                break;
            }
            var snapshot = round == 1 ? initialSnapshot : await conversations.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
                return;
            var present = await ScanAsync(scope, id, minAfter, snapshot.Conversation.LastSequence, worker, cancellationToken).ConfigureAwait(false);
            if (present is null)
            {
                failure = "The duplicate scan exceeds its bound.";
                break;
            }
            var revision = snapshot.Revision;
            var conflicted = false;
            for (var i = 0; i < pending.Count; i++)
            {
                var emission = pending[i];
                if (flushed.Contains(emission.Key))
                    continue;
                if (present.Contains((emission.Event.Kind, emission.Key)))
                {
                    flushed.Add(emission.Key);
                    continue;
                }
                var result = await service.AppendExtensionAsync(actor, id, SlaEventCodec.Encode(emission.Event), revision, cancellationToken).ConfigureAwait(false);
                switch (result)
                {
                    case ConversationApplied applied:
                        revision = applied.Revision;
                        flushed.Add(emission.Key);
                        continue;
                    case ConversationNotFound:
                        return;
                    case ConversationConflict:
                        conflicted = true;
                        break;
                    case ConversationRefused refused:
                        failure = $"The conversation service refused the SLA event ({refused.Reason}); grant the SLA worker conversation:manage.";
                        break;
                }
                break;
            }
            if (!conflicted && failure is null && flushed.Count < pending.Count)
                failure = "Emissions remain unflushed.";
        }
        engine.Acknowledge(flushed);
        if (failure is not null)
            engine.DeferFlush(time.GetUtcNow() + options.TimerRetryDelay);
        // Losing this race is fine: the winner loaded the same pending emissions and flushes them through the same fenced protocol.
        _ = await trackers.WriteAsync(scope, engine.ToTracker(stored.Tracker.Access), stored.Revision, claim, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
            throw new SlaFlushDeferredException(failure);
    }

    /// <summary>
    /// The (kind, key) pairs of SLA events appended by <paramref name="worker"/> after <paramref name="after"/> up to <paramref name="head"/>,
    /// or <see langword="null"/> when the span exceeds the configured bound (undecidable).
    /// </summary>
    private async Task<HashSet<(string Kind, string Key)>?> ScanAsync(TicketScope scope, ConversationId conversation, long after, long head, AccessActor worker,
        CancellationToken cancellationToken)
    {
        if (head - after > options.MaximumDuplicateScanEvents)
            return null;
        if (after >= head)
            return EmptyPresent;
        var present = new HashSet<(string Kind, string Key)>();
        while (after < head)
        {
            var events = await conversations.ReadTimelineAsync(scope, conversation, after, ReadPage, includeNotes: false, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < events.Count; i++)
            {
                var timelineEvent = events[i];
                if (timelineEvent.Actor == worker && timelineEvent.Event.Value is ExtensionEvent extension && SlaEventCodec.TryDecode(extension, out var decoded))
                    present.Add((decoded.Kind, decoded.Key));
            }
            if (events.Count < ReadPage)
                break;
            after = events[^1].Sequence;
        }
        return present;
    }

    private static readonly HashSet<(string Kind, string Key)> EmptyPresent = [];
}

internal sealed class SlaRunCache
{
    public Dictionary<(TicketScope Scope, InboxId Inbox), Inbox?> Inboxes { get; } = [];
    public Dictionary<(TicketScope Scope, SlaPolicyId Policy), StoredSlaPolicy?> Policies { get; } = [];
    public Dictionary<(TicketScope Scope, BusinessCalendarId Calendar), SlaClockTime?> Calendars { get; } = [];
}

/// <summary>
/// Pending SLA emissions could not be appended (refused, conflicting or undecidable duplicate scan). The tracker was written with the
/// remaining emissions and <see cref="SlaTracker.FlushRetryAt"/>, which reschedules its timer; the message carries no customer data.
/// </summary>
public sealed class SlaFlushDeferredException(string message) : InvalidOperationException(message);
