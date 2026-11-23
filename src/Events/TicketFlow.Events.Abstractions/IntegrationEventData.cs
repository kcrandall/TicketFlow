using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>Members shared by every typed integration event payload.</summary>
public interface IIntegrationEventPayload
{
    /// <summary>The CloudEvents <c>type</c> (see <see cref="IntegrationEventTypes"/>).</summary>
    string Type { get; }

    /// <summary>The CloudEvents <c>subject</c>: <c>conversations/&lt;id&gt;</c> or <c>contacts/&lt;id&gt;</c> (identifier URI-escaped).</summary>
    string Subject { get; }
}

/// <summary>An acting principal recorded with an event: kind (<c>user</c>, <c>api-client</c>, <c>automation</c>, <c>system</c>) and identifier.</summary>
public sealed record EventActor
{
    public EventActor(string kind, string id)
    {
        Kind = kind is "user" or "api-client" or "automation" or "system" ? kind : throw new ArgumentException("Unknown actor kind.", nameof(kind));
        Id = TicketScope.RequireIdentifier(id, nameof(id));
    }

    public string Kind { get; }
    public string Id { get; }
}

internal static class EventSubjects
{
    public static string Conversation(ConversationId id) => "conversations/" + Uri.EscapeDataString(id.Value);

    public static string Contact(ContactId id) => "contacts/" + Uri.EscapeDataString(id.Value);

    public static string Token(string value, string name) => EventText.RequireToken(value, 32, name);

    public static string? OptionalToken(string? value, string name) => value is null ? null : EventText.RequireToken(value, 64, name);

    public static IReadOnlyList<LabelId> Labels(IEnumerable<LabelId> labels, string name)
    {
        ArgumentNullException.ThrowIfNull(labels, name);
        LabelId[] snapshot = [.. labels];
        if (snapshot.Length > 128)
            throw new ArgumentException("At most 128 initialized labels.", name);
        for (var i = 0; i < snapshot.Length; i++)
            if (snapshot[i] == default)
                throw new ArgumentException("At most 128 initialized labels.", name);
        return snapshot;
    }

    public static string Identifier(string value, string name) => TicketScope.RequireIdentifier(value, name);

    public static void Require(params ReadOnlySpan<bool> initialized)
    {
        for (var i = 0; i < initialized.Length; i++)
            if (!initialized[i])
                throw new ArgumentException("Event identifiers must be initialized.");
    }
}

