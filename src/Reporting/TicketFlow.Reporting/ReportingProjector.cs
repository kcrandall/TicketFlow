using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

namespace TicketFlow.Reporting;

/// <summary>
/// Projects conversations into report facts. As an <see cref="IConversationOutboxHandler"/> (consumer <c>reporting</c>) it
/// synchronizes the conversation of every change (delivery updates are skipped): it reads the reporting state and the conversation
/// snapshot, replays the timeline after the state's <see cref="ReportConversationState.LastSequence"/> through the trusted
/// <see cref="IConversationStore"/> and writes the new state with the derived facts atomically and revision-fenced, one timeline page
/// per write. Facts are keyed by their source timeline event, so redelivery, concurrent syncs and replays never count anything twice.
/// A conversation first seen by the projector (for example because the consumer registered after it was created) is replayed from
/// sequence 1 — its historical facts land in their own past cells — unless its timeline exceeds
/// <see cref="ReportingOptions.MaximumReplayEvents"/>. A deleted conversation's state is removed; its facts remain.
/// </summary>
public sealed class ReportingProjector : IConversationOutboxHandler, IConversationOutboxBatchHandler
{
    private const int ReadPage = IConversationStore.MaximumTimelineRead;

    private readonly IConversationStore conversations;
    private readonly IInboxStore inboxes;
    private readonly IBusinessCalendarStore calendars;
    private readonly IReportingStore store;
    private readonly ReportingOptions options;
    private readonly AccessActor slaWorker;

