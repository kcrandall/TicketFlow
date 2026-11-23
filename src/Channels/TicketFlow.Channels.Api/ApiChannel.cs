using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Api;

/// <summary>
/// Where and how to deliver one API channel configuration: the HTTPS endpoint and the signing key (32-64 bytes) with its identifier.
/// Supplied by the application per (scope, configuration id); TicketFlow never stores keys. Dispose to clear the key copy.
/// </summary>
public sealed class ApiChannelDestination : IDisposable
{
    private readonly byte[] key;

    public ApiChannelDestination(Uri endpoint, ReadOnlySpan<byte> signingKey, string keyId)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (signingKey.Length is < ApiWebhookSignature.MinimumKeyBytes or > ApiWebhookSignature.MaximumKeyBytes)
            throw new ArgumentException($"Signing keys are {ApiWebhookSignature.MinimumKeyBytes}-{ApiWebhookSignature.MaximumKeyBytes} bytes.", nameof(signingKey));
        key = signingKey.ToArray();
        KeyId = ChannelText.RequireText(keyId, 128, nameof(keyId));
    }

    public Uri Endpoint { get; }
    public string KeyId { get; }
    internal ReadOnlySpan<byte> Key => key;

    public void Dispose() => CryptographicOperations.ZeroMemory(key);

    public override string ToString() => "ApiChannelDestination { <redacted> }";
}

/// <summary>Resolves the webhook destination of an API channel configuration (application-owned; may read a secret store).</summary>
public interface IApiChannelDestinationResolver
{
    /// <summary>Returns the destination, or <see langword="null"/> when the configuration has none (the message fails).</summary>
    ValueTask<ApiChannelDestination?> ResolveAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default);
}

public sealed class ApiChannelOptions
{
    /// <summary>The channel kind served (default <c>api</c>).</summary>
    public string Kind { get; set; } = "api";

    /// <summary>Exact allowed webhook hosts; empty allows any public host.</summary>
    public IList<string> AllowedHosts { get; } = [];

    /// <summary>Allowed destination ports (default 443).</summary>
    public IList<int> AllowedPorts { get; } = [443];

    /// <summary>Request timeout (1-120 seconds, default 30). A timeout after the request was written is an unknown outcome.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Identity kinds the channel delivers to, in preference order.</summary>
    public IList<ContactIdentityKind> RecipientKinds { get; } =
        [ContactIdentityKind.ExternalUser, ContactIdentityKind.WidgetVisitor, ContactIdentityKind.Email, ContactIdentityKind.Phone, ContactIdentityKind.WhatsApp, ContactIdentityKind.SocialHandle];

    internal void Validate()
    {
        _ = new ChannelKind(Kind);
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(2) || RecipientKinds.Count == 0 || RecipientKinds.Any(kind => !Enum.IsDefined(kind)))
            throw new InvalidOperationException("API channel options are invalid.");
    }
}

/// <summary>
/// The generic API channel: delivers outgoing messages as a signed JSON webhook (<see cref="ApiWebhookHeaders"/>,
/// <see cref="ApiWebhookSignature"/>) to the application-resolved destination through an SSRF-guarded client. 2xx responses are
/// accepted (an optional JSON <c>externalMessageId</c> is recorded); 408, 429 and 5xx are retryable rejections (honouring
/// <c>Retry-After</c>); other statuses are permanent rejections; blocked destinations are permanent rejections; connection failures
/// before sending are retryable; timeouts and other transport failures after the request may have been written are unknown
/// outcomes. The delivery id is the submission's idempotency key, stable across retries, so receivers can deduplicate.
/// </summary>
public sealed class ApiChannel : ITicketChannel, IDisposable
{
    private const int MaximumResponseBytes = 16 * 1024;
    private static readonly MediaTypeHeaderValue JsonUtf8 = new("application/json") { CharSet = "utf-8" };

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    private readonly IApiChannelDestinationResolver destinations;
    private readonly ApiWebhookDestinationPolicy policy;
    private readonly HttpClient client;
    private readonly TimeProvider time;
    private readonly TimeSpan timeout;

