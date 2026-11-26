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

[Flags]
internal enum AutomationContextNeeds
{
    None = 0,
    Contact = 1 << 0,
    Inbox = 1 << 1,
    Calendar = 1 << 2,
    AgentName = 1 << 3,
    All = Contact | Inbox | Calendar | AgentName,
}

internal static class AutomationContextNeedsHelper
{
    public static AutomationContextNeeds ForCondition(AutomationCondition? condition) => condition switch
    {
        null => AutomationContextNeeds.None,
        AllOfCondition all => Combine(all.Items),
        AnyOfCondition any => Combine(any.Items),
        NegatedCondition negated => ForCondition(negated.Operand),
        HasLabelsCondition labels => labels.Target == ConditionTarget.Contact ? AutomationContextNeeds.Contact : AutomationContextNeeds.None,
        AttributeCondition attribute => attribute.Target == ConditionTarget.Contact ? AutomationContextNeeds.Contact : AutomationContextNeeds.None,
        ChannelKindIsCondition => AutomationContextNeeds.Inbox,
        BusinessHoursCondition => AutomationContextNeeds.Inbox | AutomationContextNeeds.Calendar,
        StatusIsCondition or PriorityCondition or InboxIsCondition or TeamIsCondition or AssigneeIsCondition
            or MessageContentCondition or ElapsedCondition => AutomationContextNeeds.None,
    };

    public static AutomationContextNeeds ForTemplate(AutomationTemplate template)
    {
        var needs = AutomationContextNeeds.None;
        var vars = template.Variables;
        for (var i = 0; i < vars.Count; i++)
        {
            var v = vars[i];
            if (v is AutomationTemplate.ContactName or AutomationTemplate.ContactCompany || v.StartsWith(AutomationTemplate.ContactAttributePrefix, StringComparison.Ordinal))
                needs |= AutomationContextNeeds.Contact;
            else if (string.Equals(v, AutomationTemplate.AgentName, StringComparison.Ordinal))
                needs |= AutomationContextNeeds.AgentName;
        }
        return needs;
    }

    public static AutomationContextNeeds ForCannedResponse(CannedResponse response, bool needsContactLocale)
    {
        var needs = needsContactLocale ? AutomationContextNeeds.Contact : AutomationContextNeeds.None;
        for (var i = 0; i < response.Variants.Count; i++)
            needs |= ForTemplate(response.Variants[i].Template);
        return needs;
    }

    public static AutomationContextNeeds ForAction(AutomationAction action) => action switch
    {
        // SendReply loads the canned response at execution time, so conservatively assume contact locale/vars and agent.name.
        SendReplyAction => AutomationContextNeeds.Contact | AutomationContextNeeds.AgentName,
        AddNoteAction note => ForTemplate(note.Template),
        AssignUserAction or AssignTeamAction or AssignToRoutingAction or AddLabelsAction or RemoveLabelsAction or SetPriorityAction
            or SetStatusAction or SnoozeAction or SetAttributeAction or SendWebhookAction or TriggerSurveyAction or ExecuteSlaEscalationAction => AutomationContextNeeds.None,
    };

    public static AutomationContextNeeds ForActions(IReadOnlyList<AutomationAction> actions)
    {
        var needs = AutomationContextNeeds.None;
        for (var i = 0; i < actions.Count; i++)
            needs |= ForAction(actions[i]);
        return needs;
    }

    public static AutomationContextNeeds ForRule(AutomationRule rule) =>
        ForCondition(rule.Condition) | ForActions(rule.Actions);

    public static bool ActionReadsMutableConversationState(AutomationAction action) => action switch
    {
        SendReplyAction or AddNoteAction or SendWebhookAction or ExecuteSlaEscalationAction => true,
        AssignUserAction or AssignTeamAction or AssignToRoutingAction or AddLabelsAction or RemoveLabelsAction or SetPriorityAction
            or SetStatusAction or SnoozeAction or SetAttributeAction or TriggerSurveyAction => false,
    };

