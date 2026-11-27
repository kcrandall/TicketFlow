using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Net.Http.Headers;

namespace TicketFlow.Hosting.AspNetCore;

/// <summary>
/// The filter on every TicketFlow group: rejects oversized bodies (413), unresolved actors or scopes (400/401/404) and maps validation
/// failures thrown by domain constructors and codecs (<see cref="ArgumentException"/>, <see cref="FormatException"/>,
/// <see cref="JsonException"/>, <see cref="OverflowException"/>) to 422. Exception messages are returned only when TicketFlow code produced them (they never carry input
/// values); other messages are replaced with a generic text.
/// </summary>
internal sealed class TicketFlowEndpointFilter : IEndpointFilter
{
    /// <summary>Header that marks a body-less POST as a deliberate API call (any value).</summary>
    public const string RequestHeader = TicketFlowHttp.RequestHeader;

    public static Func<EndpointFilterFactoryContext, EndpointFilterDelegate, EndpointFilterDelegate> Create(long maximumBodyBytes, bool noStore = false) =>
        (factoryContext, next) =>
        {
            var parameters = factoryContext.MethodInfo.GetParameters();
            var actorIndex = -1;
            var scopeIndex = -1;
            for (var i = 0; i < parameters.Length; i++)
            {
                var type = parameters[i].ParameterType;
                if (type == typeof(TicketFlowActor))
                    actorIndex = i;
                else if (type == typeof(TicketFlowPublicScope))
                    scopeIndex = i;
            }
            return async context =>
            {
                var http = context.HttpContext;
                if (noStore)
                    StampNoStore(http.Response);
                if (http.Request.ContentLength is { } length && length > maximumBodyBytes)
                    return TicketFlowHttp.Problem(StatusCodes.Status413PayloadTooLarge, "payload-too-large");
                if (actorIndex >= 0 && context.GetArgument<TicketFlowActor?>(actorIndex) is { } actor)
                {
                    if (actor.FailureStatus > 0)
                        return TicketFlowHttp.Problem(actor.FailureStatus, actor.FailureCode ?? "unauthenticated");
                    if (HttpMethods.IsPost(http.Request.Method) && !IsJson(http.Request) && !http.Request.Headers.ContainsKey(RequestHeader))
                        return TicketFlowHttp.Problem(StatusCodes.Status400BadRequest, "json-or-header-required",
                            [$"POST requests need a JSON body (Content-Type: application/json) or the {RequestHeader} header."]);
                }
                if (scopeIndex >= 0 && context.GetArgument<TicketFlowPublicScope?>(scopeIndex) is { FailureStatus: > 0 } scope)
                    return TicketFlowHttp.Problem(scope.FailureStatus, scope.FailureCode ?? "not-found");
                try
                {
                    return await next(context).ConfigureAwait(false);
                }
                catch (Exception error) when (error is ArgumentException or FormatException or JsonException or OverflowException && !http.RequestAborted.IsCancellationRequested)
                {
                    return TicketFlowHttp.Problem(StatusCodes.Status422UnprocessableEntity, "invalid", [Message(error)]);
                }
            };
        };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.ContentLength is { } length && length > TicketFlowHttp.MaximumBody(http))
            return TicketFlowHttp.Problem(StatusCodes.Status413PayloadTooLarge, "payload-too-large");
        foreach (var argument in context.Arguments)
        {
            switch (argument)
            {
                case TicketFlowActor { FailureStatus: > 0 } actor:
                    return TicketFlowHttp.Problem(actor.FailureStatus, actor.FailureCode ?? "unauthenticated");
                case TicketFlowActor when HttpMethods.IsPost(http.Request.Method) && !IsJson(http.Request) && !http.Request.Headers.ContainsKey(RequestHeader):
                    // A cross-site page can send a body-less or form POST without a CORS preflight; a JSON content type or a custom
                    // header cannot be sent cross-origin without one, so cookie-authenticated hosts are not exposed to CSRF.
                    return TicketFlowHttp.Problem(StatusCodes.Status400BadRequest, "json-or-header-required",
                        [$"POST requests need a JSON body (Content-Type: application/json) or the {RequestHeader} header."]);
                case TicketFlowPublicScope { FailureStatus: > 0 } scope:
                    return TicketFlowHttp.Problem(scope.FailureStatus, scope.FailureCode ?? "not-found");
            }
        }
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or OverflowException && !http.RequestAborted.IsCancellationRequested)
        {
            return TicketFlowHttp.Problem(StatusCodes.Status422UnprocessableEntity, "invalid", [Message(error)]);
        }
    }

    private static void StampNoStore(HttpResponse response) =>
        response.OnStarting(static state =>
        {
            var httpResponse = (HttpResponse)state;
            if (httpResponse.Headers.CacheControl.Count == 0)
                httpResponse.Headers.CacheControl = "no-store";
            if (string.Equals(httpResponse.Headers.CacheControl.ToString(), "no-store", StringComparison.OrdinalIgnoreCase)
                && httpResponse.Headers.Pragma.Count == 0)
                httpResponse.Headers.Pragma = "no-cache";
            return Task.CompletedTask;
        }, response);

    private static bool IsJson(HttpRequest request)
    {
        if (request.ContentType is not { Length: > 0 } contentType
            || !MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            return false;
        var mediaType = parsed.MediaType;
        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            return true;
        return mediaType.Length > "application/+json".Length
            && mediaType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
            && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static string Message(Exception error)
    {
        var origin = error.TargetSite?.DeclaringType?.Assembly.GetName().Name;
        if (origin is null || !origin.StartsWith("TicketFlow.", StringComparison.Ordinal) || error.Message.Length > 512)
            return "The request is invalid.";
        // ArgumentException appends " (Parameter 'x')"; keep the parameter name, which is part of the contract.
        return error.Message;
    }
}

/// <summary>Request body limit metadata honored by the server's endpoint routing (Kestrel, HTTP.sys).</summary>
internal sealed class TicketFlowRequestSizeLimit(long maximum) : IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize { get; } = maximum;
}
