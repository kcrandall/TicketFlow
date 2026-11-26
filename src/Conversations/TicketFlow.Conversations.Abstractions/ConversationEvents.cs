using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>The contact side of a conversation.</summary>
public sealed record ContactParty
{
    public ContactParty(ContactId contact)
    {
        if (contact == default)
            throw new ArgumentException("A contact identifier is required.", nameof(contact));
        Contact = contact;
    }

    public ContactId Contact { get; }
}

/// <summary>An agent, API client, automation or system component (kind and identifier; never credentials).</summary>
public sealed record PrincipalParty
{
    public PrincipalParty(AccessActor actor) => Actor = actor ?? throw new ArgumentNullException(nameof(actor));

    public AccessActor Actor { get; }
}

/// <summary>Who sent a message. Switch over every case without a discard arm.</summary>
public union ConversationParty(ContactParty, PrincipalParty);

public enum MessageDirection
{
    /// <summary>From the contact, recorded from a channel.</summary>
    Incoming = 1,

    /// <summary>To the contact, dispatched through the channel outbox.</summary>
    Outgoing = 2,
}

public enum MessageContentType
{
    PlainText = 1,
    Markdown = 2,
    Html = 3,
}

/// <summary>
/// Delivery states. Outgoing messages move forward only: Pending → Sent → Delivered → Read; Failed is terminal and reachable from
/// Pending or Sent. Incoming messages are Received. Persisted; never renumber.
/// </summary>
public enum MessageDeliveryState
{
    Pending = 1,
    Sent = 2,
    Delivered = 3,
    Read = 4,
    Failed = 5,
    Received = 6,
}

/// <summary>The current delivery state of a message, when it was reached and, for failures, a channel failure code token.</summary>
public sealed record MessageDelivery
{
    public const int MaximumFailureCodeLength = 64;

    public MessageDelivery(MessageDeliveryState state, DateTimeOffset updatedAt, string? failureCode = null)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (failureCode is not null && state != MessageDeliveryState.Failed)
            throw new ArgumentException("Only failed deliveries carry a failure code.", nameof(failureCode));
        State = state;
        UpdatedAt = ConversationText.Instant(updatedAt);
        FailureCode = failureCode is null ? null : ConversationText.RequireToken(failureCode, MaximumFailureCodeLength, nameof(failureCode), "Failure codes");
    }

    public MessageDeliveryState State { get; }
    public DateTimeOffset UpdatedAt { get; }
    public string? FailureCode { get; }

    /// <summary>True when an outgoing message may move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool CanAdvance(MessageDeliveryState from, MessageDeliveryState to) => (from, to) switch
    {
        (MessageDeliveryState.Pending, MessageDeliveryState.Sent or MessageDeliveryState.Delivered or MessageDeliveryState.Read or MessageDeliveryState.Failed) => true,
        (MessageDeliveryState.Sent, MessageDeliveryState.Delivered or MessageDeliveryState.Read or MessageDeliveryState.Failed) => true,
        (MessageDeliveryState.Delivered, MessageDeliveryState.Read) => true,
        _ => false,
    };
}

/// <summary>
/// Metadata reference to an attachment held by application or channel storage. TicketFlow never stores attachment bytes.
/// File names are customer content; never logged.
/// </summary>
public sealed record AttachmentRef
{
    public const int MaximumStorageRefLength = 1024;
    public const int MaximumFileNameLength = 255;
    public const int MaximumMediaTypeLength = 127;

    public AttachmentRef(string storageRef, string fileName, string mediaType, long sizeBytes)
    {
        StorageRef = ConversationText.RequireText(storageRef, MaximumStorageRefLength, nameof(storageRef));
        FileName = ConversationText.RequireText(fileName, MaximumFileNameLength, nameof(fileName));
        MediaType = ConversationText.RequireText(mediaType, MaximumMediaTypeLength, nameof(mediaType));
        if (!MediaType.Contains('/'))
            throw new ArgumentException("Media types have the form type/subtype.", nameof(mediaType));
        foreach (var ch in MediaType)
            if (char.IsWhiteSpace(ch))
                throw new ArgumentException("Media types have the form type/subtype.", nameof(mediaType));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        SizeBytes = sizeBytes;
    }

