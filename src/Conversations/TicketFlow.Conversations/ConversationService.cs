using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Conversations;

/// <summary>
/// Default <see cref="IConversationService"/>. Existence and revision fences are checked before authorization decisions on stored
/// records (like the contact service and access administrator); mutations that move a conversation's access attributes (team,
/// labels, attributes) also authorize the resulting conversation. Commands without an expected revision re-read and retry when a
/// concurrent writer wins, so concurrent replies all apply with gapless sequences. Never logs bodies, addresses or attribute values.
/// </summary>
public sealed partial class ConversationService : IConversationService
{
    private readonly IConversationStore store;
    private readonly IConversationAttributeDefinitionStore definitions;
    private readonly IAccessEvaluator evaluator;
    private readonly IAccessQueryPlanner planner;
    private readonly IAccessFieldPolicyStore policies;
    private readonly IInboxStore inboxes;
    private readonly ITeamStore teams;
    private readonly IContactStore contacts;
    private readonly ConversationsOptions options;
    private readonly TimeProvider time;
    private readonly ContactRepointer repointer;

    public ConversationService(
        IConversationStore store,
        IConversationAttributeDefinitionStore definitions,
        IAccessEvaluator evaluator,
        IAccessQueryPlanner planner,
        IAccessFieldPolicyStore policies,
        IInboxStore inboxes,
        ITeamStore teams,
        IContactStore contacts,
        IOptions<ConversationsOptions> options,
        TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.teams = teams ?? throw new ArgumentNullException(nameof(teams));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        repointer = new ContactRepointer(store, contacts, this.options.MaximumWriteAttempts, time);
    }

    public async Task<ConversationCommandResult> StartAsync(PrincipalRef actor, StartConversationRequest request, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        var now = Now;
        Conversation conversation;
        MessageEvent? initial = null;
        try
        {
            if (request.InitialMessage is { } draft)
                initial = Incoming(draft, request.Contact, now);
            conversation = new Conversation(request.Id ?? options.ConversationIdFactory(), request.Inbox, request.Contact, ConversationStatus.Open,
                request.Priority, request.Assignee, request.Team, request.Labels ?? [], request.Attributes ?? new Dictionary<string, ConversationAttributeValue>(StringComparer.Ordinal),
                [], request.ChannelThread, now, now, initial is null ? null : now, null, initial is null ? null : now, null, initial is null ? 0 : 1);
        }
        catch (ArgumentException)
        {
            return ConversationRefused.Invalid("The conversation request is invalid or exceeds conversation limits.");
        }
        if (!await PermittedAsync(actor, ConversationPermissions.Create, ConversationAccess.Resource(scope, conversation), cancellationToken).ConfigureAwait(false))
            return ConversationRefused.Forbidden;

        var inboxTask = inboxes.GetAsync(scope, conversation.Inbox, cancellationToken);
        var contactTask = contacts.GetAsync(scope, conversation.Contact, cancellationToken);
        var teamTask = conversation.Team is { } team ? teams.GetAsync(scope, team, cancellationToken) : null;
        var defsTask = conversation.Attributes.Count > 0 ? DefinitionsAsync(scope, cancellationToken) : null;
        await Task.WhenAll(
            teamTask is not null && defsTask is not null ? [inboxTask, contactTask, teamTask, defsTask]
            : teamTask is not null ? [inboxTask, contactTask, teamTask]
            : defsTask is not null ? [inboxTask, contactTask, defsTask]
            : [inboxTask, contactTask]).ConfigureAwait(false);

        var inbox = await inboxTask.ConfigureAwait(false);
        if (inbox is null || !inbox.Inbox.Enabled)
            return ConversationRefused.Invalid("The inbox does not exist or is disabled.");
        var contact = await contactTask.ConfigureAwait(false);
        if (contact is null)
            return ConversationRefused.Invalid("The contact does not exist.");
        if (contact.Contact.MergedInto is not null)
            return ConversationRefused.Invalid("The contact was merged; start the conversation with the surviving contact.");
        if (teamTask is not null && await teamTask.ConfigureAwait(false) is null)
            return ConversationRefused.Invalid("The team does not exist.");
        if (defsTask is not null && ValidateAttributes(conversation.Attributes, await defsTask.ConfigureAwait(false)) is { } invalid)
            return invalid;
        if (request.Id is not null && initial?.ExternalId is { } external
            && await ReplayAsync(actor, scope, conversation.Inbox, external, cancellationToken).ConfigureAwait(false) is { } replay)
            return replay;

        IReadOnlyList<TimelineEvent> events = initial is null ? [] : [Timeline(conversation.Id, 1, now, actor, initial)];
        IReadOnlyList<ConversationEventSummary> summaries = initial is null ? [] : [ConversationEventSummary.From(events[0])];
        var change = new ConversationChange(Guid.CreateVersion7(), scope, conversation.Id, conversation.Inbox, 1, ConversationChangeKind.Started,
            summaries, null, null, now);
        var result = await store.WriteAsync(scope, new ConversationWrite(conversation, null, events, change), cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ConversationWriteApplied applied => await StartedAsync(actor, applied, cancellationToken).ConfigureAwait(false),
            ConversationWriteNotFound => ConversationNotFound.Instance,
            ConversationWriteConflict conflict => new ConversationConflict(conflict.CurrentRevision),
            ConversationDuplicateMessage duplicate => await ReplayAsync(actor, scope, duplicate.Existing, cancellationToken).ConfigureAwait(false),
            ConversationWriteRejected rejected => Rejected(rejected),
        };
    }