    private static AutomationContextNeeds Combine(IReadOnlyList<AutomationCondition> items)
    {
        var needs = AutomationContextNeeds.None;
        for (var i = 0; i < items.Count; i++)
            needs |= ForCondition(items[i]);
        return needs;
    }
}

internal sealed class AutomationContextCache
{
    public Dictionary<(TicketScope Scope, InboxId Inbox), Inbox?> Inboxes { get; } = [];
    public Dictionary<(TicketScope Scope, BusinessCalendarId Calendar), BusinessCalendarCalculator?> Calendars { get; } = [];
    public Dictionary<(PrincipalRef Principal, ContactId Contact), AutomationContactFacts?> Contacts { get; } = [];
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

    public Task<(AutomationContext? Context, AutomationContextFailure? Failure)> BuildAsync(PrincipalRef principal, ConversationId conversation, AutomationEvent automationEvent,
        AutomationMessageFacts? message, CancellationToken cancellationToken = default) =>
        BuildInternalAsync(principal, conversation, automationEvent, message, AutomationContextNeeds.All, cache: null, cancellationToken);

    internal async Task<(AutomationContext? Context, AutomationContextFailure? Failure)> BuildInternalAsync(PrincipalRef principal, ConversationId conversation,
        AutomationEvent automationEvent, AutomationMessageFacts? message, AutomationContextNeeds needs, AutomationContextCache? cache, CancellationToken cancellationToken)
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
        var needContact = (needs & AutomationContextNeeds.Contact) != AutomationContextNeeds.None;
        var needInbox = (needs & (AutomationContextNeeds.Inbox | AutomationContextNeeds.Calendar)) != AutomationContextNeeds.None;
        var needCalendar = (needs & AutomationContextNeeds.Calendar) != AutomationContextNeeds.None;

        AutomationContactFacts? contact = null;
        Inbox? inbox = null;
        if (needContact && needInbox)
        {
            var contactTask = ContactAsync(principal, view.Contact, cache, cancellationToken);
            var inboxTask = InboxAsync(scope, view.Inbox, cache, cancellationToken);
            await Task.WhenAll(contactTask, inboxTask).ConfigureAwait(false);
            contact = contactTask.Result;
            inbox = inboxTask.Result;
        }
        else if (needContact)
            contact = await ContactAsync(principal, view.Contact, cache, cancellationToken).ConfigureAwait(false);
        else if (needInbox)
            inbox = await InboxAsync(scope, view.Inbox, cache, cancellationToken).ConfigureAwait(false);

        BusinessCalendarCalculator? calendar = null;
        if (needCalendar && inbox?.Calendar is { } calendarId)
            calendar = await CalendarAsync(scope, calendarId, cache, cancellationToken).ConfigureAwait(false);

