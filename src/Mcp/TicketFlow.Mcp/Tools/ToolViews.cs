using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;

namespace TicketFlow.Mcp.Tools;

/// <summary>
/// Result projections shared by the toolsets. They follow the hosting DTO conventions (string identifiers, camelCase tokens, one
/// populated payload per <c>kind</c>) and only ever carry what the masked domain views carry: masked values are absent and listed in
/// <c>maskedFields</c>.
/// </summary>
internal static class ToolViews
{
    public static IReadOnlyList<object> Masked(IEnumerable<MaskedField> fields) =>
        [.. fields.Select(field => (object)new { field = field.Field.Value, revealable = field.Revealable })];

    public static object Actor(AccessActor actor) => new { kind = TicketFlowToolJson.Token(actor.Kind), id = actor.Id };

    public static object Value(ConversationAttributeValue value) => value switch
    {
        ConversationTextValue text => new { type = "text", text = (object)text.Value },
        ConversationNumberValue number => new { type = "number", number = (object)number.Value },
        ConversationBooleanValue boolean => new { type = "boolean", boolean = (object)boolean.Value },
        ConversationDateValue date => new { type = "date", date = (object)date.Value },
        ConversationListValue list => new { type = "list", items = (object)list.Items },
    };

    public static object Value(ContactAttributeValue value) => value switch
    {
        TextAttributeValue text => new { type = "text", text = (object)text.Value },
        NumberAttributeValue number => new { type = "number", number = (object)number.Value },
        BooleanAttributeValue boolean => new { type = "boolean", boolean = (object)boolean.Value },
        DateAttributeValue date => new { type = "date", date = (object)date.Value },
        ListAttributeValue list => new { type = "list", items = (object)list.Items },
    };

    public static object Status(ConversationStatus status) => new { kind = TicketFlowToolJson.Token(status.Kind), snoozedUntil = status.SnoozedUntil };

    public static object Conversation(ConversationView view) => new
    {
        id = view.Id.Value,
        revision = view.Revision,
        inboxId = view.Inbox.Value,
        contactId = view.Contact.Value,
        status = Status(view.Status),
        priority = TicketFlowToolJson.Token(view.Priority),
        assigneeId = view.Assignee?.Value,
        teamId = view.Team?.Value,
        labels = view.Labels.Select(label => label.Value).ToList(),
        attributes = view.Attributes.Select(attribute => new
        {
            key = attribute.Key,
            type = TicketFlowToolJson.Token(attribute.Type),
            sensitive = attribute.Sensitive,
            value = attribute.Value is { } value ? Value(value) : null,
            masked = attribute.Masked,
            revealable = attribute.Revealable,
        }).ToList(),
        participants = view.Participants.Select(participant => participant.Value).ToList(),
        channelThread = view.ChannelThread is { } thread ? new { channel = thread.Channel, externalThreadId = thread.ExternalThreadId, masked = thread.Masked, revealable = thread.Revealable } : null,
        createdAt = view.CreatedAt,
        lastActivityAt = view.LastActivityAt,
        waitingSince = view.WaitingSince,
        firstReplyAt = view.FirstReplyAt,
        lastSequence = view.LastSequence,
        maskedFields = Masked(view.MaskedFields),
    };

    /// <summary>A conversation in a list: the identifying and triage fields only (use <c>conversations_get</c> for attributes).</summary>
    public static object ConversationSummary(ConversationView view) => new
    {
        id = view.Id.Value,
        revision = view.Revision,
        inboxId = view.Inbox.Value,
        contactId = view.Contact.Value,
        status = Status(view.Status),
        priority = TicketFlowToolJson.Token(view.Priority),
        assigneeId = view.Assignee?.Value,
        teamId = view.Team?.Value,
        labels = view.Labels.Select(label => label.Value).ToList(),
        lastActivityAt = view.LastActivityAt,
        waitingSince = view.WaitingSince,
        maskedFields = Masked(view.MaskedFields),
    };

    public static object Command(ConversationApplied applied) => new
    {
        conversationId = applied.Conversation.Value,
        revision = applied.Revision,
        sequences = applied.Events.Select(item => item.Sequence).ToList(),
        replayed = applied.Replayed,
    };