    public ApiChannel(IApiChannelDestinationResolver destinations, IOptions<ApiChannelOptions> options, TimeProvider time, HttpMessageHandler? innerHandler = null)
    {
        this.destinations = destinations ?? throw new ArgumentNullException(nameof(destinations));
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        value.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        policy = new ApiWebhookDestinationPolicy(value.AllowedHosts, value.AllowedPorts);
        var handler = innerHandler is null ? ApiWebhookHttp.CreateHandler(policy) : ApiWebhookHttp.Guard(policy, innerHandler);
        client = new HttpClient(handler) { Timeout = value.Timeout };
        timeout = value.Timeout;
        Kind = new ChannelKind(value.Kind);
        RecipientKinds = [.. value.RecipientKinds];
    }

    public ChannelKind Kind { get; }

    public ChannelCapabilities Capabilities { get; } = new(ChannelFeatures.Attachments | ChannelFeatures.RichContent | ChannelFeatures.Threading | ChannelFeatures.DeliveryReceipts
        | ChannelFeatures.ReadReceipts, maximumAttachments: ConversationMessageLimits.MaximumAttachments);

    public IReadOnlyList<ContactIdentityKind> RecipientKinds { get; }

    public async Task<ChannelSendResult> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var destination = await destinations.ResolveAsync(message.Scope, message.Binding.ConfigurationId, cancellationToken).ConfigureAwait(false);
        if (destination is null)
            return new ChannelSendRejected("destination-missing", retryable: false);
        if (!policy.Allows(destination.Endpoint))
            return new ChannelSendRejected("endpoint-blocked", retryable: false);

