using System.Globalization;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Automation;

/// <summary>A context could not be built: the conversation does not exist, or the principal may not read it.</summary>
public enum AutomationContextFailure
{
    NotFound = 1,
    Forbidden = 2,
}

/// <summary>
/// Builds <see cref="AutomationContext"/>s as a given principal: the conversation and contact are read through the access-checked
/// services (so masked values are absent and conditions over them are unknown); the inbox channel kind and business calendar are
/// read from the workspace stores (configuration, not support data).
/// </summary>
public sealed class AutomationContextBuilder(IConversationService conversations, IContactService contacts, IInboxStore inboxes, IBusinessCalendarStore calendars,
    TimeProvider time)
{
    private readonly IConversationService conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IContactService contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
    private readonly IInboxStore inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
    private readonly IBusinessCalendarStore calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
    private readonly TimeProvider time = time ?? throw new ArgumentNullException(nameof(time));

    public async Task<(AutomationContext? Context, AutomationContextFailure? Failure)> BuildAsync(PrincipalRef principal, ConversationId conversation, AutomationEvent automationEvent,
        AutomationMessageFacts? message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(automationEvent);
        var read = await conversations.GetAsync(principal, conversation, cancellationToken).ConfigureAwait(false);
        var (view, failure) = read switch
        {
            ConversationView found => (found, (AutomationContextFailure?)null),
            ConversationNotFound => ((ConversationView?)null, AutomationContextFailure.NotFound),
            ConversationRefused => (null, AutomationContextFailure.Forbidden),
        };
        if (view is null)
            return (null, failure);
        var scope = principal.Scope;
        var contact = await ContactAsync(principal, view.Contact, cancellationToken).ConfigureAwait(false);
        var inbox = (await inboxes.GetAsync(scope, view.Inbox, cancellationToken).ConfigureAwait(false))?.Inbox;
        BusinessCalendarCalculator? calendar = null;
        if (inbox?.Calendar is { } calendarId && await calendars.GetAsync(scope, calendarId, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            try
            {
                calendar = new BusinessCalendarCalculator(stored.Calendar);
            }
            catch (TimeZoneNotFoundException)
            {
                calendar = null;
            }
        }
        return (new AutomationContext(AutomationConversationFacts.From(view), contact, message, automationEvent, time.GetUtcNow(), inbox?.Channel.Kind, calendar), null);
    }

    private async Task<AutomationContactFacts?> ContactAsync(PrincipalRef principal, ContactId id, CancellationToken cancellationToken)
    {
        var result = await contacts.GetAsync(principal, id, cancellationToken).ConfigureAwait(false);
        if (result.Value is not ContactApplied<ContactView> applied)
            return null;
        var view = applied.Value;
        var attributes = new Dictionary<string, AutomationAttributeFact>(StringComparer.Ordinal);
        var masked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in view.Attributes)
        {
            if (attribute.Masked || attribute.Value is not { } value)
                masked.Add(attribute.Key);
            else
                attributes[attribute.Key] = Fact(value);
        }
        return new AutomationContactFacts(view.Id, view.Profile.DisplayName, view.Profile.Company, view.Profile.Locale, view.Labels, attributes, masked);
    }

    internal static AutomationAttributeFact Fact(ContactAttributeValue value) => value switch
    {
        TextAttributeValue text => new(AutomationAttributeKind.Text, text.Value, null),
        NumberAttributeValue number => new(AutomationAttributeKind.Number, number.Value.ToString(CultureInfo.InvariantCulture), null),
        BooleanAttributeValue boolean => new(AutomationAttributeKind.Boolean, boolean.Value ? "true" : "false", null),
        DateAttributeValue date => new(AutomationAttributeKind.Date, date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null),
        ListAttributeValue list => new(AutomationAttributeKind.List, null, list.Items),
    };
}

/// <summary>Template variable values from a context. Masked or unreadable values are absent, so templates render their fallbacks.</summary>
public static class AutomationTemplateValues
{
    public static IReadOnlyDictionary<string, string?> From(AutomationContext context, string? agentName)
    {
        ArgumentNullException.ThrowIfNull(context);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AutomationTemplate.ConversationIdVariable] = context.Conversation.Id.Value,
            [AutomationTemplate.ConversationStatus] = context.Conversation.Status.Kind.ToString().ToLowerInvariant(),
            [AutomationTemplate.ConversationPriority] = context.Conversation.Priority.ToString().ToLowerInvariant(),
            [AutomationTemplate.AgentName] = agentName,
            [AutomationTemplate.ContactName] = context.Contact?.Name,
            [AutomationTemplate.ContactCompany] = context.Contact?.Company,
        };
        foreach (var (key, fact) in context.Conversation.Attributes)
            values[AutomationTemplate.ConversationAttributePrefix + key] = Text(fact);
        if (context.Contact is { } contact)
            foreach (var (key, fact) in contact.Attributes)
                values[AutomationTemplate.ContactAttributePrefix + key] = Text(fact);
        return values;
    }

    private static string? Text(AutomationAttributeFact fact) => fact.Kind == AutomationAttributeKind.List ? string.Join(", ", fact.Items ?? []) : fact.Scalar;
}
