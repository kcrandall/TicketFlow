using System.Globalization;
using Microsoft.Extensions.Options;
using TicketFlow.Channels.Api;

namespace TicketFlow.Automation;

/// <summary>
/// The identifiers-only JSON body of automation webhooks: <c>{"v":1,"event":"automation.webhook","rule":…,"conversation":…,"inbox":…,
/// "status":…,"priority":…,"assignee":…,"team":…,"labels":[…],"trigger":…,"sentAt":…}</c>. Never bodies, contact details or attribute values.
/// </summary>
public static class AutomationWebhookPayload
{
    public static string Create(AutomationActionRequest request, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        var conversation = request.Context.Conversation;
        return AutomationJsonWriter.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("event", "automation.webhook");
            if (request.Rule is { } rule)
                writer.WriteString("rule", rule.Value);
            writer.WriteString("conversation", conversation.Id.Value);
            writer.WriteString("inbox", conversation.Inbox.Value);
            writer.WriteString("status", conversation.Status.Kind.ToString().ToLowerInvariant());
            writer.WriteString("priority", conversation.Priority.ToString().ToLowerInvariant());
            if (conversation.Assignee is { } assignee)
                writer.WriteString("assignee", assignee.Value);
            if (conversation.Team is { } team)
                writer.WriteString("team", team.Value);
            writer.WriteStartArray("labels");
            foreach (var label in conversation.Labels)
                writer.WriteStringValue(label.Value);
            writer.WriteEndArray();
            writer.WriteString("trigger", request.Context.Event.Kind.ToString());
            writer.WriteString("sentAt", sentAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        });
    }
}

internal static class AutomationJsonWriter
{
    public static string Write(Action<System.Text.Json.Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            write(writer);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>
/// The default <see cref="IAutomationWebhookSender"/>: resolves the destination through the application's
/// <see cref="IAutomationWebhookDestinationResolver"/>, signs the body with the E6 API channel's version-one HMAC-SHA256 scheme
/// (<see cref="ApiWebhookSignature"/>, headers <see cref="ApiWebhookHeaders"/>, the delivery id stable across retries) and posts it
/// through an <see cref="ApiWebhookDestinationPolicy"/>-guarded client (https only, public addresses only, no redirects or proxies).
/// 2xx is delivered; anything else is a failure code (<c>destination-missing</c>, <c>endpoint-blocked</c>, <c>connection-failed</c>,
/// <c>timeout</c>, <c>transport-error</c>, <c>http-&lt;status&gt;</c>).
/// </summary>
public sealed class SignedAutomationWebhookSender : IAutomationWebhookSender, IDisposable
{
    private readonly IAutomationWebhookDestinationResolver? destinations;
    private readonly ApiWebhookDestinationPolicy policy;
    private readonly HttpClient client;
    private readonly TimeProvider time;

    public SignedAutomationWebhookSender(IOptions<AutomationOptions> options, TimeProvider time, IAutomationWebhookDestinationResolver? destinations = null,
        HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        value.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.destinations = destinations;
        policy = new ApiWebhookDestinationPolicy(value.AllowedWebhookHosts, value.AllowedWebhookPorts);
        var handler = innerHandler is null ? ApiWebhookHttp.CreateHandler(policy) : ApiWebhookHttp.Guard(policy, innerHandler);
        client = new HttpClient(handler) { Timeout = value.WebhookTimeout };
    }

    public async Task<AutomationWebhookResult> SendAsync(AutomationWebhookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (destinations is null)
            return new AutomationWebhookFailed("destination-missing");
        using var destination = await destinations.ResolveAsync(request.Scope, request.Destination, cancellationToken).ConfigureAwait(false);
        if (destination is null)
            return new AutomationWebhookFailed("destination-missing");
        if (!policy.Allows(destination.Endpoint))
            return new AutomationWebhookFailed("endpoint-blocked");
        var body = System.Text.Encoding.UTF8.GetBytes(request.Payload);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        using var message = new HttpRequestMessage(HttpMethod.Post, destination.Endpoint) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        message.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Id, request.DeliveryId);
        message.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Signature, ApiWebhookSignature.Create(destination.Key, request.DeliveryId, timestamp, body));
        message.Headers.TryAddWithoutValidation(ApiWebhookHeaders.KeyId, destination.KeyId);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return status is >= 200 and < 300
                ? new AutomationWebhookDelivered(status)
                : new AutomationWebhookFailed("http-" + status.ToString(CultureInfo.InvariantCulture));
        }
        catch (HttpRequestException error) when (Blocked(error))
        {
            return new AutomationWebhookFailed("endpoint-blocked");
        }
        catch (HttpRequestException error) when (error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError)
        {
            return new AutomationWebhookFailed("connection-failed");
        }
        catch (HttpRequestException)
        {
            return new AutomationWebhookFailed("transport-error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AutomationWebhookFailed("timeout");
        }
    }

    public void Dispose() => client.Dispose();

    private static bool Blocked(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
            if (error is ApiWebhookBlockedException)
                return true;
        return false;
    }
}

/// <summary>A resolver over a fixed map (tests and simple hosts). Keys are copied per resolution and disposed by the sender.</summary>
public sealed class StaticAutomationWebhookDestinationResolver(IReadOnlyDictionary<WebhookDestinationId, (Uri Endpoint, byte[] Key, string KeyId)> destinations)
    : IAutomationWebhookDestinationResolver
{
    public ValueTask<AutomationWebhookDestination?> ResolveAsync(Core.TicketScope scope, WebhookDestinationId destination, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(destinations.TryGetValue(destination, out var value) ? new AutomationWebhookDestination(value.Endpoint, value.Key, value.KeyId) : null);
}
