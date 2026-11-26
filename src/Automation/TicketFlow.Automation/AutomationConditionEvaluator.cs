using System.Globalization;
using System.Text.RegularExpressions;
using TicketFlow.Conversations;

namespace TicketFlow.Automation;

/// <summary>
/// Three-valued (Kleene) evaluation of <see cref="AutomationCondition"/> trees over an <see cref="AutomationContext"/>:
/// <see langword="true"/>, <see langword="false"/> or <see langword="null"/> (unknown). Pure and side-effect free.
/// </summary>
public static class AutomationConditionEvaluator
{
    /// <summary>True when <paramref name="condition"/> is absent or evaluates to true.</summary>
    public static bool Matches(AutomationCondition? condition, AutomationContext context) => condition is null || Evaluate(condition, context) == true;

    public static bool? Evaluate(AutomationCondition condition, AutomationContext context)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(context);
        var conversation = context.Conversation;
        return condition switch
        {
            AllOfCondition all => All(all.Items, context),
            AnyOfCondition any => Any(any.Items, context),
            NegatedCondition negated => Evaluate(negated.Operand, context) is { } value ? !value : null,
            StatusIsCondition status => status.Matches(conversation.Status.Kind),
            PriorityCondition priority => priority.Comparison switch
            {
                PriorityComparison.Equal => conversation.Priority == priority.Priority,
                PriorityComparison.NotEqual => conversation.Priority != priority.Priority,
                PriorityComparison.AtLeast => conversation.Priority >= priority.Priority,
                PriorityComparison.AtMost => conversation.Priority <= priority.Priority,
                _ => null,
            },
            InboxIsCondition inbox => Contains(inbox.Inboxes, conversation.Inbox),
            TeamIsCondition team => conversation.Team is { } teamId ? Contains(team.Teams, teamId) : team.OrNone,
            AssigneeIsCondition assignee => conversation.Assignee is { } user ? Contains(assignee.Users, user) : assignee.OrUnassigned,
            HasLabelsCondition labels => Labels(labels, labels.Target == ConditionTarget.Conversation ? conversation.Labels : context.Contact?.Labels),
            ChannelKindIsCondition channel => context.Channel is { } kind ? Contains(channel.Kinds, kind) : null,
            AttributeCondition attribute => Attribute(attribute, context),
            MessageContentCondition message => Message(message, context.Message),
            BusinessHoursCondition hours => BusinessHours(hours, context),
            ElapsedCondition elapsed => Elapsed(elapsed, context),
        };
    }

    private static bool Contains<T>(IReadOnlyList<T> items, T target) where T : IEquatable<T>
    {
        for (var i = 0; i < items.Count; i++)
            if (items[i].Equals(target))
                return true;
        return false;
    }

    private static bool? All(IReadOnlyList<AutomationCondition> items, AutomationContext context)
    {
        var unknown = false;
        for (var i = 0; i < items.Count; i++)
        {
            switch (Evaluate(items[i], context))
            {
                case false:
                    return false;
                case null:
                    unknown = true;
                    break;
            }
        }
        return unknown ? null : true;
    }

    private static bool? Any(IReadOnlyList<AutomationCondition> items, AutomationContext context)
    {
        var unknown = false;
        for (var i = 0; i < items.Count; i++)
        {
            switch (Evaluate(items[i], context))
            {
                case true:
                    return true;
                case null:
                    unknown = true;
                    break;
            }
        }
        return unknown ? null : false;
    }

    private static bool? Labels(HasLabelsCondition condition, IReadOnlyList<Core.LabelId>? labels)
    {
        if (labels is null)
            return null;
        var wanted = condition.Labels;
        switch (condition.Match)
        {
            case LabelMatch.All:
                for (var i = 0; i < wanted.Count; i++)
                    if (!Contains(labels, wanted[i]))
                        return false;
                return true;
            case LabelMatch.Any:
                for (var i = 0; i < wanted.Count; i++)
                    if (Contains(labels, wanted[i]))
                        return true;
                return false;
            case LabelMatch.None:
                for (var i = 0; i < wanted.Count; i++)
                    if (Contains(labels, wanted[i]))
                        return false;
                return true;
            default:
                return null;
        }
    }

    private static bool? Attribute(AttributeCondition condition, AutomationContext context)
    {
        IReadOnlyDictionary<string, AutomationAttributeFact> attributes;
        IReadOnlySet<string> masked;
        if (condition.Target == ConditionTarget.Conversation)
            (attributes, masked) = (context.Conversation.Attributes, context.Conversation.MaskedAttributes);
        else if (context.Contact is { } contact)
            (attributes, masked) = (contact.Attributes, contact.MaskedAttributes);
        else
            return null;
        if (masked.Contains(condition.Key))
            return null;
        if (!attributes.TryGetValue(condition.Key, out var fact))
            return condition.Operator == AttributeOperator.Exists ? false : null;
        var value = condition.Values.Count > 0 ? condition.Values[0] : null;
        return condition.Operator switch
        {
            AttributeOperator.Exists => true,
            AttributeOperator.Equal => fact.Scalar is { } scalar ? string.Equals(scalar, value, global::System.StringComparison.Ordinal) : null,
            AttributeOperator.NotEqual => fact.Scalar is { } scalar ? !string.Equals(scalar, value, global::System.StringComparison.Ordinal) : null,
            AttributeOperator.In => fact.Scalar is { } scalar ? condition.ContainsValue(scalar) : null,
            AttributeOperator.Contains => fact.Kind == AutomationAttributeKind.List
                ? ContainsOrdinal(fact.Items!, value!)
                : fact.Kind == AutomationAttributeKind.Text ? fact.Scalar!.Contains(value!, StringComparison.OrdinalIgnoreCase) : null,
            AttributeOperator.GreaterThan => Compare(fact, value!, condition.NumericValue) is { } order ? order > 0 : null,
            AttributeOperator.LessThan => Compare(fact, value!, condition.NumericValue) is { } order ? order < 0 : null,
            _ => null,
        };
    }

    private static bool ContainsOrdinal(IReadOnlyList<string> items, string target)
    {
        for (var i = 0; i < items.Count; i++)
            if (string.Equals(items[i], target, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static int? Compare(AutomationAttributeFact fact, string value, decimal? numericValue) => fact.Kind switch
    {
        AutomationAttributeKind.Number => numericValue is { } right
            && decimal.TryParse(fact.Scalar, NumberStyles.Number, CultureInfo.InvariantCulture, out var left) ? left.CompareTo(right) : null,
        AutomationAttributeKind.Date or AutomationAttributeKind.Text => string.CompareOrdinal(fact.Scalar, value),
        AutomationAttributeKind.Boolean or AutomationAttributeKind.List => null,
        _ => null,
    };

    private static bool? Message(MessageContentCondition condition, AutomationMessageFacts? message)
    {
        if (message is null)
            return null;
        var body = message.Body.Length > MessageContentCondition.MaximumInputLength
            ? message.Body.AsSpan(0, MessageContentCondition.MaximumInputLength)
            : message.Body.AsSpan();
        var comparison = condition.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        switch (condition.Operator)
        {
            case MessageTextOperator.Contains:
                return body.Contains(condition.Value.AsSpan(), comparison);
            case MessageTextOperator.NotContains:
                return !body.Contains(condition.Value.AsSpan(), comparison);
            case MessageTextOperator.StartsWith:
                return body.StartsWith(condition.Value.AsSpan(), comparison);
            case MessageTextOperator.EndsWith:
                return body.EndsWith(condition.Value.AsSpan(), comparison);
            case MessageTextOperator.Matches:
                try
                {
                    return condition.Regex!.IsMatch(body);
                }
                catch (RegexMatchTimeoutException)
                {
                    // Fail closed: a pattern that cannot finish within its timeout never fires a rule.
                    return null;
                }
            default:
                return null;
        }
    }

    private static bool? BusinessHours(BusinessHoursCondition condition, AutomationContext context)
    {
        if (context.Calendar is not { } calendar)
            return null;
        try
        {
            return calendar.IsOpen(context.Now) == condition.Open;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or TimeZoneNotFoundException)
        {
            return null;
        }
    }

    private static bool? Elapsed(ElapsedCondition condition, AutomationContext context)
    {
        var conversation = context.Conversation;
        DateTimeOffset? since = condition.Since switch
        {
            ConversationTimestamp.Created => conversation.CreatedAt,
            ConversationTimestamp.LastActivity => conversation.LastActivityAt,
            ConversationTimestamp.WaitingSince => conversation.WaitingSince,
            ConversationTimestamp.LastIncoming => conversation.LastIncomingAt,
            ConversationTimestamp.LastOutgoing => conversation.LastOutgoingAt,
            _ => null,
        };
        if (since is not { } instant)
            return null;
        var elapsed = context.Now - instant;
        return condition.Comparison switch
        {
            ElapsedComparison.AtLeast => elapsed >= condition.Duration,
            ElapsedComparison.LessThan => elapsed < condition.Duration,
            _ => null,
        };
    }
}