    public string StorageRef { get; }
    public string FileName { get; }
    public string MediaType { get; }
    public long SizeBytes { get; }

    public override string ToString() => $"AttachmentRef {{ MediaType = {MediaType}, SizeBytes = {SizeBytes} }}";
}

/// <summary>Limits shared by messages and notes.</summary>
public static class ConversationMessageLimits
{
    /// <summary>Maximum body size in UTF-8 bytes (64 KiB).</summary>
    public const int MaximumBodyBytes = 64 * 1024;

    public const int MaximumAttachments = 32;

    /// <summary>Maximum external (provider) message identifier length.</summary>
    public const int MaximumExternalIdLength = 1024;

    internal static IReadOnlyList<AttachmentRef> Attachments(IEnumerable<AttachmentRef>? attachments, string parameterName)
    {
        if (attachments is null)
            return [];
        if (attachments is ICollection<AttachmentRef> { Count: 0 } or IReadOnlyCollection<AttachmentRef> { Count: 0 })
            return [];
        AttachmentRef[] snapshot = [.. attachments];
        if (snapshot.Length > MaximumAttachments)
            throw new ArgumentException($"At most {MaximumAttachments} attachments are allowed.", parameterName);
        for (var i = 0; i < snapshot.Length; i++)
            if (snapshot[i] is null)
                throw new ArgumentException($"At most {MaximumAttachments} attachments are allowed.", parameterName);
        return snapshot;
    }

    internal static string? ExternalId(string? externalId, string parameterName) =>
        externalId is null ? null : ConversationText.RequireText(externalId, MaximumExternalIdLength, parameterName);
}

/// <summary>
/// A message to or from the contact. <see cref="Body"/> is customer content (≤ 64 KiB UTF-8; never logged). A message without text
/// must carry at least one attachment. <see cref="ExternalId"/> is the provider message identifier (inbound idempotency key; set
/// for outgoing messages when the channel reports it) and <see cref="Delivery"/> the current delivery state; both are kept current
/// by the store although the timeline entry itself is immutable.
/// </summary>
public sealed record MessageEvent
{
    public MessageEvent(MessageId id, MessageDirection direction, ConversationParty sender, string body, MessageContentType contentType,
        IEnumerable<AttachmentRef>? attachments, string? externalId, MessageDelivery delivery)
    {
        if (id == default)
            throw new ArgumentException("A message identifier is required.", nameof(id));
        if (!Enum.IsDefined(direction) || !Enum.IsDefined(contentType))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (sender.Value is null)
            throw new ArgumentException("A sender is required.", nameof(sender));
        if ((direction == MessageDirection.Incoming) != (sender.Value is ContactParty))
            throw new ArgumentException("Incoming messages are sent by the contact; outgoing messages by a principal.", nameof(sender));
        ArgumentNullException.ThrowIfNull(delivery);
        if ((direction == MessageDirection.Incoming) != (delivery.State == MessageDeliveryState.Received))
            throw new ArgumentException("Only incoming messages are Received.", nameof(delivery));
        Id = id;
        Direction = direction;
        Sender = sender;
        Attachments = ConversationMessageLimits.Attachments(attachments, nameof(attachments));
        Body = ConversationText.RequireBody(body, ConversationMessageLimits.MaximumBodyBytes, nameof(body), allowEmpty: Attachments.Count > 0);
        ContentType = contentType;
        ExternalId = ConversationMessageLimits.ExternalId(externalId, nameof(externalId));
        Delivery = delivery;
    }

    private MessageEvent(MessageId id, MessageDirection direction, ConversationParty sender, string body, MessageContentType contentType,
        IReadOnlyList<AttachmentRef> attachments, string? externalId, MessageDelivery delivery)
    {
        Id = id;
        Direction = direction;
        Sender = sender;
        Body = body;
        ContentType = contentType;
        Attachments = attachments;
        ExternalId = externalId;
        Delivery = delivery;
    }

