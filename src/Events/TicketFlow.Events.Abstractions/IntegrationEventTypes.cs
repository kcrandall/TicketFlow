namespace TicketFlow.Events;

/// <summary>
/// The v1 integration event types (CloudEvents <c>type</c>): <c>ticketflow.&lt;area&gt;.&lt;name&gt;.v&lt;version&gt;</c>. A breaking payload change
/// introduces a new version suffix; existing versions only ever gain documented optional fields.
/// </summary>
public static class IntegrationEventTypes
{
    public const string Prefix = "ticketflow.";
    public const int MaximumLength = 128;

    public const string ConversationCreated = "ticketflow.conversation.created.v1";
    public const string ConversationStatusChanged = "ticketflow.conversation.status-changed.v1";
    public const string ConversationAssigned = "ticketflow.conversation.assigned.v1";
    public const string ConversationTeamChanged = "ticketflow.conversation.team-changed.v1";
    public const string ConversationPriorityChanged = "ticketflow.conversation.priority-changed.v1";
    public const string ConversationLabelsChanged = "ticketflow.conversation.labels-changed.v1";
    public const string ConversationMessageReceived = "ticketflow.conversation.message-received.v1";
    public const string ConversationMessageSent = "ticketflow.conversation.message-sent.v1";
    public const string ConversationNoteAdded = "ticketflow.conversation.note-added.v1";
    public const string ConversationResolved = "ticketflow.conversation.resolved.v1";
    public const string ConversationReopened = "ticketflow.conversation.reopened.v1";
    public const string ConversationDeleted = "ticketflow.conversation.deleted.v1";
    public const string ContactCreated = "ticketflow.contact.created.v1";
    public const string ContactUpdated = "ticketflow.contact.updated.v1";
    public const string ContactDeleted = "ticketflow.contact.deleted.v1";
    public const string ContactMerged = "ticketflow.contact.merged.v1";
    public const string ContactUnmerged = "ticketflow.contact.unmerged.v1";
    public const string ContactIdentityLinked = "ticketflow.contact.identity-linked.v1";
    public const string SlaApplied = "ticketflow.sla.applied.v1";
    public const string SlaWarning = "ticketflow.sla.warning.v1";
    public const string SlaBreached = "ticketflow.sla.breached.v1";
    public const string SlaMet = "ticketflow.sla.met.v1";
    public const string RoutingAssigned = "ticketflow.routing.assigned.v1";
    public const string RoutingOverflowed = "ticketflow.routing.overflowed.v1";
    public const string RoutingQueueWaitExceeded = "ticketflow.routing.queue-wait-exceeded.v1";
    public const string SurveyResponded = "ticketflow.survey.responded.v1";
    public const string ChannelDeliveryStatus = "ticketflow.channel.delivery-status.v1";

    /// <summary>Every v1 type, ordered (ordinal).</summary>
    public static IReadOnlyList<string> All { get; } = [.. new[]
    {
        ConversationCreated, ConversationStatusChanged, ConversationAssigned, ConversationTeamChanged, ConversationPriorityChanged, ConversationLabelsChanged,
        ConversationMessageReceived, ConversationMessageSent, ConversationNoteAdded, ConversationResolved, ConversationReopened, ConversationDeleted,
        ContactCreated, ContactUpdated, ContactDeleted, ContactMerged, ContactUnmerged, ContactIdentityLinked,
        SlaApplied, SlaWarning, SlaBreached, SlaMet, RoutingAssigned, RoutingOverflowed, RoutingQueueWaitExceeded, SurveyResponded, ChannelDeliveryStatus,
    }.Order(StringComparer.Ordinal)];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string? type) => type is not null && Known.Contains(type);

    /// <summary>True for <c>ticketflow.</c> followed by 2-6 dot-separated tokens ending in <c>v&lt;1-999&gt;</c>, at most 128 characters.</summary>
    public static bool IsWellFormed(string? type)
    {
        if (type is null || type.Length > MaximumLength || !type.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        var segments = type[Prefix.Length..].Split('.');
        if (segments.Length is < 2 or > 6 || segments.Any(segment => !EventText.IsToken(segment, 64) || segment.Contains('.')))
            return false;
        var version = segments[^1];
        return version.Length is >= 2 and <= 4 && version[0] == 'v' && version[1] != '0' && version[1..].All(char.IsAsciiDigit);
    }
}

/// <summary>
/// A subscription type filter: an exact type (<c>ticketflow.conversation.created.v1</c>), a dotted prefix ending in <c>.*</c>
/// (<c>ticketflow.conversation.*</c>, <c>ticketflow.*</c>) or <c>*</c> for every type.
/// </summary>
public readonly record struct EventTypePattern
{
    private readonly string? value;

    public EventTypePattern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValid(value))
            throw new ArgumentException("Type patterns are '*', an exact ticketflow type or a dotted 'ticketflow.' prefix ending in '.*'.", nameof(value));
        this.value = value;
    }

    public static EventTypePattern All { get; } = new("*");

    public string Value => value ?? throw new InvalidOperationException("The EventTypePattern is uninitialized.");

    public bool IsInitialized => value is not null;

    public bool Matches(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var pattern = Value;
        if (pattern == "*")
            return true;
        return pattern.EndsWith(".*", StringComparison.Ordinal)
            ? type.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : string.Equals(type, pattern, StringComparison.Ordinal);
    }

    public override string ToString() => value ?? string.Empty;

    private static bool IsValid(string value)
    {
        if (value == "*")
            return true;
        if (value.Length > IntegrationEventTypes.MaximumLength)
            return false;
        if (value.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = value[..^2];
            return prefix == "ticketflow" || prefix.StartsWith(IntegrationEventTypes.Prefix, StringComparison.Ordinal)
                && prefix[IntegrationEventTypes.Prefix.Length..].Split('.').All(segment => EventText.IsToken(segment, 64) && !segment.Contains('.'));
        }
        return IntegrationEventTypes.IsWellFormed(value);
    }
}

/// <summary>How much of an event a subscription receives. Persisted; never renumber.</summary>
public enum EventPrivacy
{
    /// <summary>Identifiers only (conversation, inbox, contact, message, user, team, survey ids). The default.</summary>
    IdsOnly = 1,

    /// <summary>Identifiers plus non-identifying state: statuses, priorities, labels, channel kinds, counts, SLA metrics, scores.</summary>
    Metadata = 2,

    /// <summary>
    /// Metadata plus content hydrated at delivery time (message bodies and attachment metadata, survey comments, contact profile and
    /// identities) through the owning feature services as the subscription principal, so Access masking applies. Requires
    /// <c>events:content</c> (sensitive).
    /// </summary>
    Content = 3,
}