    public ReportingProjector(IConversationStore conversations, IInboxStore inboxes, IBusinessCalendarStore calendars, IReportingStore store, IOptions<ReportingOptions> options)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        slaWorker = new AccessActor(AccessActorKind.ApiClient, this.options.SlaWorkerClientId.Value);
    }

    public string Consumer => ReportingOptions.Consumer;

    public Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var change = entry.Change;
        return change.Kind switch
        {
            ConversationChangeKind.DeliveryUpdated => Task.CompletedTask,
            ConversationChangeKind.Started or ConversationChangeKind.EventsAppended or ConversationChangeKind.Deleted => SyncAsync(change.Scope, change.Conversation, cancellationToken),
            _ => throw new InvalidOperationException("Unknown conversation change kind."),
        };
    }

    public async Task HandleBatchAsync(IReadOnlyList<ConversationOutboxEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;
        if (entries.Count == 1)
        {
            await HandleAsync(entries[0], cancellationToken).ConfigureAwait(false);
            return;
        }
        var seen = new HashSet<(TicketScope Scope, ConversationId Conversation)>();
        var batchCache = new BatchLookupCache(inboxes, calendars);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i] ?? throw new ArgumentNullException(nameof(entries));
            var change = entry.Change;
            switch (change.Kind)
            {
                case ConversationChangeKind.DeliveryUpdated:
                    continue;
                case ConversationChangeKind.Started:
                case ConversationChangeKind.EventsAppended:
                case ConversationChangeKind.Deleted:
                    if (seen.Add((change.Scope, change.Conversation)))
                        await SyncCoreAsync(change.Scope, change.Conversation, batchCache, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("Unknown conversation change kind.");
            }
        }
    }

    /// <summary>Brings the reporting state and facts of one conversation up to its current timeline head. Idempotent.</summary>
    public Task SyncAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        return SyncCoreAsync(scope, conversation, null, cancellationToken);
    }

    private async Task SyncCoreAsync(TicketScope scope, ConversationId conversation, BatchLookupCache? batchCache, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (await TrySyncAsync(scope, conversation, batchCache, cancellationToken).ConfigureAwait(false))
                return;
            if (attempt >= options.MaximumWriteAttempts)
                throw new InvalidOperationException("Concurrent reporting projector writers kept winning.");
        }
    }

    /// <summary>One synchronization pass; false when a concurrent writer won and the pass must be repeated.</summary>
    private async Task<bool> TrySyncAsync(TicketScope scope, ConversationId id, BatchLookupCache? batchCache, CancellationToken cancellationToken)
    {
        var storedTask = store.GetConversationAsync(scope, id, cancellationToken);
        var snapshotTask = conversations.GetAsync(scope, id, cancellationToken);
        await Task.WhenAll(storedTask, snapshotTask).ConfigureAwait(false);
        var stored = storedTask.Result;
        var snapshot = snapshotTask.Result;
        if (snapshot is null)
            return stored is null || Applied(await store.DeleteConversationAsync(scope, id, stored.Revision, cancellationToken).ConfigureAwait(false)) is not null;
        var conversation = snapshot.Conversation;
        if (stored is not null && stored.State.LastSequence >= conversation.LastSequence)
            return true;

        Inbox? loadedInbox = null;
        var inboxLoaded = false;
        BusinessCalendarCalculator? loadedCalculator = null;
        var calculatorLoaded = false;

        async ValueTask<Inbox?> GetInboxAsync()
        {
            if (!inboxLoaded)
            {
                loadedInbox = batchCache is not null
                    ? await batchCache.GetInboxAsync(scope, conversation.Inbox, cancellationToken).ConfigureAwait(false)
                    : (await inboxes.GetAsync(scope, conversation.Inbox, cancellationToken).ConfigureAwait(false))?.Inbox;
                inboxLoaded = true;
            }
            return loadedInbox;
        }

        async ValueTask<BusinessCalendarCalculator?> GetCalculatorAsync()
        {
            if (!calculatorLoaded)
            {
                var inbox = await GetInboxAsync().ConfigureAwait(false);
                if (inbox?.Calendar is { } calId)
                    loadedCalculator = batchCache is not null
                        ? await batchCache.GetCalculatorAsync(scope, calId, cancellationToken).ConfigureAwait(false)
                        : await CalculatorAsync(scope, calId, cancellationToken).ConfigureAwait(false);
                calculatorLoaded = true;
            }
            return loadedCalculator;
        }

        ReportReplay replay;
        IReadOnlyList<TimelineEvent>? history = null;
        if (stored is not null)
            replay = new ReportReplay(stored.State, GetCalculatorAsync, slaWorker);
        else
        {
            var channel = Channel(conversation.ChannelThread?.Channel);
            if (channel is null)
            {
                var inbox = await GetInboxAsync().ConfigureAwait(false);
                channel = Channel(inbox?.Channel.Kind.Value);
            }
            if (conversation.LastSequence <= options.MaximumReplayEvents)
            {
                history = await ReadAsync(scope, id, 0, conversation.LastSequence, cancellationToken).ConfigureAwait(false);
                replay = new ReportReplay(Initial(conversation, history, channel), GetCalculatorAsync, slaWorker);
            }
            else
                replay = new ReportReplay(Adopt(conversation, channel), GetCalculatorAsync, slaWorker);
            replay.Created(ReportFactIds.Created(scope, id));
        }

        var revision = stored?.Revision;
        if (history is not null || stored is null)
        {
            if (history is not null && history.Count > 0)
            {
                for (var offset = 0; offset < history.Count; offset += ReadPage)
                {
                    var end = Math.Min(offset + ReadPage, history.Count);
                    for (var i = offset; i < end; i++)
                        await replay.ApplyAsync(history[i]).ConfigureAwait(false);
                    if ((revision = await WriteAsync(scope, replay, revision, cancellationToken).ConfigureAwait(false)) is null)
                        return false;
                }
            }
            if (replay.Facts.Count > 0 && (revision = await WriteAsync(scope, replay, revision, cancellationToken).ConfigureAwait(false)) is null)
                return false;
            return true;
        }
        while (replay.LastSequence < conversation.LastSequence)
        {
            var events = await conversations.ReadTimelineAsync(scope, id, replay.LastSequence, ReadPage, includeNotes: true, cancellationToken).ConfigureAwait(false);
            var before = replay.LastSequence;
            for (var i = 0; i < events.Count; i++)
            {
                var timelineEvent = events[i];
                if (timelineEvent.Sequence > before && timelineEvent.Sequence <= conversation.LastSequence)
                    await replay.ApplyAsync(timelineEvent).ConfigureAwait(false);
            }
            if (replay.LastSequence == before)
                break;
            if ((revision = await WriteAsync(scope, replay, revision, cancellationToken).ConfigureAwait(false)) is null)
                return false;
        }
        return true;
    }

    /// <summary>Writes the replay's state and pending facts; returns the new revision, or null on a conflict.</summary>
    private async Task<long?> WriteAsync(TicketScope scope, ReportReplay replay, long? revision, CancellationToken cancellationToken)
    {
        var applied = Applied(await store.ApplyAsync(scope, new ReportProjection(replay.Snapshot(), replay.Facts), revision, cancellationToken).ConfigureAwait(false));
        if (applied is null)
            return null;
        replay.Facts.Clear();
        return applied.Conversation.Revision;
    }

    private static ReportWriteApplied? Applied(ReportWriteResult result) => result switch
    {
        ReportWriteApplied applied => applied,
        ReportWriteConflict => null,
        ReportWriteRejected rejected => throw new InvalidOperationException($"The reporting store rejected the write: {rejected.Reason}."),
    };

    private async Task<IReadOnlyList<TimelineEvent>> ReadAsync(TicketScope scope, ConversationId id, long after, long head, CancellationToken cancellationToken)
    {
        var events = new List<TimelineEvent>((int)Math.Min(head - after, ReadPage));
        while (after < head)
        {
            var page = await conversations.ReadTimelineAsync(scope, id, after, ReadPage, includeNotes: true, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < page.Count; i++)
            {
                var item = page[i];
                if (item.Sequence > after && item.Sequence <= head)
                    events.Add(item);
            }
            if (page.Count == 0 || page[^1].Sequence <= after)
                break;
            after = page[^1].Sequence;
        }
        return events;
    }

    /// <summary>The inbox's business calendar calculator, or null (calendar time) without a calendar, when it was deleted or its zone does not resolve here.</summary>
    private async Task<BusinessCalendarCalculator?> CalculatorAsync(TicketScope scope, BusinessCalendarId id, CancellationToken cancellationToken)
    {
        if (await calendars.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is not { } calendar)
            return null;
        return WorkspaceTimeZones.TryResolve(calendar.Calendar.TimeZoneId, out var zone) ? new BusinessCalendarCalculator(calendar.Calendar, zone) : null;
    }

    private static string? Channel(string? value) => ReportText.IsToken(value, ReportDimensions.MaximumChannelLength) ? value : null;

    /// <summary>The state at creation: the snapshot with every timeline change undone (first previous values; labels reversed).</summary>
    private static ReportConversationState Initial(Conversation conversation, IReadOnlyList<TimelineEvent> history, string? channel)
    {
        var assignee = conversation.Assignee;
        var team = conversation.Team;
        var priority = conversation.Priority;
        var status = conversation.Status.Kind;
        bool assigneeSeen = false, teamSeen = false, prioritySeen = false, statusSeen = false;
        var hasLabelChanges = false;
        for (var i = 0; i < history.Count; i++)
            switch (history[i].Event.Value)
            {
                case AssignmentChanged change when !assigneeSeen:
                    (assignee, assigneeSeen) = (change.Previous, true);
                    break;
                case TeamChanged change when !teamSeen:
                    (team, teamSeen) = (change.Previous, true);
                    break;
                case PriorityChanged change when !prioritySeen:
                    (priority, prioritySeen) = (change.Previous, true);
                    break;
                case StatusChanged change when !statusSeen:
                    (status, statusSeen) = (change.Previous.Kind, true);
                    break;
                case LabelsChanged:
                    hasLabelChanges = true;
                    break;
            }
        IReadOnlyList<LabelId> initialLabels;
        if (!hasLabelChanges)
            initialLabels = conversation.Labels;
        else
        {
            var labels = new HashSet<LabelId>(conversation.Labels);
            for (var i = history.Count - 1; i >= 0; i--)
                if (history[i].Event.Value is LabelsChanged change)
                {
                    labels.ExceptWith(change.Added);
                    labels.UnionWith(change.Removed);
                }
            var sorted = new LabelId[labels.Count];
            labels.CopyTo(sorted);
            Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Value, b.Value));
            initialLabels = sorted.Length > ReportDimensions.MaximumLabels ? sorted[..ReportDimensions.MaximumLabels] : sorted;
        }
        return new ReportConversationState(conversation.Id, conversation.Inbox, channel, conversation.CreatedAt, status, assignee, team, priority,
            initialLabels, null, null, 0, 0);
    }

    /// <summary>A state adopted from the snapshot without replaying history.</summary>
    private static ReportConversationState Adopt(Conversation conversation, string? channel)
    {
        var status = conversation.Status.Kind;
        return new ReportConversationState(conversation.Id, conversation.Inbox, channel, conversation.CreatedAt, status, conversation.Assignee, conversation.Team,
            conversation.Priority, conversation.Labels, conversation.WaitingSince, conversation.FirstReplyAt,
            status is ConversationStatusKind.Resolved or ConversationStatusKind.Closed ? 1 : 0, conversation.LastSequence);
    }

    private sealed class BatchLookupCache(IInboxStore inboxes, IBusinessCalendarStore calendars)
    {
        private readonly Dictionary<(TicketScope, InboxId), Inbox?> inboxCache = [];
        private readonly Dictionary<(TicketScope, BusinessCalendarId), BusinessCalendarCalculator?> calendarCache = [];

        public async ValueTask<Inbox?> GetInboxAsync(TicketScope scope, InboxId id, CancellationToken cancellationToken)
        {
            var key = (scope, id);
            if (inboxCache.TryGetValue(key, out var cached))
                return cached;
            var inbox = (await inboxes.GetAsync(scope, id, cancellationToken).ConfigureAwait(false))?.Inbox;
            inboxCache[key] = inbox;
            return inbox;
        }

        public async ValueTask<BusinessCalendarCalculator?> GetCalculatorAsync(TicketScope scope, BusinessCalendarId id, CancellationToken cancellationToken)
        {
            var key = (scope, id);
            if (calendarCache.TryGetValue(key, out var cached))
                return cached;
            BusinessCalendarCalculator? calc = null;
            if (await calendars.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } calendar
                && WorkspaceTimeZones.TryResolve(calendar.Calendar.TimeZoneId, out var zone))
                calc = new BusinessCalendarCalculator(calendar.Calendar, zone);
            calendarCache[key] = calc;
            return calc;
        }
    }
}

