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
public sealed class ReportingProjector : IConversationOutboxHandler
{
    private const int ReadPage = IConversationStore.MaximumTimelineRead;

    private readonly IConversationStore conversations;
    private readonly IInboxStore inboxes;
    private readonly IBusinessCalendarStore calendars;
    private readonly IReportingStore store;
    private readonly ReportingOptions options;

    public ReportingProjector(IConversationStore conversations, IInboxStore inboxes, IBusinessCalendarStore calendars, IReportingStore store, IOptions<ReportingOptions> options)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
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

    /// <summary>Brings the reporting state and facts of one conversation up to its current timeline head. Idempotent.</summary>
    public async Task SyncAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        for (var attempt = 1; ; attempt++)
        {
            if (await TrySyncAsync(scope, conversation, cancellationToken).ConfigureAwait(false))
                return;
            if (attempt >= options.MaximumWriteAttempts)
                throw new InvalidOperationException("Concurrent reporting projector writers kept winning.");
        }
    }

    /// <summary>One synchronization pass; false when a concurrent writer won and the pass must be repeated.</summary>
    private async Task<bool> TrySyncAsync(TicketScope scope, ConversationId id, CancellationToken cancellationToken)
    {
        var stored = await store.GetConversationAsync(scope, id, cancellationToken).ConfigureAwait(false);
        var snapshot = await conversations.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return stored is null || Applied(await store.DeleteConversationAsync(scope, id, stored.Revision, cancellationToken).ConfigureAwait(false)) is not null;
        var conversation = snapshot.Conversation;
        if (stored is not null && stored.State.LastSequence >= conversation.LastSequence)
            return true;

        var inbox = (await inboxes.GetAsync(scope, conversation.Inbox, cancellationToken).ConfigureAwait(false))?.Inbox;
        var calculator = await CalculatorAsync(scope, inbox, cancellationToken).ConfigureAwait(false);
        var channel = Channel(conversation.ChannelThread?.Channel) ?? Channel(inbox?.Channel.Kind.Value);
        var worker = new AccessActor(AccessActorKind.ApiClient, options.SlaWorkerClientId.Value);

        ReportReplay replay;
        IReadOnlyList<TimelineEvent>? history = null;
        if (stored is not null)
            replay = new ReportReplay(stored.State, calculator, worker);
        else
        {
            if (conversation.LastSequence <= options.MaximumReplayEvents)
            {
                history = await ReadAsync(scope, id, 0, conversation.LastSequence, cancellationToken).ConfigureAwait(false);
                replay = new ReportReplay(Initial(conversation, history, channel), calculator, worker);
            }
            else
                replay = new ReportReplay(Adopt(conversation, channel), calculator, worker);
            replay.Created(ReportFactIds.Created(scope, id));
        }

        var revision = stored?.Revision;
        if (history is not null || stored is null)
        {
            foreach (var page in (history ?? []).Chunk(ReadPage))
            {
                foreach (var timelineEvent in page)
                    replay.Apply(timelineEvent);
                if ((revision = await WriteAsync(scope, replay, revision, cancellationToken).ConfigureAwait(false)) is null)
                    return false;
            }
            if (replay.Facts.Count > 0 && (revision = await WriteAsync(scope, replay, revision, cancellationToken).ConfigureAwait(false)) is null)
                return false;
            return true;
        }
        while (replay.State.LastSequence < conversation.LastSequence)
        {
            var events = await conversations.ReadTimelineAsync(scope, id, replay.State.LastSequence, ReadPage, includeNotes: true, cancellationToken).ConfigureAwait(false);
            var before = replay.State.LastSequence;
            foreach (var timelineEvent in events.Where(item => item.Sequence > before && item.Sequence <= conversation.LastSequence))
                replay.Apply(timelineEvent);
            if (replay.State.LastSequence == before)
                break;
            if ((revision = await WriteAsync(scope, replay, revision, cancellationToken).ConfigureAwait(false)) is null)
                return false;
        }
        return true;
    }

    /// <summary>Writes the replay's state and pending facts; returns the new revision, or null on a conflict.</summary>
    private async Task<long?> WriteAsync(TicketScope scope, ReportReplay replay, long? revision, CancellationToken cancellationToken)
    {
        var applied = Applied(await store.ApplyAsync(scope, new ReportProjection(replay.State, replay.Facts), revision, cancellationToken).ConfigureAwait(false));
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
        var events = new List<TimelineEvent>();
        while (after < head)
        {
            var page = await conversations.ReadTimelineAsync(scope, id, after, ReadPage, includeNotes: true, cancellationToken).ConfigureAwait(false);
            events.AddRange(page.Where(item => item.Sequence > after && item.Sequence <= head));
            if (page.Count == 0 || page[^1].Sequence <= after)
                break;
            after = page[^1].Sequence;
        }
        return events;
    }

    /// <summary>The inbox's business calendar calculator, or null (calendar time) without a calendar, when it was deleted or its zone does not resolve here.</summary>
    private async Task<BusinessCalendarCalculator?> CalculatorAsync(TicketScope scope, Inbox? inbox, CancellationToken cancellationToken)
    {
        if (inbox?.Calendar is not { } id || await calendars.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is not { } calendar)
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
        foreach (var timelineEvent in history)
            switch (timelineEvent.Event.Value)
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
            }
        var labels = new HashSet<LabelId>(conversation.Labels);
        foreach (var timelineEvent in history.Reverse())
            if (timelineEvent.Event.Value is LabelsChanged change)
            {
                labels.ExceptWith(change.Added);
                labels.UnionWith(change.Removed);
            }
        return new ReportConversationState(conversation.Id, conversation.Inbox, channel, conversation.CreatedAt, status, assignee, team, priority,
            labels.OrderBy(label => label.Value, StringComparer.Ordinal).Take(ReportDimensions.MaximumLabels), null, null, 0, 0);
    }

    /// <summary>A state adopted from the snapshot without replaying history.</summary>
    private static ReportConversationState Adopt(Conversation conversation, string? channel)
    {
        var status = conversation.Status.Kind;
        return new ReportConversationState(conversation.Id, conversation.Inbox, channel, conversation.CreatedAt, status, conversation.Assignee, conversation.Team,
            conversation.Priority, conversation.Labels, conversation.WaitingSince, conversation.FirstReplyAt,
            status is ConversationStatusKind.Resolved or ConversationStatusKind.Closed ? 1 : 0, conversation.LastSequence);
    }
}