    public static object TimelineEvent(TimelineEventView view, TicketFlowToolContext context)
    {
        var item = view.Event;
        object? payload = item.Event switch
        {
            MessageEvent message => Message(message, context),
            NoteEvent note => new { id = note.Id.Value, body = context.Text(note.Body, out var truncated), truncated, attachments = note.Attachments.Count },
            AssignmentChanged assignment => new { previous = assignment.Previous?.Value, current = assignment.Current?.Value },
            TeamChanged team => new { previous = team.Previous?.Value, current = team.Current?.Value },
            StatusChanged status => new { previous = Status(status.Previous), current = Status(status.Current) },
            PriorityChanged priority => new { previous = TicketFlowToolJson.Token(priority.Previous), current = TicketFlowToolJson.Token(priority.Current) },
            LabelsChanged labels => new { added = labels.Added.Select(label => label.Value).ToList(), removed = labels.Removed.Select(label => label.Value).ToList() },
            AttributeChanged attribute => new
            {
                key = attribute.Key,
                previous = attribute.Previous is { } previous ? Value(previous) : null,
                current = attribute.Current is { } current ? Value(current) : null,
            },
            ParticipantChanged participant => new { userId = participant.Participant.Value, change = TicketFlowToolJson.Token(participant.Change) },
            ContactChanged contact => new { previous = contact.Previous.Value, current = contact.Current.Value, merge = contact.Merge },
            SystemEvent system => new { code = system.Code, detail = system.Detail },
            ExtensionEvent extension => new { kind = extension.Kind, payloadVersion = extension.PayloadVersion },
        };
        return new
        {
            id = item.Id,
            sequence = item.Sequence,
            occurredAt = item.OccurredAt,
            actor = Actor(item.Actor),
            kind = TicketFlowToolJson.Token(item.Event.Kind),
            data = payload,
            maskedFields = Masked(view.MaskedFields),
        };
    }

    private static object Message(MessageEvent message, TicketFlowToolContext context) => new
    {
        id = message.Id.Value,
        direction = TicketFlowToolJson.Token(message.Direction),
        sender = message.Sender switch
        {
            ContactParty contact => new { kind = "contact", contactId = (string?)contact.Contact.Value, actor = (object?)null },
            PrincipalParty principal => new { kind = "principal", contactId = (string?)null, actor = (object?)Actor(principal.Actor) },
        },
        body = context.Text(message.Body, out var truncated),
        truncated,
        contentType = TicketFlowToolJson.Token(message.ContentType),
        attachments = message.Attachments.Select(attachment => new { fileName = attachment.FileName, mediaType = attachment.MediaType, sizeBytes = attachment.SizeBytes }).ToList(),
        delivery = new { state = TicketFlowToolJson.Token(message.Delivery.State), updatedAt = message.Delivery.UpdatedAt, failureCode = message.Delivery.FailureCode },
    };

    public static object Contact(ContactView view) => new
    {
        id = view.Id.Value,
        revision = view.Revision,
        profile = new
        {
            displayName = view.Profile.DisplayName,
            company = view.Profile.Company,
            locale = view.Profile.Locale,
            timeZone = view.Profile.TimeZone,
        },
        identities = view.Identities.Select(identity => new
        {
            kind = TicketFlowToolJson.Token(identity.Kind),
            @namespace = identity.Namespace,
            value = identity.Value,
            verification = identity.Verification switch { IdentityVerified => "verified", IdentityClaimed => "claimed" },
            masked = identity.Masked,
            revealable = identity.Revealable,
        }).ToList(),
        attributes = view.Attributes.Select(attribute => new
        {
            key = attribute.Key,
            type = TicketFlowToolJson.Token(attribute.Type),
            sensitive = attribute.Sensitive,
            value = attribute.Value is { } value ? Value(value) : null,
            masked = attribute.Masked,
            revealable = attribute.Revealable,
        }).ToList(),
        labels = view.Labels.Select(label => label.Value).ToList(),
        blocked = view.Blocked,
        createdAt = view.CreatedAt,
        updatedAt = view.UpdatedAt,
        maskedFields = Masked(view.MaskedFields),
    };

    public static TicketFlowToolError Refused(ConversationRefused refused) => refused.Reason switch
    {
        ConversationRefusal.Forbidden => TicketFlowToolError.Forbidden(),
        ConversationRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
        ConversationRefusal.Closed => new TicketFlowToolError("closed", "The conversation is closed and accepts no further changes.", refused.Errors),
        _ => TicketFlowToolError.Rejected(refused.Errors),
    };

    public static object Map(ConversationCommandResult result) => result switch
    {
        ConversationApplied applied => Command(applied),
        ConversationNotFound => TicketFlowToolError.NotFound("The conversation"),
        ConversationConflict conflict => TicketFlowToolError.Conflict(conflict.CurrentRevision),
        ConversationRefused refused => Refused(refused),
    };

    public static TicketFlowToolError Refused(ContactRefused refused) => refused.Reason switch
    {
        ContactRefusal.Forbidden => TicketFlowToolError.Forbidden(),
        ContactRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
        ContactRefusal.NotFound => TicketFlowToolError.NotFound("The contact"),
        ContactRefusal.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision),
        ContactRefusal.IdentityTaken => new TicketFlowToolError("identity-taken", "A verified identity is already held by another contact.", refused.Errors),
        ContactRefusal.Merged => new TicketFlowToolError("merged", "The contact was merged into another contact.", refused.Errors,
            Details: new Dictionary<string, object?> { ["mergedInto"] = refused.MergedInto?.Value }),
        _ => TicketFlowToolError.Rejected(refused.Errors),
    };
}
