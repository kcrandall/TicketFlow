using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Routing;
using TicketFlow.Surveys;

namespace TicketFlow.Events;

/// <summary>
/// Follows the durable contact change log (<see cref="IContactStore.ReadChangesAsync"/>) into contact events. The position is a
/// compare-and-set checkpoint (<see cref="CheckpointName"/>) in the <see cref="IEventLog"/> store; event ids derive from the change ids, so
/// a lost checkpoint race or a crash between append and checkpoint never duplicates events. Hosts call <see cref="CatchUpAsync"/> per
/// workspace in their loop (E11). A follower started on an existing workspace begins at sequence 0 (every retained change).
/// </summary>
public sealed class EventContactFollower(IContactStore contacts, IEventLog log, IEventPublisher publisher)
{
    public const string CheckpointName = "contacts";
    public const int MaximumBatch = IContactStore.MaximumChangesPerRead;

    private readonly IContactStore contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
    private readonly IEventLog log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly IEventPublisher publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

    /// <summary>Projects up to <paramref name="limit"/> contact changes of <paramref name="scope"/>; returns the number consumed.</summary>
    public async Task<int> CatchUpAsync(TicketScope scope, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumBatch);
        var checkpoint = await log.GetCheckpointAsync(scope, CheckpointName, cancellationToken).ConfigureAwait(false);
        var changes = await contacts.ReadChangesAsync(scope, checkpoint ?? 0, limit, cancellationToken).ConfigureAwait(false);
        if (changes.Count == 0)
            return 0;
        var drafts = new List<IntegrationEventDraft>();
        foreach (var change in changes)
            if (Map(change.Event) is { } data)
                drafts.Add(new IntegrationEventDraft(new IntegrationEvent(EventText.EventId(change.Event.Id, data.Type), change.Event.OccurredAt, null, data),
                    new EventResource(contact: change.Event.Contact)));
        await publisher.PublishAsync(scope, drafts, cancellationToken).ConfigureAwait(false);
        // A lost race means another follower advanced over the same changes (appends are idempotent by event id).
        _ = await log.SetCheckpointAsync(scope, CheckpointName, changes[^1].Sequence, checkpoint, cancellationToken).ConfigureAwait(false);
        return changes.Count;
    }

    private static IntegrationEventData? Map(ContactChangeDraft change) => change.Kind switch
    {
        ContactChangeKind.Created => new ContactCreatedData(change.Contact),
        ContactChangeKind.Updated => new ContactUpdatedData(change.Contact),
        ContactChangeKind.Deleted => new ContactDeletedData(change.Contact),
        ContactChangeKind.Merged when change.Related is { } target && change.Merge is { } merge => new ContactMergedData(change.Contact, target, merge),
        ContactChangeKind.Unmerged when change.Related is { } target && change.Merge is { } merge => new ContactUnmergedData(change.Contact, target, merge),
        _ => null,   // kinds added later by the contacts feature are skipped until mapped
    };
}

/// <summary>
/// Follows recorded survey responses into <c>ticketflow.survey.responded.v1</c> events. Each run reads the window
/// [checkpoint − <see cref="EventsOptions.SurveyOverlap"/>, checkpoint + <see cref="EventsOptions.SurveySlice"/>) (capped at now) through
/// the trusted <see cref="ISurveyStore"/> and advances the checkpoint (<see cref="CheckpointName"/>, microseconds since the Unix epoch) to
/// the window end; re-read responses are deduplicated by event id (derived from the invitation). The first run starts at the current
/// time. Hosts call <see cref="CatchUpAsync"/> per workspace in their loop.
/// </summary>
public sealed class EventSurveyFollower
{
    public const string CheckpointName = "surveys";

    private readonly ISurveyStore surveys;
    private readonly IEventLog log;
    private readonly IEventPublisher publisher;
    private readonly EventsOptions options;
    private readonly TimeProvider time;