    public MessageId Id { get; }
    public MessageDirection Direction { get; }
    public ConversationParty Sender { get; }
    public string Body { get; }
    public MessageContentType ContentType { get; }
    public IReadOnlyList<AttachmentRef> Attachments { get; }
    public string? ExternalId { get; }
    public MessageDelivery Delivery { get; }

    /// <summary>A copy with the current delivery state and external identifier.</summary>
    public MessageEvent WithDelivery(MessageDelivery delivery, string? externalId)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if ((Direction == MessageDirection.Incoming) != (delivery.State == MessageDeliveryState.Received))
            throw new ArgumentException("Only incoming messages are Received.", nameof(delivery));
        var validatedExternal = ReferenceEquals(externalId, ExternalId) ? ExternalId : ConversationMessageLimits.ExternalId(externalId, nameof(externalId));
        return new(Id, Direction, Sender, Body, ContentType, Attachments, validatedExternal, delivery);
    }

    public bool Equals(MessageEvent? other)
    {
        if (other is null || Id != other.Id || Direction != other.Direction || !Equals(Sender.Value, other.Sender.Value) || !string.Equals(Body, other.Body, StringComparison.Ordinal)
            || ContentType != other.ContentType || Attachments.Count != other.Attachments.Count || !string.Equals(ExternalId, other.ExternalId, StringComparison.Ordinal)
            || Delivery != other.Delivery)
            return false;
        for (var i = 0; i < Attachments.Count; i++)
            if (Attachments[i] != other.Attachments[i])
                return false;
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Id, Direction, Body.Length);

    public override string ToString() => $"MessageEvent {{ Id = {Id}, Direction = {Direction} }}";
}

/// <summary>A private note for agents. Never delivered to the contact; readable only with <c>note:read</c>.</summary>
public sealed record NoteEvent
{
    public NoteEvent(MessageId id, string body, IEnumerable<AttachmentRef>? attachments = null)
    {
        if (id == default)
            throw new ArgumentException("A note identifier is required.", nameof(id));
        Id = id;
        Attachments = ConversationMessageLimits.Attachments(attachments, nameof(attachments));
        Body = ConversationText.RequireBody(body, ConversationMessageLimits.MaximumBodyBytes, nameof(body), allowEmpty: Attachments.Count > 0);
    }

    public MessageId Id { get; }
    public string Body { get; }
    public IReadOnlyList<AttachmentRef> Attachments { get; }

    public bool Equals(NoteEvent? other) => other is not null && Id == other.Id && string.Equals(Body, other.Body, StringComparison.Ordinal) && Attachments.SequenceEqual(other.Attachments);

    public override int GetHashCode() => HashCode.Combine(Id, Body.Length);

    public override string ToString() => $"NoteEvent {{ Id = {Id} }}";
}

public sealed record AssignmentChanged(UserId? Previous, UserId? Current);

public sealed record TeamChanged(TeamId? Previous, TeamId? Current);

public sealed record StatusChanged(ConversationStatus Previous, ConversationStatus Current);

public sealed record PriorityChanged(ConversationPriority Previous, ConversationPriority Current);

/// <summary>Labels added and removed (each in ordinal order, disjoint, at least one change).</summary>
public sealed record LabelsChanged
{
    public LabelsChanged(IEnumerable<LabelId> added, IEnumerable<LabelId> removed)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(removed);
        Added = Normalize(added);
        Removed = Normalize(removed);
        if (Added.Count + Removed.Count == 0 || Added.Count + Removed.Count > 2 * Conversation.MaximumLabels)
            throw new ArgumentException("Label changes must be initialized, disjoint and non-empty.", nameof(added));
        if (Added.Count > 0 && Removed.Count > 0)
        {
            int i = 0, j = 0;
            while (i < Added.Count && j < Removed.Count)
            {
                var cmp = string.CompareOrdinal(Added[i].Value, Removed[j].Value);
                if (cmp == 0)
                    throw new ArgumentException("Label changes must be initialized, disjoint and non-empty.", nameof(removed));
                if (cmp < 0)
                    i++;
                else
                    j++;
            }
        }
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<LabelId> Normalize(IEnumerable<LabelId> source)
    {
        var list = new List<LabelId>();
        foreach (var label in source)
        {
            if (label == default)
                throw new ArgumentException("Label changes must be initialized, disjoint and non-empty.", nameof(source));
            list.Add(label);
        }
        if (list.Count == 0)
            return System.Collections.ObjectModel.ReadOnlyCollection<LabelId>.Empty;
        if (list.Count == 1)
            return list.AsReadOnly();
        list.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        var write = 1;
        for (var read = 1; read < list.Count; read++)
            if (!string.Equals(list[read].Value, list[write - 1].Value, StringComparison.Ordinal))
                list[write++] = list[read];
        if (write < list.Count)
            list.RemoveRange(write, list.Count - write);
        return list.AsReadOnly();
    }