/// <summary>The mutable fold of timeline events into a reporting state and facts.</summary>
internal sealed class ReportReplay
{
    private readonly ConversationId conversation;
    private readonly InboxId inbox;
    private readonly string? channel;
    private readonly DateTimeOffset createdAt;
    private readonly Func<ValueTask<BusinessCalendarCalculator?>> getCalculator;
    private readonly AccessActor slaWorker;

    private ConversationStatusKind status;
    private UserId? assignee;
    private TeamId? team;
    private ConversationPriority priority;
    private IReadOnlyList<LabelId> labels;
    private DateTimeOffset? waitingSince;
    private DateTimeOffset? firstResponseAt;
    private int resolutions;
    private long lastSequence;
    private ReportDimensions? cachedDimensions;

    public ReportReplay(ReportConversationState state, Func<ValueTask<BusinessCalendarCalculator?>> getCalculator, AccessActor slaWorker)
    {
        conversation = state.Conversation;
        inbox = state.Inbox;
        channel = state.Channel;
        createdAt = state.CreatedAt;
        status = state.Status;
        assignee = state.Assignee;
        team = state.Team;
        priority = state.Priority;
        labels = state.Labels;
        waitingSince = state.WaitingSince;
        firstResponseAt = state.FirstResponseAt;
        resolutions = state.Resolutions;
        lastSequence = state.LastSequence;
        this.getCalculator = getCalculator;
        this.slaWorker = slaWorker;
    }