    public Task<ConversationCommandResult> AppendIncomingAsync(PrincipalRef actor, ConversationId id, IncomingMessageDraft message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return MutateAsync(actor, id, null, ConversationPermissions.Ingest, checkResulting: false, async (stored, now) =>
        {
            var conversation = stored.Conversation;
            if (conversation.Status.Value is ClosedStatus)
            {
                // A redelivered message is a replay even after the conversation was closed.
                if (message.ExternalId is { } external
                    && await ReplayAsync(actor, stored.Scope, conversation.Inbox, external, cancellationToken).ConfigureAwait(false) is { } replay)
                    return replay;
                return ConversationRefused.Closed;
            }
            MessageEvent incoming;
            try
            {
                incoming = Incoming(message, conversation.Contact, now);
            }
            catch (ArgumentException)
            {
                return ConversationRefused.Invalid("The message is invalid or exceeds message limits.");
            }
            List<ConversationEvent> events = conversation.Status.Value is OpenStatus
                ? [incoming]
                : [incoming, new StatusChanged(conversation.Status, ConversationStatus.Open)];
            return new Plan(conversation.With(status: ConversationStatus.Open, lastActivityAt: Later(conversation, now),
                waitingSince: conversation.WaitingSince ?? now, lastIncomingAt: now), events);
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> ReplyAsync(PrincipalRef actor, ConversationId id, OutgoingMessageDraft message, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Reply, checkResulting: false, (stored, now) =>
        {
            var conversation = stored.Conversation;
            if (conversation.Status.Value is ClosedStatus)
                return Decided(ConversationRefused.Closed);
            MessageEvent outgoing;
            try
            {
                outgoing = new MessageEvent(message.Id ?? options.MessageIdFactory(), MessageDirection.Outgoing, new PrincipalParty(AccessActor.From(actor)),
                    message.Body, message.ContentType, message.Attachments, null, new MessageDelivery(MessageDeliveryState.Pending, now));
            }
            catch (ArgumentException)
            {
                return Decided(ConversationRefused.Invalid("The reply is invalid or exceeds message limits."));
            }
            return Decided(new Plan(conversation.With(lastActivityAt: Later(conversation, now), waitingSince: new Optional<DateTimeOffset?>(null),
                firstReplyAt: conversation.FirstReplyAt ?? now, lastOutgoingAt: now), [outgoing]));
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> AddNoteAsync(PrincipalRef actor, ConversationId id, NoteDraft note, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.NoteWrite, checkResulting: false, (stored, now) =>
        {
            var conversation = stored.Conversation;
            if (conversation.Status.Value is ClosedStatus)
                return Decided(ConversationRefused.Closed);
            NoteEvent created;
            try
            {
                created = new NoteEvent(note.Id ?? options.MessageIdFactory(), note.Body, note.Attachments);
            }
            catch (ArgumentException)
            {
                return Decided(ConversationRefused.Invalid("The note is invalid or exceeds note limits."));
            }
            return Decided(new Plan(conversation.With(lastActivityAt: Later(conversation, now)), [created]));
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> AssignAsync(PrincipalRef actor, ConversationId id, UserId? assignee, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        if (assignee == default(UserId))
            throw new ArgumentException("The assignee must be initialized when supplied.", nameof(assignee));
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Assign, checkResulting: false, (stored, now) =>
        {
            var conversation = stored.Conversation;
            if (conversation.Assignee == assignee)
                return Decided(Unchanged(stored));
            return Decided(new Plan(conversation.With(assignee: new Optional<UserId?>(assignee)), [new AssignmentChanged(conversation.Assignee, assignee)]));
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> SetTeamAsync(PrincipalRef actor, ConversationId id, TeamId? team, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        if (team == default(TeamId))
            throw new ArgumentException("The team must be initialized when supplied.", nameof(team));
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Assign, checkResulting: true, async (stored, now) =>
        {
            var conversation = stored.Conversation;
            if (conversation.Team == team)
                return Unchanged(stored);
            if (team is { } value && await teams.GetAsync(stored.Scope, value, cancellationToken).ConfigureAwait(false) is null)
                return ConversationRefused.Invalid("The team does not exist.");
            return new Plan(conversation.With(team: new Optional<TeamId?>(team)), [new TeamChanged(conversation.Team, team)]);
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> ChangeStatusAsync(PrincipalRef actor, ConversationId id, ConversationStatus status, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        if (status.Value is null)
            throw new ArgumentException("A status is required.", nameof(status));
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Resolve, checkResulting: false, (stored, now) =>
        {
            var conversation = stored.Conversation;
            if (conversation.Status.Value is ClosedStatus)
                return Decided(ConversationRefused.Closed);
            if (status.SnoozedUntil is { } until && until <= now)
                return Decided(ConversationRefused.Invalid("A snooze must end in the future."));
            if (Equals(conversation.Status.Value, status.Value))
                return Decided(Unchanged(stored));
            var stopsWaiting = status.Value is ResolvedStatus or ClosedStatus;
            return Decided(new Plan(conversation.With(status: status, lastActivityAt: Later(conversation, now),
                waitingSince: stopsWaiting ? new Optional<DateTimeOffset?>(null) : default), [new StatusChanged(conversation.Status, status)]));
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> SnoozeAsync(PrincipalRef actor, ConversationId id, DateTimeOffset until, long? expectedRevision = null, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(actor, id, ConversationStatus.Snoozed(until), expectedRevision, cancellationToken);

    public Task<ConversationCommandResult> SetPriorityAsync(PrincipalRef actor, ConversationId id, ConversationPriority priority, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(priority))
            throw new ArgumentOutOfRangeException(nameof(priority));
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Manage, checkResulting: false, (stored, now) =>
        {
            var conversation = stored.Conversation;
            return Decided(conversation.Priority == priority
                ? Unchanged(stored)
                : new Plan(conversation.With(priority: priority), [new PriorityChanged(conversation.Priority, priority)]));
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> UpdateLabelsAsync(PrincipalRef actor, ConversationId id, IEnumerable<LabelId> add, IEnumerable<LabelId> remove, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        IReadOnlyList<LabelId> adds = [.. add];
        IReadOnlyList<LabelId> removes = [.. remove];
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Manage, checkResulting: true, (stored, now) =>
        {
            if (adds.Count + removes.Count > 2 * Conversation.MaximumLabels)
                return Decided(ConversationRefused.Invalid("Label changes must be initialized, bounded and disjoint."));
            var addSet = new HashSet<LabelId>(adds.Count);
            foreach (var label in adds)
            {
                if (label == default)
                    return Decided(ConversationRefused.Invalid("Label changes must be initialized, bounded and disjoint."));
                addSet.Add(label);
            }
            var removeSet = new HashSet<LabelId>(removes.Count);
            foreach (var label in removes)
            {
                if (label == default || addSet.Contains(label))
                    return Decided(ConversationRefused.Invalid("Label changes must be initialized, bounded and disjoint."));
                removeSet.Add(label);
            }
            var conversation = stored.Conversation;
            var currentSet = conversation.Labels.Count > 0 ? new HashSet<LabelId>(conversation.Labels) : null;
            var added = new List<LabelId>(addSet.Count);
            foreach (var label in addSet)
                if (currentSet is null || !currentSet.Contains(label))
                    added.Add(label);
            var removed = new List<LabelId>(removeSet.Count);
            if (currentSet is not null)
                foreach (var label in removeSet)
                    if (currentSet.Contains(label))
                        removed.Add(label);
            if (added.Count + removed.Count == 0)
                return Decided(Unchanged(stored));
            var labels = new List<LabelId>(conversation.Labels.Count - removed.Count + added.Count);
            foreach (var label in conversation.Labels)
                if (!removeSet.Contains(label))
                    labels.Add(label);
            labels.AddRange(added);
            if (labels.Count > Conversation.MaximumLabels)
                return Decided(new ConversationRefused(ConversationRefusal.Rejected, [$"A conversation carries at most {Conversation.MaximumLabels} labels."]));
            return Decided(new Plan(conversation.With(labels: labels), [new LabelsChanged(added, removed)]));
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> UpdateAttributesAsync(PrincipalRef actor, ConversationId id, IReadOnlyDictionary<string, ConversationAttributeValue?> changes, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var snapshot = changes.ToList();
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Manage, checkResulting: true, async (stored, now) =>
        {
            if (snapshot.Count is 0 or > ConversationWrite.MaximumEvents)
                return ConversationRefused.Invalid($"An update changes 1-{ConversationWrite.MaximumEvents} attributes.");
            var conversation = stored.Conversation;
            var definitionMap = await DefinitionsAsync(stored.Scope, cancellationToken).ConfigureAwait(false);
            var attributes = new Dictionary<string, ConversationAttributeValue>(conversation.Attributes, StringComparer.Ordinal);
            var events = new List<ConversationEvent>(snapshot.Count);
            snapshot.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
            foreach (var (key, value) in snapshot)
            {
                if (!ConversationAttributeDefinition.IsValidKey(key))
                    return ConversationRefused.Invalid("Attribute keys must be lowercase tokens.");
                var previous = attributes.TryGetValue(key, out var existing) ? existing : (ConversationAttributeValue?)null;
                if (value is { } present)
                {
                    if (!definitionMap.TryGetValue(key, out var definition))
                        return ConversationRefused.Invalid($"Attribute '{key}' is not defined.");
                    if (!definition.Accepts(present))
                        return ConversationRefused.Invalid($"Attribute '{key}' requires a {definition.Type} value.");
                    if (previous is { } old && Equals(old.Value, present.Value))
                        continue;
                    attributes[key] = present;
                }
                else if (!attributes.Remove(key))
                    continue;
                events.Add(new AttributeChanged(key, previous, value));
            }
            if (events.Count == 0)
                return Unchanged(stored);
            try
            {
                return new Plan(conversation.With(attributes: attributes), events, definitionMap);
            }
            catch (ArgumentException)
            {
                return new ConversationRefused(ConversationRefusal.Rejected, [$"A conversation carries at most {Conversation.MaximumAttributes} attributes."]);
            }
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> UpdateParticipantsAsync(PrincipalRef actor, ConversationId id, IEnumerable<UserId> add, IEnumerable<UserId> remove, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        var addSet = new HashSet<UserId>(add);
        var removeSet = new HashSet<UserId>(remove);
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Manage, checkResulting: false, (stored, now) =>
        {
            foreach (var user in addSet)
                if (user == default || removeSet.Contains(user))
                    return Decided(ConversationRefused.Invalid("Participant changes must be initialized and disjoint."));
            foreach (var user in removeSet)
                if (user == default)
                    return Decided(ConversationRefused.Invalid("Participant changes must be initialized and disjoint."));
            var conversation = stored.Conversation;
            var currentSet = conversation.Participants.Count > 0 ? new HashSet<UserId>(conversation.Participants) : null;
            var events = new List<ConversationEvent>(addSet.Count + removeSet.Count);
            foreach (var user in addSet)
                if (currentSet is null || !currentSet.Contains(user))
                    events.Add(new ParticipantChanged(user, ParticipantChange.Added));
            if (currentSet is not null)
                foreach (var user in removeSet)
                    if (currentSet.Contains(user))
                        events.Add(new ParticipantChanged(user, ParticipantChange.Removed));
            if (events.Count == 0)
                return Decided(Unchanged(stored));
            if (events.Count > ConversationWrite.MaximumEvents)
                return Decided(ConversationRefused.Invalid($"An update changes at most {ConversationWrite.MaximumEvents} participants."));
            var participants = new List<UserId>(conversation.Participants.Count + addSet.Count);
            foreach (var user in conversation.Participants)
                if (!removeSet.Contains(user))
                    participants.Add(user);
            foreach (var user in addSet)
                if (currentSet is null || !currentSet.Contains(user))
                    participants.Add(user);
            if (participants.Count > Conversation.MaximumParticipants)
                return Decided(new ConversationRefused(ConversationRefusal.Rejected, [$"A conversation has at most {Conversation.MaximumParticipants} participants."]));
            return Decided(new Plan(conversation.With(participants: participants), events));
        }, cancellationToken);
    }

    public Task<ConversationCommandResult> AppendExtensionAsync(PrincipalRef actor, ConversationId id, ExtensionEvent extension, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return MutateAsync(actor, id, expectedRevision, ConversationPermissions.Manage, checkResulting: false,
            (stored, now) => Decided(new Plan(stored.Conversation, [extension])), cancellationToken);
    }

    public async Task<ConversationCommandResult> DeleteAsync(PrincipalRef actor, ConversationId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ConversationNotFound.Instance;
        if (!await PermittedAsync(actor, ConversationPermissions.Delete, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false))
            return ConversationRefused.Forbidden;
        if (stored.Revision != expectedRevision)
            return new ConversationConflict(stored.Revision);
        var change = new ConversationChange(Guid.CreateVersion7(), scope, id, stored.Conversation.Inbox, stored.Revision, ConversationChangeKind.Deleted, [], null, null, Now);
        return await store.DeleteAsync(scope, id, expectedRevision, change, cancellationToken).ConfigureAwait(false) switch
        {
            ConversationWriteApplied applied => new ConversationApplied(id, applied.Conversation.Revision, []),
            ConversationWriteNotFound => ConversationNotFound.Instance,
            ConversationWriteConflict conflict => new ConversationConflict(conflict.CurrentRevision),
            ConversationDuplicateMessage => throw new InvalidOperationException("A delete cannot record a duplicate message."),
            ConversationWriteRejected rejected => Rejected(rejected),
        };
    }

    public async Task<ConversationCommandResult> UpdateDeliveryStatusAsync(PrincipalRef actor, DeliveryStatusUpdate update, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(update);
        StoredMessage? message = null;
        StoredConversation? stored = null;
        for (var attempt = 1; ; attempt++)
        {
            message ??= update.Conversation is { } conversationId
                ? await store.GetMessageAsync(scope, conversationId, update.Message!.Value, cancellationToken).ConfigureAwait(false)
                : await store.FindMessageByExternalIdAsync(scope, update.Inbox!.Value, update.ExternalId!, cancellationToken).ConfigureAwait(false);
            if (message is null)
                return ConversationNotFound.Instance;
            if (stored is null)
            {
                stored = await store.GetAsync(scope, message.Conversation, cancellationToken).ConfigureAwait(false);
                if (stored is null)
                    return ConversationNotFound.Instance;
                if (!await PermittedAsync(actor, ConversationPermissions.Ingest, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false))
                    return ConversationRefused.Forbidden;
            }
            if (message.Direction != MessageDirection.Outgoing)
                return ConversationRefused.Invalid("Delivery reports apply to outgoing messages.");
            if (update.ProviderMessageId is { } provider && message.ExternalId is { } recorded && !string.Equals(provider, recorded, StringComparison.Ordinal))
                return ConversationRefused.Invalid("The message already has a different provider message identifier.");
            var externalId = message.ExternalId is null ? update.ProviderMessageId : null;
            var advance = MessageDelivery.CanAdvance(message.Delivery.State, update.State);
            if (!advance && externalId is null)
                return new ConversationApplied(message.Conversation, stored.Revision, [], Replayed: true);
            var now = Now;
            var delivery = advance ? new MessageDelivery(update.State, now, update.FailureCode) : message.Delivery;
            var change = new ConversationChange(Guid.CreateVersion7(), scope, message.Conversation, message.Inbox, stored.Revision, ConversationChangeKind.DeliveryUpdated,
                [], message.Message, delivery.State, now);
            var result = await store.UpdateDeliveryAsync(scope, new MessageDeliveryWrite(message.Conversation, message.Message, message.DeliveryRevision, delivery,
                externalId, change), cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case MessageDeliveryApplied:
                    return new ConversationApplied(message.Conversation, stored.Revision, []);
                case ConversationWriteNotFound:
                    return ConversationNotFound.Instance;
                case MessageDeliveryConflict conflict when attempt < options.MaximumWriteAttempts:
                    message = conflict.Current;
                    continue;
                case MessageDeliveryConflict:
                    return new ConversationRefused(ConversationRefusal.Rejected, ["Concurrent delivery updates kept winning; retry."]);
                case ConversationDuplicateMessage:
                    return ConversationRefused.Invalid("The provider message identifier is already recorded in the inbox.");
            }
        }
    }

    /// <summary>The planned next snapshot (head and revision are set by the pipeline) and the events to append.</summary>
    private sealed record Plan(Conversation Next, IReadOnlyList<ConversationEvent> Events, IReadOnlyDictionary<string, ConversationAttributeDefinition>? Definitions = null);

    /// <summary>A command decision: a plan, or a final outcome (refusal, no-op or replay).</summary>
    private readonly record struct Decision(Plan? Plan, ConversationCommandResult? Outcome)
    {
        public static implicit operator Decision(Plan plan) => new(plan, null);

        public static implicit operator Decision(ConversationRefused refused) => new(null, refused);

        public static implicit operator Decision(ConversationApplied applied) => new(null, applied);

        public static implicit operator Decision(ConversationCommandResult result) => new(null, result);
    }

    private static ValueTask<Decision> Decided(Decision decision) => ValueTask.FromResult(decision);

    /// <summary>
    /// Read → authorize → fence → decide → (authorize result) → revision-fenced atomic write. Without an expected revision, conflicts
    /// re-run the pipeline on the newer snapshot up to <see cref="ConversationsOptions.MaximumWriteAttempts"/> times.
    /// </summary>
    private async Task<ConversationCommandResult> MutateAsync(PrincipalRef actor, ConversationId id, long? expectedRevision, Permission permission, bool checkResulting,
        Func<StoredConversation, DateTimeOffset, ValueTask<Decision>> decide, CancellationToken cancellationToken)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        IAccessDecisionContext? accessContext = null;
        for (var attempt = 1; ; attempt++)
        {
            var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
            if (stored is null)
                return ConversationNotFound.Instance;
            if (checkResulting)
            {
                accessContext ??= await evaluator.LoadDecisionContextAsync(actor, scope, [permission], cancellationToken).ConfigureAwait(false);
                if (!(await accessContext.DecideAsync(permission, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false)).IsPermitted)
                    return ConversationRefused.Forbidden;
            }
            else if (!await PermittedAsync(actor, permission, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false))
                return ConversationRefused.Forbidden;
            if (expectedRevision is { } expected && stored.Revision != expected)
                return new ConversationConflict(stored.Revision);
            var now = Now;
            var decision = await decide(stored, now).ConfigureAwait(false);
            if (decision.Outcome is { } outcome)
                return outcome;
            var plan = decision.Plan!;
            if (checkResulting && !(await accessContext!.DecideAsync(permission, ConversationAccess.Resource(scope, plan.Next), cancellationToken).ConfigureAwait(false)).IsPermitted)
                return ConversationRefused.Forbidden;

            var head = stored.Conversation.LastSequence;
            var eventCount = plan.Events.Count;
            var events = new TimelineEvent[eventCount];
            var summaries = new ConversationEventSummary[eventCount];
            var accessActor = AccessActor.From(actor);
            for (var i = 0; i < eventCount; i++)
            {
                var te = new TimelineEvent(id, head + i + 1, Guid.CreateVersion7(), now, accessActor, plan.Events[i]);
                events[i] = te;
                summaries[i] = ConversationEventSummary.From(te);
            }
            var next = plan.Next.With(lastSequence: head + eventCount);
            var change = new ConversationChange(Guid.CreateVersion7(), scope, id, next.Inbox, stored.Revision + 1, ConversationChangeKind.EventsAppended,
                summaries, null, null, now);
            var result = await store.WriteAsync(scope, new ConversationWrite(next, stored.Revision, events, change), cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case ConversationWriteApplied applied:
                    return new ConversationApplied(id, applied.Conversation.Revision,
                        await ProjectForActorAsync(actor, applied.Conversation, applied.Events, cancellationToken, plan.Definitions).ConfigureAwait(false));
                case ConversationWriteNotFound:
                    return ConversationNotFound.Instance;
                case ConversationWriteConflict when expectedRevision is null && attempt < options.MaximumWriteAttempts:
                    continue;
                case ConversationWriteConflict conflict when expectedRevision is null:
                    return new ConversationRefused(ConversationRefusal.Rejected, [$"Concurrent writers kept winning; retry. Current revision {conflict.CurrentRevision}."]);
                case ConversationWriteConflict conflict:
                    return new ConversationConflict(conflict.CurrentRevision);
                case ConversationDuplicateMessage duplicate:
                    return await ReplayAsync(actor, scope, duplicate.Existing, cancellationToken).ConfigureAwait(false);
                case ConversationWriteRejected rejected:
                    return Rejected(rejected);
            }
        }
    }

    /// <summary>An already recorded inbound message: reported as a replayed success when the actor may ingest into its conversation.</summary>
    private async Task<ConversationCommandResult?> ReplayAsync(PrincipalRef actor, TicketScope scope, InboxId inbox, string externalId, CancellationToken cancellationToken)
    {
        var existing = await store.FindMessageByExternalIdAsync(scope, inbox, externalId, cancellationToken).ConfigureAwait(false);
        return existing is null ? (ConversationCommandResult?)null : await ReplayAsync(actor, scope, existing, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ConversationCommandResult> ReplayAsync(PrincipalRef actor, TicketScope scope, StoredMessage existing, CancellationToken cancellationToken)
    {
        var holder = await store.GetAsync(scope, existing.Conversation, cancellationToken).ConfigureAwait(false);
        if (holder is null)
            return ConversationNotFound.Instance;
        if (!await PermittedAsync(actor, ConversationPermissions.Ingest, ConversationAccess.Resource(holder), cancellationToken).ConfigureAwait(false))
            return ConversationRefused.Forbidden;
        var events = await store.ReadTimelineAsync(scope, existing.Conversation, existing.Sequence - 1, 1, includeNotes: false, cancellationToken).ConfigureAwait(false);
        return new ConversationApplied(existing.Conversation, holder.Revision,
            await ProjectForActorAsync(actor, holder, [.. events.Where(item => item.Sequence == existing.Sequence)], cancellationToken).ConfigureAwait(false), Replayed: true);
    }

    /// <summary>
    /// A started conversation. If its contact was merged between the start's contact check and its write (so the merge follower's walk
    /// may have missed it), the conversation is re-pointed along the merge chain before returning.
    /// </summary>
    private async Task<ConversationCommandResult> StartedAsync(PrincipalRef actor, ConversationWriteApplied applied, CancellationToken cancellationToken)
    {
        var stored = applied.Conversation;
        if ((await contacts.GetAsync(stored.Scope, stored.Conversation.Contact, cancellationToken).ConfigureAwait(false))?.Contact.MergedInto is { } mergedInto)
            stored = await repointer.RepairAsync(stored.Scope, stored.Id, cancellationToken, stored, mergedInto).ConfigureAwait(false) ?? stored;
        return new ConversationApplied(stored.Id, stored.Revision, await ProjectForActorAsync(actor, stored, applied.Events, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Returned events are projected for the actor exactly like timeline reads (masking per its <c>conversation:read</c> decision on the
    /// resulting conversation), so a command never discloses a masked attribute value or channel identifier — including previous
    /// values the actor could not see.
    /// </summary>
    private async Task<IReadOnlyList<TimelineEvent>> ProjectForActorAsync(PrincipalRef actor, StoredConversation stored, IReadOnlyList<TimelineEvent> events,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, ConversationAttributeDefinition>? preloadedDefinitions = null)
    {
        var needsAttributes = false;
        var needsChannel = false;
        for (var i = 0; i < events.Count; i++)
        {
            var value = events[i].Event.Value;
            if (value is AttributeChanged)
                needsAttributes = true;
            else if (value is MessageEvent { ExternalId: not null })
                needsChannel = true;
        }
        if (!needsAttributes && !needsChannel)
            return events;

        AccessDecision decision;
        ConversationMasking masking;
        if (!needsAttributes)
        {
            decision = await DecideAsync(actor, ConversationPermissions.Read, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false);
            masking = ConversationMasking.Empty;
        }
        else
        {
            var decisionTask = DecideAsync(actor, ConversationPermissions.Read, ConversationAccess.Resource(stored), cancellationToken);
            var maskingTask = preloadedDefinitions is not null
                ? ConversationMasking.LoadAsync(stored.Scope, ConversationPermissions.Read, preloadedDefinitions, policies, cancellationToken)
                : MaskingAsync(stored.Scope, cancellationToken);
            await Task.WhenAll(decisionTask, maskingTask).ConfigureAwait(false);
            decision = decisionTask.Result;
            masking = maskingTask.Result;
        }
        var projected = new TimelineEvent[events.Count];
        for (var i = 0; i < events.Count; i++)
            projected[i] = masking.Project(events[i], decision).Event;
        return projected;
    }

    private MessageEvent Incoming(IncomingMessageDraft draft, ContactId contact, DateTimeOffset now) =>
        new(draft.Id ?? options.MessageIdFactory(), MessageDirection.Incoming, new ContactParty(contact), draft.Body, draft.ContentType, draft.Attachments,
            draft.ExternalId, new MessageDelivery(MessageDeliveryState.Received, now));

    private static TimelineEvent Timeline(ConversationId id, long sequence, DateTimeOffset now, PrincipalRef actor, ConversationEvent item) =>
        new(id, sequence, Guid.CreateVersion7(), now, AccessActor.From(actor), item);

    private static ConversationApplied Unchanged(StoredConversation stored) => new(stored.Id, stored.Revision, []);

    private static ConversationRefused Rejected(ConversationWriteRejected rejected) =>
        new(ConversationRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]);

    private static DateTimeOffset Later(Conversation conversation, DateTimeOffset now) => now > conversation.LastActivityAt ? now : conversation.LastActivityAt;

    private DateTimeOffset Now => time.GetUtcNow();

    private static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }

    private static void RequireId(ConversationId id)
    {
        if (id == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(id));
    }

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private Task<AccessDecision> DecideAsync(PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken) =>
        evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken);

    private async Task<IReadOnlyDictionary<string, ConversationAttributeDefinition>> DefinitionsAsync(TicketScope scope, CancellationToken cancellationToken)
    {
        var stored = await definitions.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, ConversationAttributeDefinition>(stored.Count, StringComparer.Ordinal);
        foreach (var item in stored)
            map[item.Definition.Key] = item.Definition;
        return map;
    }

    private static ConversationRefused? ValidateAttributes(IReadOnlyDictionary<string, ConversationAttributeValue> attributes, IReadOnlyDictionary<string, ConversationAttributeDefinition> definitionMap)
    {
        foreach (var (key, value) in attributes)
        {
            if (!definitionMap.TryGetValue(key, out var definition))
                return ConversationRefused.Invalid($"Attribute '{key}' is not defined.");
            if (!definition.Accepts(value))
                return ConversationRefused.Invalid($"Attribute '{key}' requires a {definition.Type} value.");
        }
        return null;
    }
}
