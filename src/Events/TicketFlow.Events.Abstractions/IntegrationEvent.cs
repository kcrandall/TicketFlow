using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>
/// One integration event: a deterministic identifier (see <see cref="EventText.EventId(string, string)"/>), when the source change
/// happened, the acting principal when known, and the typed payload (which determines the CloudEvents <c>type</c> and <c>subject</c>).
/// </summary>
public sealed record IntegrationEvent
{
    public IntegrationEvent(Guid id, DateTimeOffset time, EventActor? actor, IntegrationEventData data)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("An event identifier is required.", nameof(id));
        if (data.Value is null)
            throw new ArgumentException("Event data is required.", nameof(data));
        Id = id;
        Time = EventText.Instant(time);
        Actor = actor;
        Data = data;
    }

    public Guid Id { get; }
    public DateTimeOffset Time { get; }
    public EventActor? Actor { get; }
    public IntegrationEventData Data { get; }
    public string Type => Data.Type;
    public string Subject => Data.Subject;

    public override string ToString() => $"IntegrationEvent {{ Id = {Id}, Type = {Type} }}";
}

/// <summary>
/// The access and filter snapshot of an event (the conversation's state when it was projected, or the contact). Subscription inbox and
/// team filters and Access decisions use it; it is not part of the rendered payload.
/// </summary>
public sealed record EventResource
{
    public EventResource(ConversationId? conversation = null, InboxId? inbox = null, TeamId? team = null, UserId? assignee = null, ContactId? contact = null)
    {
        if (conversation == default(ConversationId) || inbox == default(InboxId) || team == default(TeamId) || assignee == default(UserId) || contact == default(ContactId))
            throw new ArgumentException("Resource identifiers must be initialized when supplied.");
        Conversation = conversation;
        Inbox = inbox;
        Team = team;
        Assignee = assignee;
        Contact = contact;
    }

    public static EventResource None { get; } = new();

    public ConversationId? Conversation { get; }
    public InboxId? Inbox { get; }
    public TeamId? Team { get; }
    public UserId? Assignee { get; }
    public ContactId? Contact { get; }

    /// <summary>
    /// The permission and Access resource that decide whether a principal may receive the event: <c>conversation:read</c> on the
    /// conversation (team, inbox, assignee; labels unknown), else <c>contact:read</c> on the contact, else only <c>events:read</c> on the
    /// workspace.
    /// </summary>
    public (Permission Permission, AccessResource Resource) AccessCheck(TicketScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (Conversation is { } conversation)
            return (AccessPermissions.ConversationRead, new AccessResource(scope, conversation.Value, Team, Inbox, Assignee));
        if (Contact is { } contact)
            return (AccessPermissions.ContactRead, new AccessResource(scope, contact.Value));
        return (EventPermissions.Read, new AccessResource(scope));
    }
}

/// <summary>An event to append with its resource snapshot.</summary>
public sealed record IntegrationEventDraft(IntegrationEvent Event, EventResource Resource)
{
    public IntegrationEvent Event { get; } = Event ?? throw new ArgumentNullException(nameof(Event));
    public EventResource Resource { get; } = Resource ?? throw new ArgumentNullException(nameof(Resource));
}

/// <summary>A committed event: its scope, gapless per-workspace <see cref="Sequence"/> (first is 1) and when it was recorded.</summary>
public sealed record StoredIntegrationEvent(TicketScope Scope, long Sequence, IntegrationEvent Event, EventResource Resource, DateTimeOffset RecordedAt)
{
    public override string ToString() => $"StoredIntegrationEvent {{ Sequence = {Sequence}, Type = {Event.Type} }}";
}

/// <summary>An attachment's metadata (never bytes or storage references).</summary>
public sealed record EventAttachment(string FileName, string MediaType, long SizeBytes);

/// <summary>Message content hydrated for <see cref="EventPrivacy.Content"/>; <see cref="ExternalId"/> is omitted when masked.</summary>
public sealed record MessageEventContent(string Body, string ContentType, IReadOnlyList<EventAttachment> Attachments, string? ExternalId)
{
    public override string ToString() => "MessageEventContent { <redacted> }";
}

/// <summary>A survey comment hydrated for <see cref="EventPrivacy.Content"/>.</summary>
public sealed record SurveyCommentContent(string? Comment)
{
    public override string ToString() => "SurveyCommentContent { <redacted> }";
}

/// <summary>A contact identity in content: masked identities are omitted (their field paths are listed in the masked set).</summary>
public sealed record EventIdentity(string Kind, string Namespace, string Value, bool Verified);

/// <summary>Contact content hydrated for <see cref="EventPrivacy.Content"/> from the masked contact view.</summary>
public sealed record ContactEventContent(string? DisplayName, IReadOnlyList<EventIdentity> Identities)
{
    public override string ToString() => "ContactEventContent { <redacted> }";
}

/// <summary>Hydrated content kinds. Switch over every case without a discard arm.</summary>
public union EventContentValue(MessageEventContent, SurveyCommentContent, ContactEventContent);

/// <summary>Hydrated content plus the Access field paths that were masked for the subscription principal.</summary>
public sealed record EventContent(EventContentValue Value, IReadOnlyList<string> MaskedFields)
{
    public override string ToString() => "EventContent { <redacted> }";
}