        var body = Serialize(message);
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, destination.Endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = JsonUtf8;
        request.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Id, message.IdempotencyKey);
        request.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Signature, ApiWebhookSignature.Create(destination.Key, message.IdempotencyKey, timestamp, body));
        request.Headers.TryAddWithoutValidation(ApiWebhookHeaders.KeyId, destination.KeyId);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (Blocked(error))
        {
            return new ChannelSendRejected("endpoint-blocked", retryable: false);
        }
        catch (HttpRequestException error) when (error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError)
        {
            return new ChannelSendRejected("connection-failed", retryable: true);
        }
        catch (HttpRequestException)
        {
            return new ChannelSendUnknown("transport-error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ChannelSendUnknown("timeout");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
            {
                string? externalId = null;
                if (response.Content.Headers.ContentType?.MediaType is "application/json")
                {
                    // HttpClient.Timeout does not cover the body after ResponseHeadersRead; bound it separately. The submission is already
                    // accepted, so a slow or stalled body only loses the optional external id.
                    using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    bodyTimeout.CancelAfter(timeout);
                    try
                    {
                        externalId = await ExternalIdAsync(response, bodyTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        externalId = null;
                    }
                }
                return new ChannelSendAccepted(externalId);
            }
            var code = "http-" + status.ToString(CultureInfo.InvariantCulture);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500)
                return new ChannelSendRejected(code, retryable: true, RetryAfter(response));
            return new ChannelSendRejected(code, retryable: false);
        }
    }

    public void Dispose() => client.Dispose();

    private static bool Blocked(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ApiWebhookBlockedException)
                return true;
        return false;
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
        return delay is { } value ? TimeSpan.FromTicks(Math.Clamp(value.Ticks, 0, TimeSpan.FromDays(1).Ticks)) : null;
    }

    private static async Task<string?> ExternalIdAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumResponseBytes + 1);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var read = 0;
            var limit = MaximumResponseBytes + 1;
            int chunk;
            while (read < limit && (chunk = await stream.ReadAsync(buffer.AsMemory(read, limit - read), cancellationToken).ConfigureAwait(false)) > 0)
                read += chunk;
            if (read > MaximumResponseBytes)
                return null;
            using var document = JsonDocument.Parse(buffer.AsMemory(0, read), new JsonDocumentOptions { MaxDepth = 8 });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("externalMessageId"u8, out var id)
                && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= ChannelText.MaximumExternalIdLength } value && ChannelText.IsClean(value)
                ? value : null;
        }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The versioned webhook payload (<c>"v":1</c>, <c>"type":"message.outgoing"</c>) with explicit property names.</summary>
    public static byte[] Serialize(OutboundChannelMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var buffer = sharedBuffer;
        if (buffer is null)
            sharedBuffer = buffer = new ArrayBufferWriter<byte>(1024);
        else
            buffer.ResetWrittenCount();
        var writer = sharedWriter;
        if (writer is null)
            sharedWriter = writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);

        writer.WriteStartObject();
        writer.WriteNumber("v"u8, 1);
        writer.WriteString("type"u8, "message.outgoing"u8);
        writer.WriteString("id"u8, message.IdempotencyKey);
        writer.WriteString("channel"u8, message.Binding.Kind.Value);
        writer.WriteString("configurationId"u8, message.Binding.ConfigurationId);
        writer.WriteString("inbox"u8, message.Inbox.Value);
        writer.WriteString("conversation"u8, message.Conversation.Value);
        if (message.Message is { } id)
            writer.WriteString("message"u8, id.Value);
        else
            writer.WriteNull("message"u8);
        writer.WriteString("contact"u8, message.Contact.Value);
        writer.WriteStartObject("recipient"u8);
        writer.WriteString("kind"u8, IdentityKind(message.Recipient.Kind));
        writer.WriteString("value"u8, message.Recipient.Value);
        if (message.Recipient.Namespace is { } ns)
            writer.WriteString("namespace"u8, ns);
        writer.WriteEndObject();
        writer.WriteString("body"u8, message.Body);
        writer.WriteString("contentType"u8, message.ContentType switch
        {
            MessageContentType.PlainText => "plain_text"u8,
            MessageContentType.Markdown => "markdown"u8,
            MessageContentType.Html => "html"u8,
            _ => throw new InvalidOperationException("Unknown content type."),
        });
        writer.WriteStartArray("attachments"u8);
        for (var i = 0; i < message.Attachments.Count; i++)
        {
            var attachment = message.Attachments[i];
            writer.WriteStartObject();
            writer.WriteString("storageRef"u8, attachment.StorageRef);
            writer.WriteString("fileName"u8, attachment.FileName);
            writer.WriteString("mediaType"u8, attachment.MediaType);
            writer.WriteNumber("sizeBytes"u8, attachment.SizeBytes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("thread"u8);
        WriteOptional(writer, "externalThreadId"u8, message.Thread.ExternalThreadId);
        WriteOptional(writer, "inReplyTo"u8, message.Thread.InReplyTo);
        writer.WriteStartArray("references"u8);
        for (var i = 0; i < message.Thread.References.Count; i++)
            writer.WriteStringValue(message.Thread.References[i]);
        writer.WriteEndArray();
        WriteOptional(writer, "subject"u8, message.Thread.Subject);
        writer.WriteEndObject();
        if (message.Template is { } template)
        {
            writer.WriteStartObject("template"u8);
            writer.WriteString("name"u8, template.Name);
            writer.WriteString("locale"u8, template.Locale);
            writer.WriteStartArray("parameters"u8);
            for (var i = 0; i < template.Parameters.Count; i++)
                writer.WriteStringValue(template.Parameters[i]);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        writer.Flush();
        var result = buffer.WrittenSpan.ToArray();
        if (buffer.Capacity > 131072)
            sharedBuffer = null;
        return result;
    }

    private static void WriteOptional(Utf8JsonWriter writer, ReadOnlySpan<byte> name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static ReadOnlySpan<byte> IdentityKind(ContactIdentityKind kind) => kind switch
    {
        ContactIdentityKind.Email => "email"u8,
        ContactIdentityKind.Phone => "phone"u8,
        ContactIdentityKind.WhatsApp => "whatsapp"u8,
        ContactIdentityKind.WidgetVisitor => "widget_visitor"u8,
        ContactIdentityKind.ExternalUser => "external_user"u8,
        ContactIdentityKind.SocialHandle => "social_handle"u8,
        _ => throw new InvalidOperationException("Unknown identity kind."),
    };
}
