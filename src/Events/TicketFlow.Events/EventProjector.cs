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
public sealed class EventProjector : IConversationOutboxBatchHandler
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
        // Batch-fetch distinct conversations per scope once instead of N individual GetAsync calls.
        var idsByScope = new Dictionary<TicketScope, HashSet<ConversationId>>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i] ?? throw new ArgumentNullException(nameof(entries));
            if (!idsByScope.TryGetValue(entry.Change.Scope, out var set))
                idsByScope[entry.Change.Scope] = set = [];
            set.Add(entry.Change.Conversation);
        }
        var snapshotsByScope = new Dictionary<TicketScope, IReadOnlyDictionary<ConversationId, StoredConversation>>(idsByScope.Count);
        foreach (var (scope, set) in idsByScope)
        {
            var ids = new ConversationId[set.Count];
            set.CopyTo(ids);
            snapshotsByScope[scope] = await conversations.GetManyAsync(scope, ids, cancellationToken).ConfigureAwait(false);
        }
        var byScope = new Dictionary<TicketScope, List<IntegrationEventDraft>>();
        var order = new List<TicketScope>();
        for (var i = 0; i < entries.Count; i++)
        {
            var change = entries[i].Change;
            snapshotsByScope[change.Scope].TryGetValue(change.Conversation, out var stored);
            var drafts = await ProjectCoreAsync(change, stored?.Conversation, cancellationToken).ConfigureAwait(false);
            if (drafts.Count == 0)
                continue;
            if (!byScope.TryGetValue(change.Scope, out var list))
            {
                list = [];
                byScope[change.Scope] = list;
                order.Add(change.Scope);
            }
            list.AddRange(drafts);
        }
        for (var i = 0; i < order.Count; i++)
            await publisher.PublishAsync(order[i], byScope[order[i]], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The drafts for one change (idempotent: equal ids for equal sources).</summary>
    public async Task<IReadOnlyList<IntegrationEventDraft>> ProjectAsync(ConversationChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var snapshot = (await conversations.GetAsync(change.Scope, change.Conversation, cancellationToken).ConfigureAwait(false))?.Conversation;
        return await ProjectCoreAsync(change, snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IntegrationEventDraft>> ProjectCoreAsync(ConversationChange change, Conversation? snapshot, CancellationToken cancellationToken)
    {
        var scope = change.Scope;
        var resource = snapshot is null ? new EventResource(change.Conversation, change.Inbox) : EventMapping.Resource(snapshot);
        var drafts = new List<IntegrationEventDraft>();
        switch (change.Kind)
        {
            case ConversationChangeKind.Started:
                if (snapshot is null)
                    break;   // deleted before projection: only the deleted event remains
                var (history, initial) = await StartedAsync(scope, snapshot, change.Events, cancellationToken).ConfigureAwait(false);
                drafts.Add(Draft(change.Id, change.OccurredAt, null, initial, resource));
                for (var i = 0; i < history.Count; i++)
                    Map(history[i], change.Inbox, resource, drafts);
                break;
            case ConversationChangeKind.EventsAppended:
                var appended = await ReadAsync(scope, change.Conversation, change.Events, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < appended.Count; i++)
                    Map(appended[i], change.Inbox, resource, drafts);
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
        var wanted = new HashSet<long>(summaries.Count);
        long first = long.MaxValue, last = 0;
        for (var i = 0; i < summaries.Count; i++)
        {
            var seq = summaries[i].Sequence;
            wanted.Add(seq);
            if (seq < first)
                first = seq;
            if (seq > last)
                last = seq;
        }
        var count = (int)Math.Min(last - first + 1, IConversationStore.MaximumTimelineRead);
        var events = await conversations.ReadTimelineAsync(scope, id, first - 1, count, includeNotes: true, cancellationToken).ConfigureAwait(false);
        var matched = new List<TimelineEvent>(wanted.Count);
        for (var i = 0; i < events.Count; i++)
            if (wanted.Contains(events[i].Sequence))
                matched.Add(events[i]);
        return matched;
    }

    /// <summary>
    /// Reads the start change's events and reconstructs the conversation as created in one timeline pass, retaining only
    /// <see cref="LabelsChanged"/> events for the reverse pass.
    /// </summary>
    private async Task<(IReadOnlyList<TimelineEvent> History, ConversationCreatedData Initial)> StartedAsync(TicketScope scope, Conversation conversation,
        IReadOnlyList<ConversationEventSummary> summaries, CancellationToken cancellationToken)
    {
        HashSet<long>? wanted = null;
        long createdHead = 0;
        long firstWanted = long.MaxValue;
        for (var i = 0; i < summaries.Count; i++)
        {
            var seq = summaries[i].Sequence;
            (wanted ??= new(summaries.Count)).Add(seq);
            if (seq > createdHead)
                createdHead = seq;
            if (seq < firstWanted)
                firstWanted = seq;
        }

        var assignee = conversation.Assignee;
        var team = conversation.Team;
        var priority = conversation.Priority;
        var status = conversation.Status.Kind;
        var labels = new HashSet<LabelId>(conversation.Labels);
        var needReplay = conversation.LastSequence > createdHead && conversation.LastSequence - createdHead <= options.MaximumReplayEvents;
        var history = new List<TimelineEvent>(summaries.Count);

        if (!needReplay)
        {
            if (wanted is not null)
            {
                var count = (int)Math.Min(createdHead - firstWanted + 1, IConversationStore.MaximumTimelineRead);
                var events = await conversations.ReadTimelineAsync(scope, conversation.Id, firstWanted - 1, count, includeNotes: true, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < events.Count; i++)
                    if (wanted.Contains(events[i].Sequence))
                        history.Add(events[i]);
            }
        }
        else
        {
            bool assigneeSeen = false, teamSeen = false, prioritySeen = false, statusSeen = false;
            List<LabelsChanged>? labelChanges = null;
            var after = wanted is not null ? firstWanted - 1 : createdHead;
            while (after < conversation.LastSequence)
            {
                var page = await conversations.ReadTimelineAsync(scope, conversation.Id, after, IConversationStore.MaximumTimelineRead, includeNotes: true, cancellationToken)
                    .ConfigureAwait(false);
                if (page.Count == 0 || page[^1].Sequence <= after)
                    break;
                for (var i = 0; i < page.Count; i++)
                {
                    var timelineEvent = page[i];
                    var seq = timelineEvent.Sequence;
                    if (seq <= after || seq > conversation.LastSequence)
                        continue;
                    if (wanted is not null && seq <= createdHead && wanted.Contains(seq))
                        history.Add(timelineEvent);
                    if (seq > createdHead)
                    {
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
                            case LabelsChanged change:
                                (labelChanges ??= []).Add(change);
                                break;
                        }
                    }
                }
                after = page[^1].Sequence;
            }
            if (labelChanges is not null)
            {
                for (var index = labelChanges.Count - 1; index >= 0; index--)
                {
                    var change = labelChanges[index];
                    labels.ExceptWith(change.Added);
                    labels.UnionWith(change.Removed);
                }
            }
        }

        var channel = conversation.ChannelThread?.Channel is { } kind && EventText.IsToken(kind, 64) ? kind : null;
        var initial = new ConversationCreatedData(conversation.Id, conversation.Inbox, conversation.Contact, assignee, team, EventMapping.Status(status), EventMapping.Priority(priority),
            labels.OrderBy(label => label.Value, StringComparer.Ordinal), channel);
        return (history, initial);
    }
}