        return (new AutomationContext(AutomationConversationFacts.From(view), contact, message, automationEvent, time.GetUtcNow(), inbox?.Channel.Kind, calendar), null);
    }

    internal async Task<AutomationContext?> RefreshConversationAsync(PrincipalRef principal, AutomationContext current, CancellationToken cancellationToken)
    {
        var read = await conversations.GetAsync(principal, current.Conversation.Id, cancellationToken).ConfigureAwait(false);
        return read.Value is ConversationView view
            ? current with { Conversation = AutomationConversationFacts.From(view), Now = time.GetUtcNow() }
            : null;
    }

    private async Task<Inbox?> InboxAsync(TicketScope scope, InboxId id, AutomationContextCache? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.Inboxes.TryGetValue(scope, id, out var cached))
            return cached;
        var inbox = (await inboxes.GetAsync(scope, id, cancellationToken).ConfigureAwait(false))?.Inbox;
        if (cache is not null)
            cache.Inboxes[scope, id] = inbox;
        return inbox;
    }

    private async Task<BusinessCalendarCalculator?> CalendarAsync(TicketScope scope, BusinessCalendarId calendarId, AutomationContextCache? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.Calendars.TryGetValue(scope, calendarId, out var cached))
            return cached;
        BusinessCalendarCalculator? calendar = null;
        if (await calendars.GetAsync(scope, calendarId, cancellationToken).ConfigureAwait(false) is { } stored)
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
        if (cache is not null)
            cache.Calendars[scope, calendarId] = calendar;
        return calendar;
    }

    private async Task<AutomationContactFacts?> ContactAsync(PrincipalRef principal, ContactId id, AutomationContextCache? cache, CancellationToken cancellationToken)
    {
        if (cache is not null && cache.Contacts.TryGetValue(principal, id, out var cached))
            return cached;
        var result = await contacts.GetAsync(principal, id, cancellationToken).ConfigureAwait(false);
        AutomationContactFacts? facts = null;
        if (result.Value is TicketValue<ContactView> applied)
        {
            var view = applied.Value;
            Dictionary<string, AutomationAttributeFact> attributes;
            HashSet<string> masked;
            if (view.Attributes.Count == 0)
            {
                attributes = EmptyAttributes;
                masked = EmptyMasked;
            }
            else
            {
                attributes = new Dictionary<string, AutomationAttributeFact>(view.Attributes.Count, StringComparer.Ordinal);
                masked = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < view.Attributes.Count; i++)
                {
                    var attribute = view.Attributes[i];
                    if (attribute.Masked || attribute.Value is not { } value)
                        masked.Add(attribute.Key);
                    else
                        attributes[attribute.Key] = Fact(value);
                }
            }
            facts = new AutomationContactFacts(view.Id, view.Profile.DisplayName, view.Profile.Company, view.Profile.Locale, view.Labels, attributes, masked);
        }
        if (cache is not null)
            cache.Contacts[principal, id] = facts;
        return facts;
    }

    private static readonly Dictionary<string, AutomationAttributeFact> EmptyAttributes = new(0, StringComparer.Ordinal);
    private static readonly HashSet<string> EmptyMasked = new(0, StringComparer.Ordinal);

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
        var values = new Dictionary<string, string?>(6 + context.Conversation.Attributes.Count + (context.Contact?.Attributes.Count ?? 0), StringComparer.Ordinal)
        {
            [AutomationTemplate.ConversationIdVariable] = context.Conversation.Id.Value,
            [AutomationTemplate.ConversationStatus] = context.Conversation.Status.Kind.WireName,
            [AutomationTemplate.ConversationPriority] = context.Conversation.Priority.WireName,
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

    internal static Func<string, string?> Resolver(AutomationContext context, string? agentName) => variable => variable switch
    {
        AutomationTemplate.ConversationIdVariable => context.Conversation.Id.Value,
        AutomationTemplate.ConversationStatus => context.Conversation.Status.Kind.WireName,
        AutomationTemplate.ConversationPriority => context.Conversation.Priority.WireName,
        AutomationTemplate.AgentName => agentName,
        AutomationTemplate.ContactName => context.Contact?.Name,
        AutomationTemplate.ContactCompany => context.Contact?.Company,
        _ when variable.StartsWith(AutomationTemplate.ConversationAttributePrefix, StringComparison.Ordinal) =>
            context.Conversation.Attributes.TryGetValue(variable[AutomationTemplate.ConversationAttributePrefix.Length..], out var convFact) ? Text(convFact) : null,
        _ when variable.StartsWith(AutomationTemplate.ContactAttributePrefix, StringComparison.Ordinal) =>
            context.Contact is { } c && c.Attributes.TryGetValue(variable[AutomationTemplate.ContactAttributePrefix.Length..], out var contactFact) ? Text(contactFact) : null,
        _ => null,
    };

    private static string? Text(AutomationAttributeFact fact) => fact.Kind == AutomationAttributeKind.List ? string.Join(", ", fact.Items ?? []) : fact.Scalar;
}