    public IReadOnlyList<LabelId> Added { get; }
    public IReadOnlyList<LabelId> Removed { get; }

    public bool Equals(LabelsChanged? other) => other is not null && Added.SequenceEqual(other.Added) && Removed.SequenceEqual(other.Removed);

    public override int GetHashCode() => HashCode.Combine(Added.Count, Removed.Count);
}

/// <summary>An attribute was set (<see cref="Current"/>) or removed (<see cref="Current"/> <see langword="null"/>). Values may be masked in views.</summary>
public sealed record AttributeChanged
{
    public AttributeChanged(string key, ConversationAttributeValue? previous, ConversationAttributeValue? current)
    {
        Key = ConversationAttributeDefinition.RequireKey(key, nameof(key));
        if (previous is { Value: null } || current is { Value: null })
            throw new ArgumentException("Attribute values must be initialized when supplied.", previous is { Value: null } ? nameof(previous) : nameof(current));
        Previous = previous;
        Current = current;
    }

    public string Key { get; }
    public ConversationAttributeValue? Previous { get; }
    public ConversationAttributeValue? Current { get; }

    public override string ToString() => $"AttributeChanged {{ Key = {Key} }}";
}

public enum ParticipantChange
{
    Added = 1,
    Removed = 2,
}

public sealed record ParticipantChanged
{
    public ParticipantChanged(UserId participant, ParticipantChange change)
    {
        if (participant == default)
            throw new ArgumentException("A participant is required.", nameof(participant));
        if (!Enum.IsDefined(change))
            throw new ArgumentOutOfRangeException(nameof(change));
        Participant = participant;
        Change = change;
    }

    public UserId Participant { get; }
    public ParticipantChange Change { get; }
}

/// <summary>The conversation's contact changed: re-pointed by a contact merge (<see cref="Merge"/>) or restored by an unmerge.</summary>
public sealed record ContactChanged
{
    public ContactChanged(ContactId previous, ContactId current, Guid? merge)
    {
        if (previous == default || current == default || previous == current || merge == Guid.Empty)
            throw new ArgumentException("Contact changes name two different contacts and a non-empty merge identifier when supplied.", previous == default ? nameof(previous) : merge == Guid.Empty ? nameof(merge) : nameof(current));
        Previous = previous;
        Current = current;
        Merge = merge;
    }

    public ContactId Previous { get; }
    public ContactId Current { get; }
    public Guid? Merge { get; }
}

/// <summary>
/// A TicketFlow-generated notice identified by a dotted code (for example <c>conversation.started</c>). <see cref="Detail"/> is
/// operator-facing text that must not contain personal data.
/// </summary>
public sealed record SystemEvent
{
    public const int MaximumCodeLength = 128;
    public const int MaximumDetailLength = 1024;

    public SystemEvent(string code, string? detail = null)
    {
        Code = ConversationText.RequireDottedName(code, MaximumCodeLength, nameof(code), "System event codes");
        Detail = ConversationText.OptionalText(detail, MaximumDetailLength, nameof(detail));
    }

    public string Code { get; }
    public string? Detail { get; }
}