    public long LastSequence => lastSequence;

    public List<ReportFact> Facts { get; } = [];

    public ReportConversationState Snapshot() => new(conversation, inbox, channel, createdAt, status, assignee, team, priority, labels, waitingSince, firstResponseAt, resolutions, lastSequence);

    public void Created(Guid source) => Add(source, ReportMetric.ConversationCreated, createdAt, Dimensions());

    public ValueTask ApplyAsync(TimelineEvent timelineEvent)
    {
        var at = timelineEvent.OccurredAt;
        var source = timelineEvent.Id;
        switch (timelineEvent.Event)
        {
            case MessageEvent { Direction: MessageDirection.Incoming }:
                Add(source, ReportMetric.MessageReceived, at, Dimensions());
                waitingSince ??= at;
                break;
            case MessageEvent message:
                var replier = message.Sender.Value is PrincipalParty { Actor.Kind: AccessActorKind.User } party ? new UserId(party.Actor.Id) : (UserId?)null;
                var dimensions = Dimensions().WithAgent(replier);
                Add(source, ReportMetric.MessageSent, at, dimensions);
                // Only user replies answer the contact; automated and API messages leave the wait running.
                if (replier is not null && waitingSince is { } since)
                {
                    var isFirst = firstResponseAt is null;
                    if (isFirst)
                        firstResponseAt = at;
                    waitingSince = null;
                    lastSequence = timelineEvent.Sequence;
                    return RecordReplyDurationsAsync(source, isFirst, since, at, dimensions);
                }
                break;
            case AssignmentChanged change:
                if (change.Current is { } current)
                {
                    var assigned = Dimensions().WithAgent(current);
                    Add(source, ReportMetric.Assignment, at, assigned);
                    if (change.Previous is { } previous && previous != current)
                        Add(source, ReportMetric.Handoff, at, assigned);
                }
                if (assignee != change.Current)
                {
                    assignee = change.Current;
                    cachedDimensions = null;
                }
                break;
            case TeamChanged change:
                if (change.Previous is { } previousTeam && change.Current is { } currentTeam && previousTeam != currentTeam)
                    Add(source, ReportMetric.TeamTransfer, at, new ReportDimensions(inbox, currentTeam, assignee, channel, priority, labels));
                if (team != change.Current)
                {
                    team = change.Current;
                    cachedDimensions = null;
                }
                break;
            case StatusChanged change:
                var wasResolved = IsResolved(change.Previous.Kind);
                var isResolved = IsResolved(change.Current.Kind);
                status = change.Current.Kind;
                if (isResolved && !wasResolved)
                {
                    var dims = Dimensions();
                    Add(source, ReportMetric.ConversationResolved, at, dims);
                    var firstResolution = resolutions == 0;
                    resolutions++;
                    waitingSince = null;
                    if (firstResolution)
                    {
                        lastSequence = timelineEvent.Sequence;
                        return DurationsAsync(source, ReportMetric.ResolutionTime, ReportMetric.ResolutionBusinessTime, createdAt, at, dims);
                    }
                }
                else if (wasResolved && !isResolved)
                    Add(source, ReportMetric.ConversationReopened, at, Dimensions());
                break;
            case PriorityChanged change:
                if (priority != change.Current)
                {
                    priority = change.Current;
                    cachedDimensions = null;
                }
                break;
            case LabelsChanged change:
                if (change.Removed.Count > 0 || change.Added.Count > 0)
                {
                    var set = new HashSet<LabelId>(labels);
                    set.ExceptWith(change.Removed);
                    set.UnionWith(change.Added);
                    if (set.Count == 0)
                        labels = [];
                    else
                    {
                        var sorted = new LabelId[set.Count];
                        set.CopyTo(sorted);
                        Array.Sort(sorted, static (a, b) => string.CompareOrdinal(a.Value, b.Value));
                        labels = sorted.Length > ReportDimensions.MaximumLabels ? sorted[..ReportDimensions.MaximumLabels] : sorted;
                    }
                    cachedDimensions = null;
                }
                break;
            case ExtensionEvent extension:
                // sla.* events are authentic only when appended by the configured SLA worker; forged ones are ignored.
                if (timelineEvent.Actor == slaWorker && SlaEventCodec.TryDecode(extension, out var sla))
                    switch (sla)
                    {
                        case SlaMetEvent met:
                            if (!met.Breached)
                                Add(source, ReportMetric.SlaAchieved, met.At, Dimensions());
                            break;
                        case SlaBreachedEvent breached:
                            Add(source, ReportMetric.SlaBreached, breached.At, Dimensions());
                            break;
                        case SlaAppliedEvent:
                        case SlaWarningEvent:
                            break;
                    }
                break;
            case NoteEvent:
            case AttributeChanged:
            case ParticipantChanged:
            case ContactChanged:
            case SystemEvent:
                break;
        }
        lastSequence = timelineEvent.Sequence;
        return ValueTask.CompletedTask;
    }

