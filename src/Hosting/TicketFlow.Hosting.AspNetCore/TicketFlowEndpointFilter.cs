using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace TicketFlow.Hosting.AspNetCore;

/// <summary>
/// The filter on every TicketFlow group: rejects oversized bodies (413), unresolved actors or scopes (400/401/404) and maps validation
/// failures thrown by domain constructors and codecs (<see cref="ArgumentException"/>, <see cref="FormatException"/>,
/// <see cref="JsonException"/>) to 422. Exception messages are returned only when TicketFlow code produced them (they never carry input
/// values); other messages are replaced with a generic text.
/// </summary>
internal sealed class TicketFlowEndpointFilter : IEndpointFilter
{
    /// <summary>Header that marks a body-less POST as a deliberate API call (any value).</summary>
    public const string RequestHeader = TicketFlowHttp.RequestHeader;

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
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException && !http.RequestAborted.IsCancellationRequested)
        {
            return TicketFlowHttp.Problem(StatusCodes.Status422UnprocessableEntity, "invalid", [Message(error)]);
        }
    }

    private static bool IsJson(HttpRequest request) =>
        request.ContentType is { } type && (type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || type.Contains("+json", StringComparison.OrdinalIgnoreCase));

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
