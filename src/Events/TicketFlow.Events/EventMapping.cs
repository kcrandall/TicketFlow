using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;
using TicketFlow.Surveys;

namespace TicketFlow.Events;

/// <summary>Maps feature values to the event contract's string vocabularies.</summary>
public static class EventMapping
{
    public static string Status(ConversationStatusKind kind) => kind.WireName;

    public static string Priority(ConversationPriority priority) => priority.WireName;

    public static string ContentType(MessageContentType type) => type switch
    {
        MessageContentType.PlainText => "plain-text",
        MessageContentType.Markdown => "markdown",
        MessageContentType.Html => "html",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string Delivery(MessageDeliveryState state) => state switch
    {
        MessageDeliveryState.Pending => "pending",
        MessageDeliveryState.Sent => "sent",
        MessageDeliveryState.Delivered => "delivered",
        MessageDeliveryState.Read => "read",
        MessageDeliveryState.Failed => "failed",
        MessageDeliveryState.Received => "received",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static string Metric(SlaMetric metric) => metric.WireName;

    public static string Basis(SlaClockBasis basis) => basis switch
    {
        SlaClockBasis.BusinessHours => "business-hours",
        SlaClockBasis.CalendarTime => "calendar-time",
        _ => throw new ArgumentOutOfRangeException(nameof(basis)),
    };

    public static string Survey(SurveyKind kind) => kind switch
    {
        SurveyKind.Csat => "csat",
        SurveyKind.Nps => "nps",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Identity(ContactIdentityKind kind) => kind switch
    {
        ContactIdentityKind.Email => "email",
        ContactIdentityKind.Phone => "phone",
        ContactIdentityKind.WhatsApp => "whatsapp",
        ContactIdentityKind.WidgetVisitor => "widget-visitor",
        ContactIdentityKind.ExternalUser => "external-user",
        ContactIdentityKind.SocialHandle => "social-handle",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static EventActor Actor(AccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(actor.Kind switch
        {
            AccessActorKind.User => "user",
            AccessActorKind.ApiClient => "api-client",
            AccessActorKind.Automation => "automation",
            AccessActorKind.System => "system",
            _ => throw new ArgumentOutOfRangeException(nameof(actor)),
        }, actor.Id);
    }

    /// <summary>The resource snapshot of a conversation event.</summary>
    public static EventResource Resource(Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return new(conversation.Id, conversation.Inbox, conversation.Team, conversation.Assignee, conversation.Contact);
    }
}