/// <summary>The mutable fold of timeline events into a reporting state and facts.</summary>
internal sealed class ReportReplay(ReportConversationState state, BusinessCalendarCalculator? calculator, AccessActor slaWorker)
{
    public ReportConversationState State { get; private set; } = state;

    public List<ReportFact> Facts { get; } = [];

    public void Created(Guid source) => Add(source, ReportMetric.ConversationCreated, State.CreatedAt, State.Dimensions());

    public void Apply(TimelineEvent timelineEvent)
    {
        var s = State;
        var at = timelineEvent.OccurredAt;
        var source = timelineEvent.Id;
        var assignee = s.Assignee;
        var team = s.Team;
        var status = s.Status;
        var priority = s.Priority;
        IEnumerable<LabelId> labels = s.Labels;
        var waiting = s.WaitingSince;
        var firstResponse = s.FirstResponseAt;
        var resolutions = s.Resolutions;
        switch (timelineEvent.Event)
        {
            case MessageEvent { Direction: MessageDirection.Incoming }:
                Add(source, ReportMetric.MessageReceived, at, s.Dimensions());
                waiting ??= at;
                break;
            case MessageEvent message:
                var replier = message.Sender.Value is PrincipalParty { Actor.Kind: AccessActorKind.User } party ? new UserId(party.Actor.Id) : (UserId?)null;
                var dimensions = s.Dimensions().WithAgent(replier);
                Add(source, ReportMetric.MessageSent, at, dimensions);
                // Only user replies answer the contact; automated and API messages leave the wait running.
                if (replier is not null && waiting is { } since)
                {
                    if (firstResponse is null)
                    {
                        Durations(source, ReportMetric.FirstResponseTime, ReportMetric.FirstResponseBusinessTime, since, at, dimensions);
                        firstResponse = at;
                    }
                    Durations(source, ReportMetric.ResponseTime, ReportMetric.ResponseBusinessTime, since, at, dimensions);
                    waiting = null;
                }
                break;
            case AssignmentChanged change:
                assignee = change.Current;
                if (change.Current is { } current)
                {
                    var assigned = s.Dimensions().WithAgent(current);
                    Add(source, ReportMetric.Assignment, at, assigned);
                    if (change.Previous is { } previous && previous != current)
                        Add(source, ReportMetric.Handoff, at, assigned);
                }
                break;
            case TeamChanged change:
                team = change.Current;
                if (change.Previous is { } previousTeam && change.Current is { } currentTeam && previousTeam != currentTeam)
                    Add(source, ReportMetric.TeamTransfer, at, new ReportDimensions(s.Inbox, currentTeam, s.Assignee, s.Channel, s.Priority, s.Labels));
                break;
            case StatusChanged change:
                var wasResolved = IsResolved(change.Previous.Kind);
                var isResolved = IsResolved(change.Current.Kind);
                if (isResolved && !wasResolved)
                {
                    Add(source, ReportMetric.ConversationResolved, at, s.Dimensions());
                    if (resolutions == 0)
                        Durations(source, ReportMetric.ResolutionTime, ReportMetric.ResolutionBusinessTime, s.CreatedAt, at, s.Dimensions());
                    resolutions++;
                    waiting = null;
                }
                else if (wasResolved && !isResolved)
                    Add(source, ReportMetric.ConversationReopened, at, s.Dimensions());
                status = change.Current.Kind;
                break;
            case PriorityChanged change:
                priority = change.Current;
                break;
            case LabelsChanged change:
                labels = s.Labels.Except(change.Removed).Union(change.Added).OrderBy(label => label.Value, StringComparer.Ordinal).Take(ReportDimensions.MaximumLabels);
                break;
            case ExtensionEvent extension:
                // sla.* events are authentic only when appended by the configured SLA worker; forged ones are ignored.
                if (timelineEvent.Actor == slaWorker && SlaEventCodec.TryDecode(extension, out var sla))
                    switch (sla)
                    {
                        case SlaMetEvent met:
                            if (!met.Breached)
                                Add(source, ReportMetric.SlaAchieved, met.At, s.Dimensions());
                            break;
                        case SlaBreachedEvent breached:
                            Add(source, ReportMetric.SlaBreached, breached.At, s.Dimensions());
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
        State = new ReportConversationState(s.Conversation, s.Inbox, s.Channel, s.CreatedAt, status, assignee, team, priority, labels, waiting, firstResponse, resolutions,
            timelineEvent.Sequence);
    }

    private static bool IsResolved(ConversationStatusKind kind) => kind is ConversationStatusKind.Resolved or ConversationStatusKind.Closed;

    private void Durations(Guid source, ReportMetric calendarMetric, ReportMetric businessMetric, DateTimeOffset from, DateTimeOffset to, ReportDimensions dimensions)
    {
        var elapsed = to > from ? to - from : TimeSpan.Zero;
        if (elapsed > ReportFact.MaximumDuration)
            return;
        Add(source, calendarMetric, to, dimensions, ReportFact.Microseconds(elapsed));
        var business = elapsed;
        if (calculator is not null && elapsed > TimeSpan.Zero)
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
        Add(source, businessMetric, to, dimensions, ReportFact.Microseconds(business));
    }

    private void Add(Guid source, ReportMetric metric, DateTimeOffset at, ReportDimensions dimensions, long value = 1) =>
        Facts.Add(new ReportFact(source, metric, State.Conversation, at, dimensions, value));
}
