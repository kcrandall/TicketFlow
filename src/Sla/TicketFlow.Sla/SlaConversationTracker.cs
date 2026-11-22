using Microsoft.Extensions.Options;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>
/// Keeps per-conversation SLA trackers in sync with conversations. As an <see cref="IConversationOutboxHandler"/> (consumer
/// <c>sla</c>) it re-synchronizes the conversation of every change; <see cref="SlaTimerScheduler"/> calls <see cref="SyncAsync"/> for
/// due timers. A sync reads the conversation snapshot and the timeline after the tracker's last applied sequence through the trusted
/// <see cref="IConversationStore"/> (the tracker mirrors the exact Access resource), replays the facts through <see cref="SlaEngine"/>,
/// advances to now, writes the tracker revision-fenced (retrying on conflicts) and then appends pending emissions through
/// <see cref="IConversationService.AppendExtensionAsync"/> as the configured API client — skipping any already present on the
/// timeline after the emission's recorded head, so redelivery never duplicates an event. Idempotent.
/// </summary>
public sealed class SlaConversationTracker : IConversationOutboxHandler
{
    private const int ReadPage = IConversationStore.MaximumTimelineRead;
    private const int MaximumDuplicateScan = 1000;

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
        return change.Kind switch
        {
            ConversationChangeKind.DeliveryUpdated => Task.CompletedTask,
            // The tracker's own extension events (and other extension-only changes) cannot move a clock.
            ConversationChangeKind.EventsAppended when change.Events.All(summary => summary.Kind is ConversationEventKind.Extension or ConversationEventKind.Note)
                => Task.CompletedTask,
            ConversationChangeKind.Started or ConversationChangeKind.EventsAppended or ConversationChangeKind.Deleted => SyncAsync(change.Scope, change.Conversation, cancellationToken),
            _ => throw new InvalidOperationException("Unknown conversation change kind."),
        };
    }

    /// <summary>Synchronizes the tracker of one conversation (deleting it when the conversation no longer exists) and flushes its emissions.</summary>
    public async Task SyncAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        for (var attempt = 1; ; attempt++)
        {
            var stored = await trackers.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
            var snapshot = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
            SlaWriteResult<StoredSlaTracker> result;
            SlaEngine? engine = null;
            if (snapshot is null)
            {
                if (stored is null)
                    return;
                result = await trackers.DeleteAsync(scope, conversation, stored.Revision, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                engine = await BuildAsync(scope, snapshot.Conversation, stored?.Tracker, cancellationToken).ConfigureAwait(false);
                result = await trackers.WriteAsync(scope, engine.ToTracker(SlaAccessSnapshot.From(snapshot.Conversation)), stored?.Revision, cancellationToken).ConfigureAwait(false);
            }
            switch (result)
            {
                case SlaWriteApplied<StoredSlaTracker> applied:
                    if (engine is not null)
                        await FlushAsync(scope, applied.Value, engine, snapshot!.Conversation, cancellationToken).ConfigureAwait(false);
                    return;
                case SlaWriteNotFound or SlaWriteConflict when attempt < options.MaximumWriteAttempts:
                    continue;
                case SlaWriteNotFound or SlaWriteConflict:
                    throw new InvalidOperationException("Concurrent SLA tracker writers kept winning.");
                case SlaWriteRejected rejected:
                    throw new InvalidOperationException($"The SLA tracker store rejected the write: {rejected.Reason}.");
            }
        }
    }

    private async Task<SlaEngine> BuildAsync(TicketScope scope, Conversation conversation, SlaTracker? tracker, CancellationToken cancellationToken)
    {
        var inbox = (await inboxes.GetAsync(scope, conversation.Inbox, cancellationToken).ConfigureAwait(false))?.Inbox;
        var policy = inbox?.SlaPolicy is { } policyId ? await policies.GetAsync(scope, policyId, cancellationToken).ConfigureAwait(false) : null;
        var times = new Dictionary<BusinessCalendarId, SlaClockTime?>();
        foreach (var id in new[] { inbox?.Calendar, tracker?.Applied?.Calendar })
            if (id is { } calendarId && !times.ContainsKey(calendarId))
                times[calendarId] = await calendars.GetAsync(scope, calendarId, cancellationToken).ConfigureAwait(false) is { } calendar
                    ? SlaClockTime.Business(new BusinessCalendarCalculator(calendar.Calendar))
                    : null;
        SlaClockTime? Resolve(BusinessCalendarId? id) => id is { } key && times.TryGetValue(key, out var value) ? value : null;

        SlaEngine engine;
        var now = time.GetUtcNow();
        if (tracker is not null)
        {
            engine = SlaEngine.From(tracker, Resolve);
            engine.SetLabels(conversation.Labels);
        }
        else
            engine = SlaEngine.Create(conversation.Id, conversation.Inbox,
                new SlaConversationFacts(ConversationStatusKind.Open, null, conversation.Priority, null, replied: false), conversation.Labels, conversation.CreatedAt, 0, Resolve);
        engine.Policy = policy;
        engine.InboxCalendar = inbox?.Calendar;
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
            foreach (var timelineEvent in events.Where(item => item.Sequence <= conversation.LastSequence))
                engine.Apply(timelineEvent);
            if (events.Count < ReadPage)
                break;
            if (engine.LastSequence == before)
                break;
        }
        engine.AdvanceTo(now);
        return engine;
    }

    private async Task FlushAsync(TicketScope scope, StoredSlaTracker stored, SlaEngine engine, Conversation conversation, CancellationToken cancellationToken)
    {
        if (stored.Tracker.Pending.Count == 0)
            return;
        var actor = new ApiClientPrincipal(scope, options.WorkerClientId);
        var flushed = new List<string>();
        foreach (var emission in stored.Tracker.Pending)
        {
            if (!await AlreadyAppendedAsync(scope, conversation.Id, emission, cancellationToken).ConfigureAwait(false))
            {
                var result = await service.AppendExtensionAsync(actor, conversation.Id, SlaEventCodec.Encode(emission.Event), cancellationToken: cancellationToken).ConfigureAwait(false);
                switch (result)
                {
                    case ConversationApplied:
                        break;
                    case ConversationNotFound:
                        return;
                    case ConversationConflict conflict:
                        throw new InvalidOperationException($"Unexpected conversation conflict at revision {conflict.CurrentRevision}.");
                    case ConversationRefused refused:
                        throw new InvalidOperationException($"The conversation service refused the SLA event ({refused.Reason}); grant the SLA worker conversation:manage.");
                }
            }
            flushed.Add(emission.Key);
        }
        engine.Acknowledge(flushed);
        // A conflicting writer loaded the same pending emissions and flushes them itself (duplicates are detected), so a lost race is fine.
        _ = await trackers.WriteAsync(scope, engine.ToTracker(stored.Tracker.Access), stored.Revision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the conversation timeline after the emission's recorded head already holds an SLA event with its key.</summary>
    private async Task<bool> AlreadyAppendedAsync(TicketScope scope, ConversationId conversation, SlaPendingEmission emission, CancellationToken cancellationToken)
    {
        var after = emission.AfterSequence;
        for (var scanned = 0; scanned < MaximumDuplicateScan;)
        {
            var events = await conversations.ReadTimelineAsync(scope, conversation, after, ReadPage, includeNotes: false, cancellationToken).ConfigureAwait(false);
            foreach (var timelineEvent in events)
                if (timelineEvent.Event.Value is ExtensionEvent extension && extension.Kind == emission.Event.Kind
                    && SlaEventCodec.TryDecode(extension, out var decoded) && decoded.Key == emission.Key)
                    return true;
            if (events.Count < ReadPage)
                return false;
            after = events[^1].Sequence;
            scanned += events.Count;
        }
        return false;
    }
}