/// <summary>A conversation was created. <see cref="Status"/>/<see cref="Priority"/> are the initial values (<c>open</c>, <c>none</c>, …).</summary>
public sealed record ConversationCreatedData : IIntegrationEventPayload
{
    public ConversationCreatedData(ConversationId conversation, InboxId inbox, ContactId contact, UserId? assignee, TeamId? team, string status, string priority,
        IEnumerable<LabelId> labels, string? channel)
    {
        EventSubjects.Require(conversation != default, inbox != default, contact != default, assignee != default(UserId), team != default(TeamId));
        (Conversation, Inbox, Contact, Assignee, Team) = (conversation, inbox, contact, assignee, team);
        Status = EventSubjects.Token(status, nameof(status));
        Priority = EventSubjects.Token(priority, nameof(priority));
        Labels = EventSubjects.Labels(labels, nameof(labels));
        Channel = EventSubjects.OptionalToken(channel, nameof(channel));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public ContactId Contact { get; }
    public UserId? Assignee { get; }
    public TeamId? Team { get; }
    public string Status { get; }
    public string Priority { get; }
    public IReadOnlyList<LabelId> Labels { get; }
    public string? Channel { get; }
    public string Type => IntegrationEventTypes.ConversationCreated;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>The status changed (<c>open|pending|snoozed|resolved|closed</c>); <see cref="SnoozedUntil"/> when the new status is snoozed.</summary>
public sealed record ConversationStatusChangedData : IIntegrationEventPayload
{
    public ConversationStatusChangedData(ConversationId conversation, InboxId inbox, string previous, string current, DateTimeOffset? snoozedUntil = null)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox) = (conversation, inbox);
        Previous = EventSubjects.Token(previous, nameof(previous));
        Current = EventSubjects.Token(current, nameof(current));
        SnoozedUntil = EventText.Instant(snoozedUntil);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Previous { get; }
    public string Current { get; }
    public DateTimeOffset? SnoozedUntil { get; }
    public string Type => IntegrationEventTypes.ConversationStatusChanged;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>The assignee changed (<see langword="null"/> = unassigned).</summary>
public sealed record ConversationAssignedData : IIntegrationEventPayload
{
    public ConversationAssignedData(ConversationId conversation, InboxId inbox, UserId? previous, UserId? current)
    {
        EventSubjects.Require(conversation != default, inbox != default, previous != default(UserId), current != default(UserId));
        (Conversation, Inbox, Previous, Current) = (conversation, inbox, previous, current);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public UserId? Previous { get; }
    public UserId? Current { get; }
    public string Type => IntegrationEventTypes.ConversationAssigned;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>The team changed (<see langword="null"/> = none).</summary>
public sealed record ConversationTeamChangedData : IIntegrationEventPayload
{
    public ConversationTeamChangedData(ConversationId conversation, InboxId inbox, TeamId? previous, TeamId? current)
    {
        EventSubjects.Require(conversation != default, inbox != default, previous != default(TeamId), current != default(TeamId));
        (Conversation, Inbox, Previous, Current) = (conversation, inbox, previous, current);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public TeamId? Previous { get; }
    public TeamId? Current { get; }
    public string Type => IntegrationEventTypes.ConversationTeamChanged;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>The priority changed (<c>none|low|medium|high|urgent</c>).</summary>
public sealed record ConversationPriorityChangedData : IIntegrationEventPayload
{
    public ConversationPriorityChangedData(ConversationId conversation, InboxId inbox, string previous, string current)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox) = (conversation, inbox);
        Previous = EventSubjects.Token(previous, nameof(previous));
        Current = EventSubjects.Token(current, nameof(current));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Previous { get; }
    public string Current { get; }
    public string Type => IntegrationEventTypes.ConversationPriorityChanged;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>Labels were added and/or removed.</summary>
public sealed record ConversationLabelsChangedData : IIntegrationEventPayload
{
    public ConversationLabelsChangedData(ConversationId conversation, InboxId inbox, IEnumerable<LabelId> added, IEnumerable<LabelId> removed)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox) = (conversation, inbox);
        Added = EventSubjects.Labels(added, nameof(added));
        Removed = EventSubjects.Labels(removed, nameof(removed));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public IReadOnlyList<LabelId> Added { get; }
    public IReadOnlyList<LabelId> Removed { get; }
    public string Type => IntegrationEventTypes.ConversationLabelsChanged;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>
/// An incoming message was recorded. Never carries the body: <see cref="ContentType"/> (<c>plain-text|markdown|html</c>) and the
/// attachment count are metadata; <see cref="TimelineSequence"/> locates the message for content hydration.
/// </summary>
public sealed record ConversationMessageReceivedData : IIntegrationEventPayload
{
    public ConversationMessageReceivedData(ConversationId conversation, InboxId inbox, MessageId message, ContactId contact, long timelineSequence, string contentType, int attachmentCount)
    {
        EventSubjects.Require(conversation != default, inbox != default, message != default, contact != default);
        ArgumentOutOfRangeException.ThrowIfLessThan(timelineSequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(attachmentCount);
        (Conversation, Inbox, Message, Contact, TimelineSequence, AttachmentCount) = (conversation, inbox, message, contact, timelineSequence, attachmentCount);
        ContentType = EventSubjects.Token(contentType, nameof(contentType));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public MessageId Message { get; }
    public ContactId Contact { get; }
    public long TimelineSequence { get; }
    public string ContentType { get; }
    public int AttachmentCount { get; }
    public string Type => IntegrationEventTypes.ConversationMessageReceived;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>An outgoing message was recorded (pending dispatch). Never carries the body.</summary>
public sealed record ConversationMessageSentData : IIntegrationEventPayload
{
    public ConversationMessageSentData(ConversationId conversation, InboxId inbox, MessageId message, EventActor sender, long timelineSequence, string contentType, int attachmentCount)
    {
        EventSubjects.Require(conversation != default, inbox != default, message != default);
        ArgumentOutOfRangeException.ThrowIfLessThan(timelineSequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(attachmentCount);
        (Conversation, Inbox, Message, TimelineSequence, AttachmentCount) = (conversation, inbox, message, timelineSequence, attachmentCount);
        Sender = sender ?? throw new ArgumentNullException(nameof(sender));
        ContentType = EventSubjects.Token(contentType, nameof(contentType));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public MessageId Message { get; }
    public EventActor Sender { get; }
    public long TimelineSequence { get; }
    public string ContentType { get; }
    public int AttachmentCount { get; }
    public string Type => IntegrationEventTypes.ConversationMessageSent;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>A private note was added. Identifiers only at every privacy level (note bodies are never exported).</summary>
public sealed record ConversationNoteAddedData : IIntegrationEventPayload
{
    public ConversationNoteAddedData(ConversationId conversation, InboxId inbox, MessageId note, EventActor author)
    {
        EventSubjects.Require(conversation != default, inbox != default, note != default);
        (Conversation, Inbox, Note) = (conversation, inbox, note);
        Author = author ?? throw new ArgumentNullException(nameof(author));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public MessageId Note { get; }
    public EventActor Author { get; }
    public string Type => IntegrationEventTypes.ConversationNoteAdded;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>The conversation became resolved or closed (<see cref="Status"/>) from an unresolved status.</summary>
public sealed record ConversationResolvedData : IIntegrationEventPayload
{
    public ConversationResolvedData(ConversationId conversation, InboxId inbox, string status)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox) = (conversation, inbox);
        Status = EventSubjects.Token(status, nameof(status));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Status { get; }
    public string Type => IntegrationEventTypes.ConversationResolved;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>A resolved or closed conversation became unresolved again.</summary>
public sealed record ConversationReopenedData : IIntegrationEventPayload
{
    public ConversationReopenedData(ConversationId conversation, InboxId inbox, string previous, string current)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox) = (conversation, inbox);
        Previous = EventSubjects.Token(previous, nameof(previous));
        Current = EventSubjects.Token(current, nameof(current));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Previous { get; }
    public string Current { get; }
    public string Type => IntegrationEventTypes.ConversationReopened;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>The conversation and its timeline were deleted.</summary>
public sealed record ConversationDeletedData : IIntegrationEventPayload
{
    public ConversationDeletedData(ConversationId conversation, InboxId inbox)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox) = (conversation, inbox);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Type => IntegrationEventTypes.ConversationDeleted;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>A contact was created.</summary>
public sealed record ContactCreatedData : IIntegrationEventPayload
{
    public ContactCreatedData(ContactId contact)
    {
        EventSubjects.Require(contact != default);
        Contact = contact;
    }

    public ContactId Contact { get; }
    public string Type => IntegrationEventTypes.ContactCreated;
    public string Subject => EventSubjects.Contact(Contact);
}

/// <summary>A contact's profile, attributes, labels, identities or blocked flag changed.</summary>
public sealed record ContactUpdatedData : IIntegrationEventPayload
{
    public ContactUpdatedData(ContactId contact)
    {
        EventSubjects.Require(contact != default);
        Contact = contact;
    }

    public ContactId Contact { get; }
    public string Type => IntegrationEventTypes.ContactUpdated;
    public string Subject => EventSubjects.Contact(Contact);
}

/// <summary>A contact was deleted.</summary>
public sealed record ContactDeletedData : IIntegrationEventPayload
{
    public ContactDeletedData(ContactId contact)
    {
        EventSubjects.Require(contact != default);
        Contact = contact;
    }

    public ContactId Contact { get; }
    public string Type => IntegrationEventTypes.ContactDeleted;
    public string Subject => EventSubjects.Contact(Contact);
}

/// <summary><see cref="Contact"/> (the source, now a tombstone) was merged into <see cref="Target"/>.</summary>
public sealed record ContactMergedData : IIntegrationEventPayload
{
    public ContactMergedData(ContactId contact, ContactId target, Guid merge)
    {
        EventSubjects.Require(contact != default, target != default, merge != Guid.Empty);
        (Contact, Target, Merge) = (contact, target, merge);
    }

    public ContactId Contact { get; }
    public ContactId Target { get; }
    public Guid Merge { get; }
    public string Type => IntegrationEventTypes.ContactMerged;
    public string Subject => EventSubjects.Contact(Contact);
}

/// <summary><see cref="Contact"/> was split back out of <see cref="Target"/> (the merge <see cref="Merge"/> was undone).</summary>
public sealed record ContactUnmergedData : IIntegrationEventPayload
{
    public ContactUnmergedData(ContactId contact, ContactId target, Guid merge)
    {
        EventSubjects.Require(contact != default, target != default, merge != Guid.Empty);
        (Contact, Target, Merge) = (contact, target, merge);
    }

    public ContactId Contact { get; }
    public ContactId Target { get; }
    public Guid Merge { get; }
    public string Type => IntegrationEventTypes.ContactUnmerged;
    public string Subject => EventSubjects.Contact(Contact);
}

/// <summary>
/// An external identity (for example an IdentityFlow subject) was linked to the contact. <see cref="IdentityKind"/>
/// (<c>external-user</c>, <c>email</c>, …) and <see cref="Verified"/> are metadata; the identity value is never part of the payload.
/// </summary>
public sealed record ContactIdentityLinkedData : IIntegrationEventPayload
{
    public ContactIdentityLinkedData(ContactId contact, string identityKind, bool verified)
    {
        EventSubjects.Require(contact != default);
        Contact = contact;
        IdentityKind = EventSubjects.Token(identityKind, nameof(identityKind));
        Verified = verified;
    }

    public ContactId Contact { get; }
    public string IdentityKind { get; }
    public bool Verified { get; }
    public string Type => IntegrationEventTypes.ContactIdentityLinked;
    public string Subject => EventSubjects.Contact(Contact);
}

/// <summary>An SLA rule was applied. Targets are <see langword="null"/> when the rule has none for a metric.</summary>
public sealed record SlaAppliedData : IIntegrationEventPayload
{
    public SlaAppliedData(ConversationId conversation, InboxId inbox, string policy, string key, string basis, string? calendar, TimeSpan? firstResponseTarget,
        TimeSpan? nextResponseTarget, TimeSpan? resolutionTarget, DateTimeOffset at)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox) = (conversation, inbox);
        Policy = EventSubjects.Identifier(policy, nameof(policy));
        Key = EventText.RequireText(key, 128, nameof(key));
        Basis = EventSubjects.Token(basis, nameof(basis));
        Calendar = calendar is null ? null : EventSubjects.Identifier(calendar, nameof(calendar));
        (FirstResponseTarget, NextResponseTarget, ResolutionTarget) = (firstResponseTarget, nextResponseTarget, resolutionTarget);
        At = EventText.Instant(at);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Policy { get; }
    public string Key { get; }
    public string Basis { get; }
    public string? Calendar { get; }
    public TimeSpan? FirstResponseTarget { get; }
    public TimeSpan? NextResponseTarget { get; }
    public TimeSpan? ResolutionTarget { get; }
    public DateTimeOffset At { get; }
    public string Type => IntegrationEventTypes.SlaApplied;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>An SLA metric (<c>first-response|next-response|resolution</c>) crossed a warning threshold.</summary>
public sealed record SlaWarningData : IIntegrationEventPayload
{
    public SlaWarningData(ConversationId conversation, InboxId inbox, string policy, string key, string metric, int cycle, int thresholdPercent, TimeSpan target,
        DateTimeOffset at, DateTimeOffset? dueAt)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox, Cycle, ThresholdPercent, Target) = (conversation, inbox, cycle, thresholdPercent, target);
        Policy = EventSubjects.Identifier(policy, nameof(policy));
        Key = EventText.RequireText(key, 128, nameof(key));
        Metric = EventSubjects.Token(metric, nameof(metric));
        At = EventText.Instant(at);
        DueAt = EventText.Instant(dueAt);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Policy { get; }
    public string Key { get; }
    public string Metric { get; }
    public int Cycle { get; }
    public int ThresholdPercent { get; }
    public TimeSpan Target { get; }
    public DateTimeOffset At { get; }
    public DateTimeOffset? DueAt { get; }
    public string Type => IntegrationEventTypes.SlaWarning;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>An SLA metric breached its target at <see cref="At"/>.</summary>
public sealed record SlaBreachedData : IIntegrationEventPayload
{
    public SlaBreachedData(ConversationId conversation, InboxId inbox, string policy, string key, string metric, int cycle, TimeSpan target, DateTimeOffset at)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox, Cycle, Target) = (conversation, inbox, cycle, target);
        Policy = EventSubjects.Identifier(policy, nameof(policy));
        Key = EventText.RequireText(key, 128, nameof(key));
        Metric = EventSubjects.Token(metric, nameof(metric));
        At = EventText.Instant(at);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Policy { get; }
    public string Key { get; }
    public string Metric { get; }
    public int Cycle { get; }
    public TimeSpan Target { get; }
    public DateTimeOffset At { get; }
    public string Type => IntegrationEventTypes.SlaBreached;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>An SLA metric was satisfied after <see cref="Elapsed"/> measured time (<see cref="Breached"/> when late).</summary>
public sealed record SlaMetData : IIntegrationEventPayload
{
    public SlaMetData(ConversationId conversation, InboxId inbox, string policy, string key, string metric, int cycle, TimeSpan target, TimeSpan elapsed, bool breached,
        DateTimeOffset at)
    {
        EventSubjects.Require(conversation != default, inbox != default);
        (Conversation, Inbox, Cycle, Target, Elapsed, Breached) = (conversation, inbox, cycle, target, elapsed, breached);
        Policy = EventSubjects.Identifier(policy, nameof(policy));
        Key = EventText.RequireText(key, 128, nameof(key));
        Metric = EventSubjects.Token(metric, nameof(metric));
        At = EventText.Instant(at);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string Policy { get; }
    public string Key { get; }
    public string Metric { get; }
    public int Cycle { get; }
    public TimeSpan Target { get; }
    public TimeSpan Elapsed { get; }
    public bool Breached { get; }
    public DateTimeOffset At { get; }
    public string Type => IntegrationEventTypes.SlaMet;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>The routing engine assigned the conversation to <see cref="Agent"/> (conversation revision <see cref="Revision"/>).</summary>
public sealed record RoutingAssignedData : IIntegrationEventPayload
{
    public RoutingAssignedData(ConversationId conversation, InboxId inbox, UserId agent, long revision)
    {
        EventSubjects.Require(conversation != default, inbox != default, agent != default);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        (Conversation, Inbox, Agent, Revision) = (conversation, inbox, agent, revision);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public UserId Agent { get; }
    public long Revision { get; }
    public string Type => IntegrationEventTypes.RoutingAssigned;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>An overflow rule moved a waiting conversation to <see cref="TargetTeam"/> or <see cref="TargetInbox"/> (exactly one).</summary>
public sealed record RoutingOverflowedData : IIntegrationEventPayload
{
    public RoutingOverflowedData(ConversationId conversation, InboxId inbox, TeamId? targetTeam, InboxId? targetInbox)
    {
        EventSubjects.Require(conversation != default, inbox != default, targetTeam != default(TeamId), targetInbox != default(InboxId), (targetTeam is null) != (targetInbox is null));
        (Conversation, Inbox, TargetTeam, TargetInbox) = (conversation, inbox, targetTeam, targetInbox);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public TeamId? TargetTeam { get; }
    public InboxId? TargetInbox { get; }
    public string Type => IntegrationEventTypes.RoutingOverflowed;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>A conversation stayed queued in <see cref="TargetInbox"/> after a routing attempt.</summary>
public sealed record RoutingQueuedData : IIntegrationEventPayload
{
    public RoutingQueuedData(ConversationId conversation, InboxId inbox, InboxId targetInbox, string reason)
    {
        EventSubjects.Require(conversation != default, inbox != default, targetInbox != default);
        (Conversation, Inbox, TargetInbox) = (conversation, inbox, targetInbox);
        Reason = EventSubjects.Token(reason, nameof(reason));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public InboxId TargetInbox { get; }
    public string Reason { get; }
    public string Type => IntegrationEventTypes.RoutingQueued;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>A conversation waited in the <see cref="TargetInbox"/> queue longer than its policy's maximum wait.</summary>
public sealed record RoutingQueueWaitExceededData : IIntegrationEventPayload
{
    public RoutingQueueWaitExceededData(ConversationId conversation, InboxId inbox, InboxId targetInbox)
    {
        EventSubjects.Require(conversation != default, inbox != default, targetInbox != default);
        (Conversation, Inbox, TargetInbox) = (conversation, inbox, targetInbox);
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public InboxId TargetInbox { get; }
    public string Type => IntegrationEventTypes.RoutingQueueWaitExceeded;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>A survey (<see cref="Kind"/> <c>csat|nps</c>) was answered with <see cref="Score"/>; the comment is content.</summary>
public sealed record SurveyRespondedData : IIntegrationEventPayload
{
    public SurveyRespondedData(ConversationId conversation, ContactId contact, string survey, string invitation, InboxId? inbox, TeamId? team, UserId? agent, string kind,
        int score, DateTimeOffset respondedAt)
    {
        EventSubjects.Require(conversation != default, contact != default, inbox != default(InboxId), team != default(TeamId), agent != default(UserId));
        (Conversation, Contact, Inbox, Team, Agent, Score) = (conversation, contact, inbox, team, agent, score);
        Survey = EventSubjects.Identifier(survey, nameof(survey));
        Invitation = EventSubjects.Identifier(invitation, nameof(invitation));
        Kind = EventSubjects.Token(kind, nameof(kind));
        RespondedAt = EventText.Instant(respondedAt);
    }

    public ConversationId Conversation { get; }
    public ContactId Contact { get; }
    public string Survey { get; }
    public string Invitation { get; }
    public InboxId? Inbox { get; }
    public TeamId? Team { get; }
    public UserId? Agent { get; }
    public string Kind { get; }
    public int Score { get; }
    public DateTimeOffset RespondedAt { get; }
    public string Type => IntegrationEventTypes.SurveyResponded;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>An outgoing message's delivery state changed (<c>pending|sent|delivered|read|failed</c>).</summary>
public sealed record ChannelDeliveryStatusData : IIntegrationEventPayload
{
    public ChannelDeliveryStatusData(ConversationId conversation, InboxId inbox, MessageId message, string state)
    {
        EventSubjects.Require(conversation != default, inbox != default, message != default);
        (Conversation, Inbox, Message) = (conversation, inbox, message);
        State = EventSubjects.Token(state, nameof(state));
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public MessageId Message { get; }
    public string State { get; }
    public string Type => IntegrationEventTypes.ChannelDeliveryStatus;
    public string Subject => EventSubjects.Conversation(Conversation);
}

/// <summary>
/// A typed integration event payload. Switch over every case without a discard arm. Persisted only through
/// <see cref="IntegrationEventCodec"/> (the event type is the explicit discriminator).
/// </summary>
public union IntegrationEventData(
    ConversationCreatedData, ConversationStatusChangedData, ConversationAssignedData, ConversationTeamChangedData, ConversationPriorityChangedData,
    ConversationLabelsChangedData, ConversationMessageReceivedData, ConversationMessageSentData, ConversationNoteAddedData, ConversationResolvedData,
    ConversationReopenedData, ConversationDeletedData, ContactCreatedData, ContactUpdatedData, ContactDeletedData, ContactMergedData, ContactUnmergedData,
    ContactIdentityLinkedData, SlaAppliedData, SlaWarningData, SlaBreachedData, SlaMetData, RoutingAssignedData, RoutingQueuedData, RoutingOverflowedData,
    RoutingQueueWaitExceededData, SurveyRespondedData, ChannelDeliveryStatusData)
{
    private IIntegrationEventPayload Payload => Value as IIntegrationEventPayload ?? throw new InvalidOperationException("The event data is uninitialized.");

    public string Type => Payload.Type;

    public string Subject => Payload.Subject;
}