    private ReportDimensions Dimensions() => cachedDimensions ??= new ReportDimensions(inbox, team, assignee, channel, priority, labels);

    private static bool IsResolved(ConversationStatusKind kind) => kind is ConversationStatusKind.Resolved or ConversationStatusKind.Closed;

    private async ValueTask RecordReplyDurationsAsync(Guid source, bool isFirst, DateTimeOffset since, DateTimeOffset at, ReportDimensions dimensions)
    {
        if (isFirst)
            await DurationsAsync(source, ReportMetric.FirstResponseTime, ReportMetric.FirstResponseBusinessTime, since, at, dimensions).ConfigureAwait(false);
        await DurationsAsync(source, ReportMetric.ResponseTime, ReportMetric.ResponseBusinessTime, since, at, dimensions).ConfigureAwait(false);
    }

    private async ValueTask DurationsAsync(Guid source, ReportMetric calendarMetric, ReportMetric businessMetric, DateTimeOffset from, DateTimeOffset to, ReportDimensions dimensions)
    {
        var elapsed = to > from ? to - from : TimeSpan.Zero;
        if (elapsed > ReportFact.MaximumDuration)
            return;
        Add(source, calendarMetric, to, dimensions, ReportFact.Microseconds(elapsed));
        var business = elapsed;
        if (elapsed > TimeSpan.Zero)
        {
            var calculator = await getCalculator().ConfigureAwait(false);
            if (calculator is not null)
            {
                try
                {
                    business = calculator.BusinessTimeBetween(from, to);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return;   // outside the calculator's supported instants or horizon: no business-time fact
                }
            }
        }
        Add(source, businessMetric, to, dimensions, ReportFact.Microseconds(business));
    }

    private void Add(Guid source, ReportMetric metric, DateTimeOffset at, ReportDimensions dimensions, long value = 1) =>
        Facts.Add(new ReportFact(source, metric, conversation, at, dimensions, value));
}
