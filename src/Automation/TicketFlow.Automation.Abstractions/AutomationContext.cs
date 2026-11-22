using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Automation;

/// <summary>Attribute value kinds for <see cref="AutomationAttributeFact"/>. Never renumber.</summary>
public enum AutomationAttributeKind
{
    Text = 1,
    Number = 2,
    Boolean = 3,
    Date = 4,
    List = 5,
}

/// <summary>
/// A readable attribute value in Access condition form: <see cref="Scalar"/> for text, number (invariant), boolean (<c>true</c>/<c>false</c>)
/// and date (<c>yyyy-MM-dd</c>); <see cref="Items"/> for lists. Personal data: never logged.
/// </summary>
public sealed record AutomationAttributeFact(AutomationAttributeKind Kind, string? Scalar, IReadOnlyList<string>? Items)
{
    public static AutomationAttributeFact From(ConversationAttributeValue value) => value switch
    {
        ConversationTextValue => new(AutomationAttributeKind.Text, value.ConditionValue, null),
        ConversationNumberValue => new(AutomationAttributeKind.Number, value.ConditionValue, null),
        ConversationBooleanValue => new(AutomationAttributeKind.Boolean, value.ConditionValue, null),
        ConversationDateValue => new(AutomationAttributeKind.Date, value.ConditionValue, null),
        ConversationListValue list => new(AutomationAttributeKind.List, null, list.Items),
    };

    public override string ToString() => $"AutomationAttributeFact {{ Kind = {Kind} }}";
}

/// <summary>
/// The conversation as the evaluating principal may see it: <see cref="Attributes"/> holds only readable values, and
/// <see cref="MaskedAttributes"/> the keys whose values were masked (conditions on them are unknown).
/// </summary>
public sealed record AutomationConversationFacts(
    ConversationId Id,
    long Revision,
    InboxId Inbox,
    ContactId Contact,
    ConversationStatus Status,
    ConversationPriority Priority,
    UserId? Assignee,
    TeamId? Team,
    IReadOnlyList<LabelId> Labels,
    IReadOnlyDictionary<string, AutomationAttributeFact> Attributes,
    IReadOnlySet<string> MaskedAttributes,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? WaitingSince,
    DateTimeOffset? LastIncomingAt,
    DateTimeOffset? LastOutgoingAt)
{
    /// <summary>Facts from a server-side masked <see cref="ConversationView"/>.</summary>
    public static AutomationConversationFacts From(ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var attributes = new Dictionary<string, AutomationAttributeFact>(StringComparer.Ordinal);
        var masked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in view.Attributes)
        {
            if (attribute.Masked || attribute.Value is not { } value)
                masked.Add(attribute.Key);
            else
                attributes[attribute.Key] = AutomationAttributeFact.From(value);
        }
        return new(view.Id, view.Revision, view.Inbox, view.Contact, view.Status, view.Priority, view.Assignee, view.Team, view.Labels, attributes, masked,
            view.CreatedAt, view.LastActivityAt, view.WaitingSince, view.LastIncomingAt, view.LastOutgoingAt);
    }

    public override string ToString() => $"AutomationConversationFacts {{ Id = {Id}, Revision = {Revision} }}";
}

/// <summary>The conversation's contact as the evaluating principal may see it (masked attributes listed in <see cref="MaskedAttributes"/>).</summary>
public sealed record AutomationContactFacts(
    ContactId Id,
    string? Name,
    string? Company,
    string? Locale,
    IReadOnlyList<LabelId> Labels,
    IReadOnlyDictionary<string, AutomationAttributeFact> Attributes,
    IReadOnlySet<string> MaskedAttributes)
{
    public override string ToString() => $"AutomationContactFacts {{ Id = {Id} }}";
}

/// <summary>The triggering message (body bounded by the conversation model). Never logged.</summary>
public sealed record AutomationMessageFacts(MessageDirection Direction, string Body)
{
    public override string ToString() => $"AutomationMessageFacts {{ Direction = {Direction} }}";
}

/// <summary>
/// Everything a condition can read: the visible conversation, its contact (when readable), the triggering message (for message
/// triggers), the event, the evaluation instant, the inbox channel kind and the inbox's business calendar.
/// </summary>
public sealed record AutomationContext(
    AutomationConversationFacts Conversation,
    AutomationContactFacts? Contact,
    AutomationMessageFacts? Message,
    AutomationEvent Event,
    DateTimeOffset Now,
    ChannelKind? Channel = null,
    BusinessCalendarCalculator? Calendar = null);
