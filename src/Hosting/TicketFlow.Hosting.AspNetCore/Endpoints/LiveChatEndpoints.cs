using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Channels;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Workspaces;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class LiveChatEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/live-chat/widgets/{configurationId}", async (TicketFlowActor actor, string configurationId, [FromServices] ILiveChatWidgetService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, configurationId, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetWidget").Produces<WidgetResponse>();
        group.MapPost("/live-chat/widgets", async (TicketFlowActor actor, WidgetRequest request, [FromServices] ILiveChatWidgetService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Widget(request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"live-chat/widgets/{stored.Widget.ConfigurationId}", Dto(stored), stored.Revision)))
            .WithName("CreateWidget").Produces<WidgetResponse>(StatusCodes.Status201Created);
        group.MapPut("/live-chat/widgets/{configurationId}", async (TicketFlowActor actor, string configurationId, WidgetRequest request, [FromServices] ILiveChatWidgetService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            if (!string.Equals(request.ConfigurationId, configurationId, StringComparison.Ordinal))
                throw new ArgumentException("The body configuration must match the route.", nameof(configurationId));
            return Map(http, await service.UpdateAsync(actor.Principal, Widget(request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateWidget").Produces<WidgetResponse>();
        group.MapDelete("/live-chat/widgets/{configurationId}", async (TicketFlowActor actor, string configurationId, [FromServices] ILiveChatWidgetService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, configurationId, revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteWidget").Produces(StatusCodes.Status204NoContent);
        group.MapGet("/conversations/{conversationId}/visitor", async (TicketFlowActor actor, string conversationId, [FromServices] ILiveChatAgentService service, HttpContext http) =>
            Map(http, await service.GetVisitorAsync(actor.Principal, new ConversationId(conversationId), http.RequestAborted), visitor => TicketFlowHttp.Ok(http, new VisitorResponse(
                visitor.VisitorId, visitor.Verified, visitor.IdentityNamespace, visitor.IdentityValue, visitor.DisplayName,
                [.. visitor.Prechat.Select(answer => new PrechatAnswerResponse(answer.Key, answer.Label, answer.Value, answer.Masked))], visitor.SessionCreatedAt,
                Attributes.Masked(visitor.MaskedFields)))))
            .WithName("GetConversationVisitor").Produces<VisitorResponse>();

        group.MapGet("/channels/{kind}/{configurationId}", async (TicketFlowActor actor, string kind, string configurationId, [FromServices] IChannelEndpointService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new ChannelKind(kind), configurationId, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetChannelEndpoint").Produces<ChannelEndpointResponse>();
        group.MapPost("/channels", async (TicketFlowActor actor, ChannelEndpointRequest request, [FromServices] IChannelEndpointService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Endpoint(request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"channels/{stored.Endpoint.Kind.Value}/{stored.Endpoint.ConfigurationId}", Dto(stored), stored.Revision)))
            .WithName("CreateChannelEndpoint").Produces<ChannelEndpointResponse>(StatusCodes.Status201Created);
        group.MapPut("/channels/{kind}/{configurationId}", async (TicketFlowActor actor, string kind, string configurationId, ChannelEndpointRequest request, [FromServices] IChannelEndpointService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            if (!string.Equals(request.Kind, kind, StringComparison.Ordinal) || !string.Equals(request.ConfigurationId, configurationId, StringComparison.Ordinal))
                throw new ArgumentException("The body kind and configuration must match the route.", nameof(configurationId));
            return Map(http, await service.UpdateAsync(actor.Principal, Endpoint(request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateChannelEndpoint").Produces<ChannelEndpointResponse>();
        group.MapDelete("/channels/{kind}/{configurationId}", async (TicketFlowActor actor, string kind, string configurationId, [FromServices] IChannelEndpointService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new ChannelKind(kind), configurationId, revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteChannelEndpoint").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    private static IResult Map<T>(HttpContext http, TicketResult<T, LiveChatRefused> result, Func<T, IResult> success) => result switch
    {
        TicketValue<T> value => success(value.Value),
        LiveChatRefused refused => refused.Reason switch
        {
            LiveChatRefusal.Forbidden => TicketFlowHttp.Refused(http, RefusalKind.Forbidden, refused.Errors),
            LiveChatRefusal.NotFound => TicketFlowHttp.Refused(http, RefusalKind.NotFound, refused.Errors),
            LiveChatRefusal.Invalid => TicketFlowHttp.Refused(http, RefusalKind.Invalid, refused.Errors),
            LiveChatRefusal.Conflict => TicketFlowHttp.Problem(TicketFlowHttp.HasIfMatch(http) ? StatusCodes.Status412PreconditionFailed : StatusCodes.Status409Conflict,
                TicketFlowHttp.HasIfMatch(http) ? "revision-mismatch" : "conflict", refused.Errors),
            LiveChatRefusal.Unavailable => TicketFlowHttp.Refused(http, RefusalKind.Unavailable, refused.Errors),
            _ => TicketFlowHttp.Refused(http, RefusalKind.Rejected, refused.Errors),
        },
    };

    private static IResult Map<T>(HttpContext http, TicketResult<T> result, Func<T, IResult> success) => TicketFlowHttp.Map(http, result, success);

    private static LiveChatWidget Widget(WidgetRequest request) =>
        LiveChatCodec.DecodeWidget(request.ConfigurationId, new InboxId(request.InboxId), Json.Raw(request.Definition, "definition"));

    private static WidgetResponse Dto(StoredLiveChatWidget stored) =>
        new(stored.Widget.ConfigurationId, stored.Widget.Inbox.Value, Json.Parse(LiveChatCodec.EncodeWidget(stored.Widget)), stored.Revision);

    private static ChannelEndpoint Endpoint(ChannelEndpointRequest request) =>
        new(new ChannelKind(request.Kind), request.ConfigurationId, new InboxId(request.InboxId), request.Address);

    private static ChannelEndpointResponse Dto(StoredChannelEndpoint stored) =>
        new(stored.Endpoint.Kind.Value, stored.Endpoint.ConfigurationId, stored.Endpoint.Inbox.Value, stored.Endpoint.Address, stored.Revision);
}
