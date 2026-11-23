using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Realtime.SignalR;

public static class TicketFlowSignalRExtensions
{
    public const string FileNameHeader = "X-TicketFlow-File-Name";

    /// <summary>
    /// Registers SignalR and the TicketFlow hub services: options, the options-based <see cref="IRealtimePrincipalResolver"/> and
    /// <see cref="IVisitorPartitionResolver"/> fallbacks (application registrations win). Requires <c>AddRealtime()</c> and, for the
    /// visitor hub, <c>AddLiveChat()</c>. For multiple nodes add a SignalR backplane (for example Redis) and a cross-node
    /// <see cref="IRealtimeBus"/>; see docs/realtime.md.
    /// </summary>
    public static TicketFlowBuilder AddTicketFlowSignalR(this TicketFlowBuilder builder, Action<TicketFlowSignalROptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<TicketFlowSignalROptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddSignalR();
        services.TryAddSingleton<IRealtimePrincipalResolver, OptionsRealtimePrincipalResolver>();
        services.TryAddSingleton<IVisitorPartitionResolver, OptionsPartitionResolver>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RealtimeTypingThrottle>();
        return builder;
    }

    /// <summary>Maps the agent hub (<paramref name="agentPattern"/>) and the visitor hub (<paramref name="visitorPattern"/>). Add host authorization to the agent hub as needed.</summary>
    public static (HubEndpointConventionBuilder Agent, HubEndpointConventionBuilder Visitor) MapTicketFlowRealtime(this IEndpointRouteBuilder endpoints,
        string agentPattern = "/ticketflow/realtime/agent", string visitorPattern = "/ticketflow/realtime/visitor")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return (endpoints.MapHub<AgentRealtimeHub>(agentPattern), endpoints.MapHub<VisitorRealtimeHub>(visitorPattern));
    }

    /// <summary>
    /// Maps <c>POST {pattern}</c>: a visitor attachment upload. Headers: <c>Authorization: Bearer {session token}</c>, <c>Content-Type</c>,
    /// <c>X-TicketFlow-File-Name</c> (URL-encoded) and the browser's <c>Origin</c>; the body is the raw file, streamed to the application's
    /// <see cref="IAttachmentStore"/> under the widget's size and type limits. Returns the attachment reference to send with a message.
    /// </summary>
    public static RouteHandlerBuilder MapLiveChatAttachmentUpload(this IEndpointRouteBuilder endpoints, string pattern = "/ticketflow/livechat/attachments")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapPost(pattern, async (HttpContext http, ILiveChatVisitorService visitors, IVisitorPartitionResolver partitions, IOptions<TicketFlowSignalROptions> options) =>
        {
            if (partitions.Resolve(http) is not { } partition)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var authorization = http.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
                return Results.Unauthorized();
            if (http.Request.ContentLength is { } length && length > options.Value.MaximumUploadBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var origin = http.Request.Headers.Origin.ToString() is { Length: > 0 } header ? header : null;
            var session = await visitors.AuthenticateAsync(partition, authorization["Bearer ".Length..].Trim(), origin, http.RequestAborted).ConfigureAwait(false);
            if (session.Value is not TicketValue<VisitorSessionContext> authenticated)
                return session.Value is LiveChatRefused { Reason: LiveChatRefusal.OriginNotAllowed } ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Unauthorized();
            var fileName = Uri.UnescapeDataString(http.Request.Headers[FileNameHeader].ToString());
            var result = await visitors.UploadAttachmentAsync(authenticated.Value, fileName, http.Request.ContentType ?? string.Empty, http.Request.Body, http.RequestAborted).ConfigureAwait(false);
            return result.Value switch
            {
                TicketValue<AttachmentRef> stored => Results.Json(RealtimeWireFormat.Attachment(stored.Value), JsonSerializerOptions.Web),
                LiveChatRefused { Reason: LiveChatRefusal.TooLarge } => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
                LiveChatRefused { Reason: LiveChatRefusal.UnsupportedMediaType } => Results.StatusCode(StatusCodes.Status415UnsupportedMediaType),
                LiveChatRefused { Reason: LiveChatRefusal.Unauthenticated } => Results.Unauthorized(),
                LiveChatRefused { Reason: LiveChatRefusal.Unavailable } => Results.NotFound(),
                _ => Results.BadRequest(),
            };
        }).DisableAntiforgery();
    }
}
