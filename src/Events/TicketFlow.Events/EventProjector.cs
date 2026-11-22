using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;

namespace TicketFlow.Events;

/// <summary>
/// Projects conversation outbox changes into integration events (an <see cref="IConversationOutboxHandler"/>, consumer
/// <c>events</c>). Each change's timeline events are read through the trusted <see cref="IConversationStore"/> and mapped; every event
/// id is derived from its source (timeline event id or outbox change id) and type, so redelivered changes append nothing twice. Message
/// and note events carry identifiers and metadata only. <c>sla.*</c> extension events become <c>ticketflow.sla.*</c> only when appended by
/// <see cref="EventsOptions.SlaWorkerClientId"/>. The resource snapshot is the conversation's state when projected. Events of a
/// change are appended in timeline order, and the outbox delivers a conversation's changes in order, so each conversation's events are
/// ordered in the log.
/// </summary>
public sealed class EventProjector : IConversationOutboxHandler
{
    private readonly IConversationStore conversations;
    private readonly IEventPublisher publisher;
    private readonly EventsOptions options;
    private readonly AccessActor slaWorker;

    public EventProjector(IConversationStore conversations, IEventPublisher publisher, IOptions<EventsOptions> options)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        slaWorker = new AccessActor(AccessActorKind.ApiClient, this.options.SlaWorkerClientId.Value);
    }

    public string Consumer => EventsOptions.Consumer;

    public async Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var drafts = await ProjectAsync(entry.Change, cancellationToken).ConfigureAwait(false);
        if (drafts.Count > 0)
            await publisher.PublishAsync(entry.Change.Scope, drafts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The drafts for one change (idempotent: equal ids for equal sources).</summary>
    public async Task<IReadOnlyList<IntegrationEventDraft>> ProjectAsync(ConversationChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var scope = change.Scope;
        var snapshot = (await conversations.GetAsync(scope, change.Conversation, cancellationToken).ConfigureAwait(false))?.Conversation;
        var resource = snapshot is null ? new EventResource(change.Conversation, change.Inbox) : EventMapping.Resource(snapshot);
        var drafts = new List<IntegrationEventDraft>();
        switch (change.Kind)
        {
            case ConversationChangeKind.Started:
                if (snapshot is null)
                    break;   // deleted before projection: only the deleted event remains
                var history = await ReadAsync(scope, change.Conversation, change.Events, cancellationToken).ConfigureAwait(false);
                var initial = await InitialAsync(scope, snapshot, change.Events.Count == 0 ? 0 : change.Events.Max(summary => summary.Sequence), cancellationToken).ConfigureAwait(false);
                drafts.Add(Draft(change.Id, change.OccurredAt, null, initial, resource));
                foreach (var timelineEvent in history)
                    Map(timelineEvent, change.Inbox, resource, drafts);
                break;
            case ConversationChangeKind.EventsAppended:
                foreach (var timelineEvent in await ReadAsync(scope, change.Conversation, change.Events, cancellationToken).ConfigureAwait(false))
                    Map(timelineEvent, change.Inbox, resource, drafts);
                break;
            case ConversationChangeKind.DeliveryUpdated:
                drafts.Add(Draft(change.Id, change.OccurredAt, null,
                    new ChannelDeliveryStatusData(change.Conversation, change.Inbox, change.Message!.Value, EventMapping.Delivery(change.Delivery!.Value)), resource));
                break;
            case ConversationChangeKind.Deleted:
                drafts.Add(Draft(change.Id, change.OccurredAt, null, new ConversationDeletedData(change.Conversation, change.Inbox), resource));
                break;
            default:
                throw new InvalidOperationException("Unknown conversation change kind.");
        }
        return drafts;
    }

    private void Map(TimelineEvent timelineEvent, InboxId inbox, EventResource resource, List<IntegrationEventDraft> drafts)
    {
        var conversation = timelineEvent.Conversation;
        var actor = EventMapping.Actor(timelineEvent.Actor);
        void Add(IntegrationEventData data) => drafts.Add(Draft(timelineEvent.Id, timelineEvent.OccurredAt, actor, data, resource));
        switch (timelineEvent.Event)
        {
            case MessageEvent { Direction: MessageDirection.Incoming } message:
                Add(new ConversationMessageReceivedData(conversation, inbox, message.Id, message.Sender.Value is ContactParty party ? party.Contact : resource.Contact!.Value,
                    timelineEvent.Sequence, EventMapping.ContentType(message.ContentType), message.Attachments.Count));
                break;
            case MessageEvent message:
                Add(new ConversationMessageSentData(conversation, inbox, message.Id,
                    message.Sender.Value is PrincipalParty sender ? EventMapping.Actor(sender.Actor) : actor, timelineEvent.Sequence,
                    EventMapping.ContentType(message.ContentType), message.Attachments.Count));
                break;
            case NoteEvent note:
                Add(new ConversationNoteAddedData(conversation, inbox, note.Id, actor));
                break;
            case AssignmentChanged change:
                Add(new ConversationAssignedData(conversation, inbox, change.Previous, change.Current));
                break;
            case TeamChanged change:
                Add(new ConversationTeamChangedData(conversation, inbox, change.Previous, change.Current));
                break;
            case StatusChanged change:
                Add(new ConversationStatusChangedData(conversation, inbox, EventMapping.Status(change.Previous.Kind), EventMapping.Status(change.Current.Kind),
                    change.Current.Value is SnoozedStatus snoozed ? snoozed.Until : null));
                var wasResolved = IsResolved(change.Previous.Kind);
                var isResolved = IsResolved(change.Current.Kind);
                if (isResolved && !wasResolved)
                    Add(new ConversationResolvedData(conversation, inbox, EventMapping.Status(change.Current.Kind)));
                else if (wasResolved && !isResolved)
                    Add(new ConversationReopenedData(conversation, inbox, EventMapping.Status(change.Previous.Kind), EventMapping.Status(change.Current.Kind)));
                break;
            case PriorityChanged change:
                Add(new ConversationPriorityChangedData(conversation, inbox, EventMapping.Priority(change.Previous), EventMapping.Priority(change.Current)));
                break;
            case LabelsChanged change:
                Add(new ConversationLabelsChangedData(conversation, inbox, change.Added, change.Removed));
                break;
            case ExtensionEvent extension:
                // sla.* events are authentic only when appended by the configured SLA worker; forged ones are ignored.
                if (timelineEvent.Actor == slaWorker && SlaEventCodec.TryDecode(extension, out var sla))
                    Add(Sla(conversation, inbox, sla));
                break;
            case AttributeChanged:
            case ParticipantChanged:
            case ContactChanged:
            case SystemEvent:
                break;
        }
    }

    private static IntegrationEventData Sla(ConversationId conversation, InboxId inbox, SlaTimelineEvent sla) => sla switch
    {
        SlaAppliedEvent applied => new SlaAppliedData(conversation, inbox, applied.Policy.Value, applied.Key, EventMapping.Basis(applied.Basis), applied.Calendar?.Value,
            applied.Targets.For(SlaMetric.FirstResponse), applied.Targets.For(SlaMetric.NextResponse), applied.Targets.For(SlaMetric.Resolution), applied.At),
        SlaWarningEvent warning => new SlaWarningData(conversation, inbox, warning.Policy.Value, warning.Key, EventMapping.Metric(warning.Metric), warning.Cycle,
            warning.ThresholdPercent, warning.Target, warning.At, warning.DueAt),
        SlaBreachedEvent breached => new SlaBreachedData(conversation, inbox, breached.Policy.Value, breached.Key, EventMapping.Metric(breached.Metric), breached.Cycle,
            breached.Target, breached.At),
        SlaMetEvent met => new SlaMetData(conversation, inbox, met.Policy.Value, met.Key, EventMapping.Metric(met.Metric), met.Cycle, met.Target, met.Elapsed, met.Breached, met.At),
    };

    private static bool IsResolved(ConversationStatusKind kind) => kind is ConversationStatusKind.Resolved or ConversationStatusKind.Closed;

    private static IntegrationEventDraft Draft(Guid source, DateTimeOffset at, EventActor? actor, IntegrationEventData data, EventResource resource) =>
        new(new IntegrationEvent(EventText.EventId(source, data.Type), at, actor, data), resource);

    /// <summary>The timeline events summarized by a change, in sequence order (missing ones — deleted conversations — are skipped).</summary>
    private async Task<IReadOnlyList<TimelineEvent>> ReadAsync(TicketScope scope, ConversationId id, IReadOnlyList<ConversationEventSummary> summaries, CancellationToken cancellationToken)
    {
        if (summaries.Count == 0)
            return [];
        var wanted = summaries.Select(summary => summary.Sequence).ToHashSet();
        var first = wanted.Min();
        var count = (int)Math.Min(wanted.Max() - first + 1, IConversationStore.MaximumTimelineRead);
        var events = await conversations.ReadTimelineAsync(scope, id, first - 1, count, includeNotes: true, cancellationToken).ConfigureAwait(false);
        return [.. events.Where(item => wanted.Contains(item.Sequence)).OrderBy(item => item.Sequence)];
    }

    /// <summary>
    /// The conversation as created: the snapshot with later assignment, team, priority, status and label changes undone (first previous
    /// values; labels reversed). Timelines longer than <see cref="EventsOptions.MaximumReplayEvents"/> use the snapshot as is.
    /// </summary>
    private async Task<ConversationCreatedData> InitialAsync(TicketScope scope, Conversation conversation, long createdHead, CancellationToken cancellationToken)
    {
        var assignee = conversation.Assignee;
        var team = conversation.Team;
        var priority = conversation.Priority;
        var status = conversation.Status.Kind;
        var labels = new HashSet<LabelId>(conversation.Labels);
        if (conversation.LastSequence > createdHead && conversation.LastSequence - createdHead <= options.MaximumReplayEvents)
        {
            var later = new List<TimelineEvent>();
            var after = createdHead;
            while (after < conversation.LastSequence)
            {
                var page = await conversations.ReadTimelineAsync(scope, conversation.Id, after, IConversationStore.MaximumTimelineRead, includeNotes: true, cancellationToken)
                    .ConfigureAwait(false);
                if (page.Count == 0 || page[^1].Sequence <= after)
                    break;
                later.AddRange(page.Where(item => item.Sequence > after && item.Sequence <= conversation.LastSequence));
                after = page[^1].Sequence;
            }
            bool assigneeSeen = false, teamSeen = false, prioritySeen = false, statusSeen = false;
            foreach (var timelineEvent in later)
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
            for (var index = later.Count - 1; index >= 0; index--)
                if (later[index].Event.Value is LabelsChanged change)
                {
                    labels.ExceptWith(change.Added);
                    labels.UnionWith(change.Removed);
                }
        }
        var channel = conversation.ChannelThread?.Channel is { } kind && EventText.IsToken(kind, 64) ? kind : null;
        return new ConversationCreatedData(conversation.Id, conversation.Inbox, conversation.Contact, assignee, team, EventMapping.Status(status), EventMapping.Priority(priority),
            labels.OrderBy(label => label.Value, StringComparer.Ordinal), channel);
    }
}
