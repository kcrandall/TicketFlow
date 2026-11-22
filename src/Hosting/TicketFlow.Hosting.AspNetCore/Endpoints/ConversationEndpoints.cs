using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class ConversationEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/conversations", async (TicketFlowActor actor, [FromServices] IConversationService service, HttpContext http, string? inbox, string? status, string? assignee,
            bool? unassigned, string? team, string? label, string? contact, int? limit, string? cursor) =>
        {
            UserId? assigneeId = assignee switch
            {
                null => null,
                "me" => actor.UserId ?? throw new ArgumentException("'me' requires a user principal.", nameof(assignee)),
                _ => new UserId(assignee),
            };
            var query = new ConversationListQuery(inbox is null ? null : new InboxId(inbox), TicketFlowHttp.ParseOptional<ConversationStatusKind>(status, "status"), assigneeId,
                unassigned ?? false, team is null ? null : new TeamId(team), label is null ? null : new LabelId(label), contact is null ? null : new ContactId(contact),
                limit ?? ConversationListQuery.DefaultLimit);
            return await service.ListAsync(actor.Principal, query, cursor, http.RequestAborted) switch
            {
                ConversationListPage page => TicketFlowHttp.Ok(http, new PageResponse<ConversationResponse>([.. page.Conversations.Select(Dto)], page.NextCursor)),
                ConversationRefused refused => Refused(http, refused),
            };
        }).WithName("ListConversations").Produces<PageResponse<ConversationResponse>>();
        group.MapPost("/conversations", async (TicketFlowActor actor, ConversationStartRequest request, [FromServices] IConversationService service, HttpContext http) =>
        {
            var start = new StartConversationRequest(new InboxId(request.InboxId), new ContactId(request.ContactId), null,
                request.Priority is null ? ConversationPriority.None : TicketFlowHttp.Parse<ConversationPriority>(request.Priority, "priority"),
                request.Labels?.Select(label => new LabelId(label)).ToList(),
                request.Attributes?.ToDictionary(pair => pair.Key, pair => Attributes.Conversation(pair.Value), StringComparer.Ordinal),
                request.TeamId is null ? null : new TeamId(request.TeamId), request.AssigneeId is null ? null : new UserId(request.AssigneeId),
                request.InitialMessage is { } message ? new IncomingMessageDraft(message.Body, ContentType(message.ContentType), Attachments(message.Attachments), Id: MessageId(message.Id)) : null,
                request.Id is null ? null : new ConversationId(request.Id));
            return await service.StartAsync(actor.Principal, start, http.RequestAborted) switch
            {
                ConversationApplied applied => TicketFlowHttp.Created(http, $"conversations/{applied.Conversation.Value}", Command(applied), applied.Revision),
                ConversationNotFound => TicketFlowHttp.NotFound(),
                ConversationConflict conflict => TicketFlowHttp.Conflict(http, conflict.CurrentRevision),
                ConversationRefused refused => Refused(http, refused),
            };
        }).WithName("StartConversation").Produces<ConversationCommandResponse>(StatusCodes.Status201Created);
        group.MapGet("/conversations/{conversationId}", async (TicketFlowActor actor, string conversationId, [FromServices] IConversationService service, HttpContext http) =>
            await service.GetAsync(actor.Principal, new ConversationId(conversationId), http.RequestAborted) switch
            {
                ConversationView view => TicketFlowHttp.Ok(http, Dto(view), view.Revision),
                ConversationNotFound => TicketFlowHttp.NotFound(),
                ConversationRefused refused => Refused(http, refused),
            }).WithName("GetConversation").Produces<ConversationResponse>();
        group.MapGet("/conversations/{conversationId}/timeline", async (TicketFlowActor actor, string conversationId, long? after, int? limit, [FromServices] IConversationService service, HttpContext http) =>
            await service.ReadTimelineAsync(actor.Principal, new ConversationId(conversationId), after ?? 0, limit ?? 50, http.RequestAborted) switch
            {
                TimelinePage page => TicketFlowHttp.Ok(http, new TimelinePageResponse(page.Conversation.Value, [.. page.Events.Select(Dto)], page.NextAfterSequence, page.NotesIncluded)),
                ConversationNotFound => TicketFlowHttp.NotFound(),
                ConversationRefused refused => Refused(http, refused),
            }).WithName("ReadTimeline").Produces<TimelinePageResponse>();

        Command<MessageInput>(group, "/conversations/{conversationId}/replies", "Reply", (service, actor, id, body, revision, ct) =>
            service.ReplyAsync(actor.Principal, id, new OutgoingMessageDraft(body.Body, ContentType(body.ContentType), Attachments(body.Attachments), MessageId(body.Id)), revision, ct));
        Command<NoteInput>(group, "/conversations/{conversationId}/notes", "AddNote", (service, actor, id, body, revision, ct) =>
            service.AddNoteAsync(actor.Principal, id, new NoteDraft(body.Body, Attachments(body.Attachments), MessageId(body.Id)), revision, ct));
        Command<AssigneeRequest>(group, "/conversations/{conversationId}/assignee", "Assign", (service, actor, id, body, revision, ct) =>
            service.AssignAsync(actor.Principal, id, body.UserId is null ? null : new UserId(body.UserId), revision, ct), put: true);
        Command<TeamAssignmentRequest>(group, "/conversations/{conversationId}/team", "SetTeam", (service, actor, id, body, revision, ct) =>
            service.SetTeamAsync(actor.Principal, id, body.TeamId is null ? null : new TeamId(body.TeamId), revision, ct), put: true);
        Command<StatusRequest>(group, "/conversations/{conversationId}/status", "ChangeStatus", (service, actor, id, body, revision, ct) =>
            service.ChangeStatusAsync(actor.Principal, id, Status(body), revision, ct), put: true);
        Command<SnoozeRequest>(group, "/conversations/{conversationId}/snooze", "Snooze", (service, actor, id, body, revision, ct) =>
            service.SnoozeAsync(actor.Principal, id, body.Until, revision, ct));
        Command<PriorityRequest>(group, "/conversations/{conversationId}/priority", "SetPriority", (service, actor, id, body, revision, ct) =>
            service.SetPriorityAsync(actor.Principal, id, TicketFlowHttp.Parse<ConversationPriority>(body.Priority, "priority"), revision, ct), put: true);
        Command<LabelsRequest>(group, "/conversations/{conversationId}/labels", "UpdateLabels", (service, actor, id, body, revision, ct) =>
            service.UpdateLabelsAsync(actor.Principal, id, (body.Add ?? []).Select(label => new LabelId(label)), (body.Remove ?? []).Select(label => new LabelId(label)), revision, ct));
        Command<AttributesRequest>(group, "/conversations/{conversationId}/attributes", "UpdateAttributes", (service, actor, id, body, revision, ct) =>
            service.UpdateAttributesAsync(actor.Principal, id, (body.Changes ?? throw new ArgumentException("Changes are required.", "changes"))
                .ToDictionary(pair => pair.Key, pair => pair.Value is null ? (ConversationAttributeValue?)null : Attributes.Conversation(pair.Value), StringComparer.Ordinal), revision, ct));
        Command<ParticipantsRequest>(group, "/conversations/{conversationId}/participants", "UpdateParticipants", (service, actor, id, body, revision, ct) =>
            service.UpdateParticipantsAsync(actor.Principal, id, (body.Add ?? []).Select(user => new UserId(user)), (body.Remove ?? []).Select(user => new UserId(user)), revision, ct));
        group.MapDelete("/conversations/{conversationId}", async (TicketFlowActor actor, string conversationId, [FromServices] IConversationService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new ConversationId(conversationId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteConversation").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/conversation-attributes", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IConversationAttributeDefinitionService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Definition.Key, Definition, limit, cursor))))
            .WithName("ListConversationAttributes").Produces<PageResponse<AttributeDefinitionResponse>>();
        group.MapPost("/conversation-attributes", async (TicketFlowActor actor, AttributeDefinitionRequest request, [FromServices] IConversationAttributeDefinitionService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Definition(request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"conversation-attributes/{stored.Definition.Key}", Definition(stored), stored.Revision)))
            .WithName("CreateConversationAttribute").Produces<AttributeDefinitionResponse>(StatusCodes.Status201Created);
        group.MapPut("/conversation-attributes/{key}", async (TicketFlowActor actor, string key, AttributeDefinitionRequest request, [FromServices] IConversationAttributeDefinitionService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            if (request.Key != key)
                throw new ArgumentException("The body key must match the route.", nameof(key));
            return Map(http, await service.UpdateAsync(actor.Principal, Definition(request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Definition(stored), stored.Revision));
        }).WithName("UpdateConversationAttribute").Produces<AttributeDefinitionResponse>();
        group.MapDelete("/conversation-attributes/{key}", async (TicketFlowActor actor, string key, [FromServices] IConversationAttributeDefinitionService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, key, revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteConversationAttribute").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    private delegate Task<ConversationCommandResult> CommandHandler<TBody>(IConversationService service, TicketFlowActor actor, ConversationId conversation, TBody body, long? expectedRevision, CancellationToken cancellationToken);

    /// <summary>A conversation command: optional <c>If-Match</c> fencing (412 on mismatch), result as <see cref="ConversationCommandResponse"/> with the new ETag.</summary>
    private static void Command<TBody>(RouteGroupBuilder group, string pattern, string name, CommandHandler<TBody> handler, bool put = false)
    {
        var handle = async (TicketFlowActor actor, string conversationId, TBody body, [FromServices] IConversationService service, HttpContext http) =>
        {
            ArgumentNullException.ThrowIfNull(body, "body");
            if (!TicketFlowHttp.TryOptionalRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await handler(service, actor, new ConversationId(conversationId), body, revision, http.RequestAborted), applied => TicketFlowHttp.Ok(http, Command(applied), applied.Revision));
        };
        (put ? group.MapPut(pattern, handle) : group.MapPost(pattern, handle)).WithName(name).Produces<ConversationCommandResponse>().ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    internal static IResult Map(HttpContext http, ConversationCommandResult result, Func<ConversationApplied, IResult> success) => result switch
    {
        ConversationApplied applied => success(applied),
        ConversationNotFound => TicketFlowHttp.NotFound(),
        ConversationConflict conflict => TicketFlowHttp.Conflict(http, conflict.CurrentRevision),
        ConversationRefused refused => Refused(http, refused),
    };

    private static IResult Map<T>(HttpContext http, ConversationResult<T> result, Func<T, IResult> success) => result switch
    {
        ConversationValue<T> value => success(value.Value),
        ConversationNotFound => TicketFlowHttp.NotFound(),
        ConversationConflict conflict => TicketFlowHttp.Conflict(http, conflict.CurrentRevision),
        ConversationRefused refused => Refused(http, refused),
    };

    internal static IResult Refused(HttpContext http, ConversationRefused refused) => TicketFlowHttp.Refused(http, refused.Reason switch
    {
        ConversationRefusal.Forbidden => RefusalKind.Forbidden,
        ConversationRefusal.Invalid => RefusalKind.Invalid,
        ConversationRefusal.Closed => RefusalKind.Closed,
        _ => RefusalKind.Rejected,
    }, refused.Errors);

    private static ConversationCommandResponse Command(ConversationApplied applied) =>
        new(applied.Conversation.Value, applied.Revision, [.. applied.Events.Select(item => item.Sequence)], applied.Replayed);

    private static ConversationStatus Status(StatusRequest request) => TicketFlowHttp.Parse<ConversationStatusKind>(request.Status, "status") switch
    {
        ConversationStatusKind.Open => ConversationStatus.Open,
        ConversationStatusKind.Pending => ConversationStatus.Pending,
        ConversationStatusKind.Snoozed => ConversationStatus.Snoozed(request.SnoozedUntil ?? throw new ArgumentException("Snoozing requires 'snoozedUntil'.", "snoozedUntil")),
        ConversationStatusKind.Resolved => ConversationStatus.Resolved,
        _ => ConversationStatus.Closed,
    };

    private static MessageContentType ContentType(string? value) => value is null ? MessageContentType.PlainText : TicketFlowHttp.Parse<MessageContentType>(value, "contentType");

    private static MessageId? MessageId(string? value) => value is null ? null : new MessageId(value);

    private static IReadOnlyList<AttachmentRef>? Attachments(IReadOnlyList<AttachmentModel>? attachments) =>
        attachments?.Select(attachment => new AttachmentRef(attachment.StorageRef, attachment.FileName, attachment.MediaType, attachment.SizeBytes)).ToList();

    internal static ConversationStatusResponse Status(ConversationStatus status) => new(TicketFlowHttp.Token(status.Kind), status.SnoozedUntil);

    internal static ActorResponse Actor(AccessActor actor) => new(TicketFlowHttp.Token(actor.Kind), actor.Id);

    internal static ConversationResponse Dto(ConversationView view) => new(
        view.Id.Value,
        view.Revision,
        view.Inbox.Value,
        view.Contact.Value,
        Status(view.Status),
        TicketFlowHttp.Token(view.Priority),
        view.Assignee?.Value,
        view.Team?.Value,
        [.. view.Labels.Select(label => label.Value)],
        [.. view.Attributes.Select(attribute => new ConversationAttributeResponse(attribute.Key, TicketFlowHttp.Token(attribute.Type), attribute.Sensitive,
            attribute.Value is { } value ? Attributes.Dto(value) : null, attribute.Masked, attribute.Revealable))],
        [.. view.Participants.Select(participant => participant.Value)],
        view.ChannelThread is { } thread ? new ChannelThreadResponse(thread.Channel, thread.ExternalThreadId, thread.Masked, thread.Revealable) : null,
        view.CreatedAt,
        view.LastActivityAt,
        view.WaitingSince,
        view.FirstReplyAt,
        view.LastIncomingAt,
        view.LastOutgoingAt,
        view.LastSequence,
        Attributes.Masked(view.MaskedFields));

    internal static TimelineEventResponse Dto(TimelineEventView view)
    {
        var item = view.Event;
        var masked = Attributes.Masked(view.MaskedFields);
        var dto = new TimelineEventResponse(item.Id, item.Sequence, item.OccurredAt, Actor(item.Actor), TicketFlowHttp.Token(item.Event.Kind), masked);
        return item.Event switch
        {
            MessageEvent message => dto with
            {
                Message = new TimelineMessageResponse(message.Id.Value, TicketFlowHttp.Token(message.Direction), message.Sender switch
                {
                    ContactParty contact => new PartyResponse("contact", ContactId: contact.Contact.Value),
                    PrincipalParty principal => new PartyResponse("principal", Actor: Actor(principal.Actor)),
                }, message.Body, TicketFlowHttp.Token(message.ContentType), Attachments(message.Attachments), message.ExternalId,
                    new DeliveryResponse(TicketFlowHttp.Token(message.Delivery.State), message.Delivery.UpdatedAt, message.Delivery.FailureCode)),
            },
            NoteEvent note => dto with { Note = new TimelineNoteResponse(note.Id.Value, note.Body, Attachments(note.Attachments)) },
            AssignmentChanged assignment => dto with { Assignment = new TimelineValueChangeResponse(assignment.Previous?.Value, assignment.Current?.Value) },
            TeamChanged team => dto with { Team = new TimelineValueChangeResponse(team.Previous?.Value, team.Current?.Value) },
            StatusChanged status => dto with { Status = new TimelineStatusChangeResponse(Status(status.Previous), Status(status.Current)) },
            PriorityChanged priority => dto with { Priority = new TimelineValueChangeResponse(TicketFlowHttp.Token(priority.Previous), TicketFlowHttp.Token(priority.Current)) },
            LabelsChanged labels => dto with { Labels = new TimelineLabelsChangeResponse([.. labels.Added.Select(label => label.Value)], [.. labels.Removed.Select(label => label.Value)]) },
            AttributeChanged attribute => dto with
            {
                Attribute = new TimelineAttributeChangeResponse(attribute.Key, attribute.Previous is { } previous ? Attributes.Dto(previous) : null,
                    attribute.Current is { } current ? Attributes.Dto(current) : null),
            },
            ParticipantChanged participant => dto with { Participant = new TimelineParticipantChangeResponse(participant.Participant.Value, TicketFlowHttp.Token(participant.Change)) },
            ContactChanged contact => dto with { Contact = new TimelineContactChangeResponse(contact.Previous.Value, contact.Current.Value, contact.Merge) },
            SystemEvent system => dto with { System = new TimelineSystemResponse(system.Code, system.Detail) },
            ExtensionEvent extension => dto with { Extension = new TimelineExtensionResponse(extension.Kind, extension.PayloadVersion, Json.Parse(extension.Payload)) },
        };
    }

    private static IReadOnlyList<AttachmentModel> Attachments(IReadOnlyList<AttachmentRef> attachments) =>
        [.. attachments.Select(attachment => new AttachmentModel(attachment.StorageRef, attachment.FileName, attachment.MediaType, attachment.SizeBytes))];

    private static ConversationAttributeDefinition Definition(AttributeDefinitionRequest request) =>
        new(request.Key, TicketFlowHttp.Parse<ConversationAttributeType>(request.Type, "type"), request.Sensitive, request.DisplayName);

    private static AttributeDefinitionResponse Definition(StoredConversationAttributeDefinition stored) =>
        new(stored.Definition.Key, TicketFlowHttp.Token(stored.Definition.Type), stored.Definition.Sensitive, stored.Definition.DisplayName, stored.Revision);
}