/// <summary>
/// Extension point for later features (SLA, automation, assistant): a dotted <see cref="Kind"/> owned by the feature (for example
/// <c>sla.breached</c>), the payload schema <see cref="PayloadVersion"/> (1-1000) and a JSON object payload (≤ 16 KiB UTF-8) the
/// owning feature encodes with its own explicit discriminators. TicketFlow stores and replays it verbatim (normalized JSON).
/// </summary>
public sealed record ExtensionEvent
{
    public const int MaximumKindLength = 128;
    public const int MaximumPayloadBytes = 16 * 1024;

    public ExtensionEvent(string kind, int payloadVersion, string payload)
    {
        Kind = ConversationText.RequireDottedName(kind, MaximumKindLength, nameof(kind), "Extension kinds");
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadVersion, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadVersion, 1000);
        PayloadVersion = payloadVersion;
        Payload = ConversationJsonCodec.NormalizeExtensionPayload(payload, MaximumPayloadBytes, nameof(payload));
    }

    public string Kind { get; }
    public int PayloadVersion { get; }

    /// <summary>A compact JSON object.</summary>
    public string Payload { get; }

    public override string ToString() => $"ExtensionEvent {{ Kind = {Kind}, PayloadVersion = {PayloadVersion} }}";
}

/// <summary>Persisted numeric event discriminators. Never renumber.</summary>
public enum ConversationEventKind
{
    Message = 1,
    Note = 2,
    Assignment = 3,
    Team = 4,
    Status = 5,
    Priority = 6,
    Labels = 7,
    Attribute = 8,
    Participant = 9,
    Contact = 10,
    System = 11,
    Extension = 12,
}

/// <summary>A timeline event payload. Switch over every case without a discard arm; persisted only through <see cref="ConversationJsonCodec"/>.</summary>
public union ConversationEvent(MessageEvent, NoteEvent, AssignmentChanged, TeamChanged, StatusChanged, PriorityChanged, LabelsChanged,
    AttributeChanged, ParticipantChanged, ContactChanged, SystemEvent, ExtensionEvent)
{
    /// <exception cref="System.Runtime.CompilerServices.SwitchExpressionException">The event is <c>default</c>.</exception>
    public ConversationEventKind Kind => this switch
    {
        MessageEvent => ConversationEventKind.Message,
        NoteEvent => ConversationEventKind.Note,
        AssignmentChanged => ConversationEventKind.Assignment,
        TeamChanged => ConversationEventKind.Team,
        StatusChanged => ConversationEventKind.Status,
        PriorityChanged => ConversationEventKind.Priority,
        LabelsChanged => ConversationEventKind.Labels,
        AttributeChanged => ConversationEventKind.Attribute,
        ParticipantChanged => ConversationEventKind.Participant,
        ContactChanged => ConversationEventKind.Contact,
        SystemEvent => ConversationEventKind.System,
        ExtensionEvent => ConversationEventKind.Extension,
    };
}

/// <summary>
/// One immutable timeline entry: its gapless per-conversation <see cref="Sequence"/> (first is 1), identifier, time, the acting
/// principal and the event.
/// </summary>
public sealed record TimelineEvent
{
    public TimelineEvent(ConversationId conversation, long sequence, Guid id, DateTimeOffset occurredAt, AccessActor actor, ConversationEvent @event)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        if (id == Guid.Empty)
            throw new ArgumentException("An event identifier is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(actor);
        if (@event.Value is null)
            throw new ArgumentException("An event is required.", nameof(@event));
        Conversation = conversation;
        Sequence = sequence;
        Id = id;
        OccurredAt = ConversationText.Instant(occurredAt);
        Actor = actor;
        Event = @event;
    }

    public ConversationId Conversation { get; }
    public long Sequence { get; }
    public Guid Id { get; }
    public DateTimeOffset OccurredAt { get; }
    public AccessActor Actor { get; }
    public ConversationEvent Event { get; }

    public TimelineEvent WithEvent(ConversationEvent @event) => new(Conversation, Sequence, Id, OccurredAt, Actor, @event);

    public override string ToString() => $"TimelineEvent {{ Conversation = {Conversation}, Sequence = {Sequence}, Kind = {Event.Kind} }}";
}