    public EventSurveyFollower(ISurveyStore surveys, IEventLog log, IEventPublisher publisher, IOptions<EventsOptions> options, TimeProvider time)
    {
        this.surveys = surveys ?? throw new ArgumentNullException(nameof(surveys));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Projects one window of responses of <paramref name="scope"/>; returns the number of new events.</summary>
    public async Task<int> CatchUpAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var now = EventText.Instant(time.GetUtcNow());
        var checkpoint = await log.GetCheckpointAsync(scope, CheckpointName, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            _ = await log.SetCheckpointAsync(scope, CheckpointName, Microseconds(now), null, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        var position = DateTimeOffset.UnixEpoch.AddTicks(checkpoint.Value * 10);
        var from = position - options.SurveyOverlap;
        var to = position + options.SurveySlice < now ? position + options.SurveySlice : now;
        if (to <= from)
            return 0;
        var drafts = new List<IntegrationEventDraft>();
        SurveyResponseCursor? after = null;
        do
        {
            var page = await surveys.ListResponsesAsync(scope, new SurveyResponseQuery(from, to, limit: SurveyLimits.MaximumPageSize, after: after), AllowAllFilter.Instance,
                cancellationToken).ConfigureAwait(false);
            foreach (var stored in page.Items)
                drafts.Add(Draft(stored.Response));
            if (drafts.Count > options.MaximumSurveyResponsesPerRun)
                throw new InvalidOperationException("The survey window holds more responses than one run may read; shorten SurveySlice.");
            after = page.Next;
        }
        while (after is not null);
        drafts.Reverse();   // pages are newest first; append oldest first
        var appended = await publisher.PublishAsync(scope, drafts, cancellationToken).ConfigureAwait(false);
        _ = await log.SetCheckpointAsync(scope, CheckpointName, Math.Max(checkpoint.Value, Microseconds(to)), checkpoint, cancellationToken).ConfigureAwait(false);
        return appended.Appended.Count;
    }

    private static long Microseconds(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    private static IntegrationEventDraft Draft(SurveyResponse response)
    {
        var data = new SurveyRespondedData(response.Conversation, response.Contact, response.Survey.Value, response.Invitation.Value.ToString("D"), response.Inbox, response.Team, response.Agent,
            EventMapping.Survey(response.Kind), response.Score, response.RespondedAt);
        return new IntegrationEventDraft(new IntegrationEvent(EventText.EventId(response.Invitation.Value, data.Type), response.RespondedAt, null, data),
            new EventResource(response.Conversation, response.Inbox, response.Team, response.Agent, response.Contact));
    }
}

/// <summary>
/// Maps routing events (an <see cref="IRoutingEventObserver"/>) to <c>ticketflow.routing.assigned|overflowed|queue-wait-exceeded.v1</c>.
/// Routing observers are best-effort in-process notifications: an event is lost when this process fails before the append (the conversation
/// outbox still yields the resulting <c>conversation.assigned</c>/<c>team-changed</c> events durably). Agent status, presence and reply-lock
/// events are not exported.
/// </summary>
public sealed class EventRoutingObserver(IConversationStore conversations, IEventPublisher publisher) : IRoutingEventObserver
{
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IEventPublisher publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));

    public async Task OnRoutingEventAsync(RoutingEvent routingEvent, CancellationToken cancellationToken = default)
    {
        var (scope, conversation, at, source) = routingEvent switch
        {
            ConversationRouted routed => (routed.Scope, routed.Conversation, routed.At, $"routed\n{routed.Conversation.Value}\n{routed.Revision}"),
            ConversationOverflowed overflowed => (overflowed.Scope, overflowed.Conversation, overflowed.At, $"overflowed\n{overflowed.Conversation.Value}\n{overflowed.At.UtcTicks}"),
            QueueWaitExceeded exceeded => (exceeded.Scope, exceeded.Conversation, exceeded.At, $"wait\n{exceeded.Conversation.Value}\n{exceeded.At.UtcTicks}"),
            AgentStatusChanged or ConversationPresenceChanged or ReplyLockChanged => default((TicketScope?, ConversationId, DateTimeOffset, string?)),
        };
        if (scope is null || source is null)
            return;
        var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return;
        var inbox = stored.Conversation.Inbox;
        IntegrationEventData data = routingEvent switch
        {
            ConversationRouted routed => new RoutingAssignedData(conversation, inbox, routed.Agent, routed.Revision),
            ConversationOverflowed overflowed => overflowed.Target switch
            {
                OverflowToTeam team => new RoutingOverflowedData(conversation, inbox, team.Team, null),
                OverflowToInbox target => new RoutingOverflowedData(conversation, inbox, null, target.Inbox),
            },
            QueueWaitExceeded exceeded => new RoutingQueueWaitExceededData(conversation, inbox, exceeded.TargetInbox),
            AgentStatusChanged or ConversationPresenceChanged or ReplyLockChanged => throw new InvalidOperationException("Unexpected routing event."),
        };
        await publisher.PublishAsync(scope, [new IntegrationEventDraft(new IntegrationEvent(EventText.EventId(source, data.Type), at, null, data),
            EventMapping.Resource(stored.Conversation))], cancellationToken).ConfigureAwait(false);
    }
}
