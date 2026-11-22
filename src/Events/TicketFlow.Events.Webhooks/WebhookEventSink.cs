using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TicketFlow.Channels.Api;
using TicketFlow.Core;

namespace TicketFlow.Events.Webhooks;

/// <summary>Additional headers of event webhooks (the signed headers are <see cref="ApiWebhookHeaders"/>).</summary>
public static class EventWebhookHeaders
{
    /// <summary>During key rotation: <c>v1=</c> HMAC of the same signed content with the previous key.</summary>
    public const string PreviousSignature = "TicketFlow-Webhook-Signature-Previous";

    /// <summary>During key rotation: the previous key's identifier.</summary>
    public const string PreviousKeyId = "TicketFlow-Webhook-Previous-Key-Id";

    /// <summary>The delivery attempt (1 for the first).</summary>
    public const string Attempt = "TicketFlow-Webhook-Attempt";
}

/// <summary>One signing key: a 32-64 byte secret and its identifier (sent, never secret). <see cref="Dispose"/> zeroes the copy.</summary>
public sealed class EventWebhookKey : IDisposable
{
    public EventWebhookKey(string keyId, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is < ApiWebhookSignature.MinimumKeyBytes or > ApiWebhookSignature.MaximumKeyBytes)
            throw new ArgumentException("Webhook keys are 32-64 bytes.", nameof(key));
        KeyId = EventText.RequireToken(keyId, 64, nameof(keyId));
        Key = (byte[])key.Clone();
    }

    public string KeyId { get; }
    public byte[] Key { get; }

    public void Dispose() => CryptographicOperations.ZeroMemory(Key);

    public override string ToString() => $"EventWebhookKey {{ KeyId = {KeyId} }}";
}

/// <summary>The keys for one subscription: <see cref="Primary"/> signs every request; <see cref="Previous"/> also signs while a rotation overlaps.</summary>
public sealed class EventWebhookKeys(EventWebhookKey primary, EventWebhookKey? previous = null) : IDisposable
{
    public EventWebhookKey Primary { get; } = primary ?? throw new ArgumentNullException(nameof(primary));
    public EventWebhookKey? Previous { get; } = previous;

    public void Dispose()
    {
        Primary.Dispose();
        Previous?.Dispose();
    }
}

/// <summary>
/// Resolves a subscription's signing keys from the application's secret store, so TicketFlow stores never hold webhook secrets. Return
/// fresh copies; the sink disposes them after each request.
/// </summary>
public interface IEventWebhookKeyResolver
{
    ValueTask<EventWebhookKeys?> ResolveAsync(TicketScope scope, EventSubscriptionId subscription, CancellationToken cancellationToken = default);
}

/// <summary>
/// A process-local key ring (tests and simple hosts). <see cref="Rotate"/> makes a new key primary and keeps the old one as previous until
/// <see cref="CompleteRotation"/>, so receivers can switch keys without dropping deliveries. Thread-safe; not durable.
/// </summary>
public sealed class InMemoryEventWebhookKeyRing : IEventWebhookKeyResolver
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope, EventSubscriptionId), (string Id, byte[] Key, string? PreviousId, byte[]? Previous)> keys = [];

    public void Set(TicketScope scope, EventSubscriptionId subscription, string keyId, byte[] key)
    {
        using var validated = new EventWebhookKey(keyId, key);
        lock (gate)
            keys[(scope, subscription)] = (validated.KeyId, (byte[])key.Clone(), null, null);
    }

    public void Rotate(TicketScope scope, EventSubscriptionId subscription, string keyId, byte[] key)
    {
        using var validated = new EventWebhookKey(keyId, key);
        lock (gate)
        {
            if (!keys.TryGetValue((scope, subscription), out var current))
                throw new InvalidOperationException("No key to rotate.");
            keys[(scope, subscription)] = (validated.KeyId, (byte[])key.Clone(), current.Id, current.Key);
        }
    }

    public void CompleteRotation(TicketScope scope, EventSubscriptionId subscription)
    {
        lock (gate)
            if (keys.TryGetValue((scope, subscription), out var current))
                keys[(scope, subscription)] = (current.Id, current.Key, null, null);
    }

    public ValueTask<EventWebhookKeys?> ResolveAsync(TicketScope scope, EventSubscriptionId subscription, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return ValueTask.FromResult(keys.TryGetValue((scope, subscription), out var value)
                ? new EventWebhookKeys(new EventWebhookKey(value.Id, value.Key), value.Previous is null ? null : new EventWebhookKey(value.PreviousId!, value.Previous))
                : null);
    }
}

public sealed class EventWebhookOptions
{
    /// <summary>Exact allowed destination hosts (empty allows any public host).</summary>
    public IList<string> AllowedHosts { get; } = [];

    /// <summary>Allowed destination ports (default 443).</summary>
    public IList<int> AllowedPorts { get; } = [443];

    /// <summary>Request timeout (1 second to 2 minutes, default 15 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The longest <c>Retry-After</c> honored (default 1 hour).</summary>
    public TimeSpan MaximumRetryAfter { get; set; } = TimeSpan.FromHours(1);

    internal void Validate()
    {
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(2) || MaximumRetryAfter <= TimeSpan.Zero)
            throw new InvalidOperationException("Event webhook options are invalid.");
    }
}

/// <summary>
/// The <c>webhook</c> sink: the subscription target is an https URL validated by <see cref="ApiWebhookDestinationPolicy"/> (https only,
/// allowed ports, optional exact host allow-list, public addresses only at connect time, no redirects, proxies or cookies). Each delivery
/// is one POST: a single CloudEvent (<c>application/cloudevents+json</c>) or a JSON array of them (<c>application/cloudevents-batch+json</c>),
/// signed with the E6 v1 scheme (<see cref="ApiWebhookSignature"/>; headers <see cref="ApiWebhookHeaders"/>, the delivery id stable across
/// retries) using the primary key from <see cref="IEventWebhookKeyResolver"/>, plus <see cref="EventWebhookHeaders.PreviousSignature"/> while a
/// rotation overlaps. 2xx is delivered; 408, 425, 429 and 5xx retry (honoring <c>Retry-After</c>); other statuses, blocked destinations and
/// a single oversized event are permanent failures; an oversized batch (over the 1 MiB signing bound) or a 413 response asks the engine to
/// split it (<see cref="EventSinkSplit"/>). Payloads and response bodies are never logged or read.
/// </summary>
public sealed class WebhookEventSink : IEventSink, IDisposable
{
    public const string SinkKind = "webhook";

    private readonly IEventWebhookKeyResolver? keys;
    private readonly ApiWebhookDestinationPolicy policy;
    private readonly HttpClient client;
    private readonly EventWebhookOptions options;
    private readonly TimeProvider time;

    public WebhookEventSink(IOptions<EventWebhookOptions> options, TimeProvider time, IEventWebhookKeyResolver? keys = null, HttpMessageHandler? innerHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.keys = keys;
        policy = new ApiWebhookDestinationPolicy(this.options.AllowedHosts, this.options.AllowedPorts);
        var handler = innerHandler is null ? ApiWebhookHttp.CreateHandler(policy) : ApiWebhookHttp.Guard(policy, innerHandler);
        client = new HttpClient(handler) { Timeout = this.options.Timeout };
    }

    public string Kind => SinkKind;

    public int MaximumBatchSize => EventSubscription.MaximumBatchSize;

    public bool IsValidTarget(TicketScope scope, string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) && policy.Allows(uri);

    public async Task<EventSinkResult> DeliverAsync(EventDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!Uri.TryCreate(delivery.Subscription.Subscription.Sink.Target, UriKind.Absolute, out var endpoint) || !policy.Allows(endpoint))
            return new EventSinkFailed("endpoint-blocked", permanent: true);
        if (keys is null)
            return new EventSinkFailed("key-missing");
        using var resolved = await keys.ResolveAsync(delivery.Scope, delivery.Subscription.Subscription.Id, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
            return new EventSinkFailed("key-missing");
        var single = delivery.Events.Count == 1;
        var body = Encoding.UTF8.GetBytes(single ? delivery.Events[0].Json : "[" + string.Join(",", delivery.Events.Select(item => item.Json)) + "]");
        if (body.Length > ApiWebhookSignature.MaximumBodyBytes)
            return single ? new EventSinkFailed("payload-too-large", permanent: true) : EventSinkSplit.Instance;
        var deliveryId = delivery.DeliveryId;
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds();
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(single ? "application/cloudevents+json" : "application/cloudevents-batch+json")
            { CharSet = "utf-8" };
        message.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Id, deliveryId);
        message.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Timestamp, timestamp.ToString(CultureInfo.InvariantCulture));
        message.Headers.TryAddWithoutValidation(ApiWebhookHeaders.Signature, ApiWebhookSignature.Create(resolved.Primary.Key, deliveryId, timestamp, body));
        message.Headers.TryAddWithoutValidation(ApiWebhookHeaders.KeyId, resolved.Primary.KeyId);
        if (resolved.Previous is { } previous)
        {
            message.Headers.TryAddWithoutValidation(EventWebhookHeaders.PreviousSignature, ApiWebhookSignature.Create(previous.Key, deliveryId, timestamp, body));
            message.Headers.TryAddWithoutValidation(EventWebhookHeaders.PreviousKeyId, previous.KeyId);
        }
        message.Headers.TryAddWithoutValidation(EventWebhookHeaders.Attempt, delivery.Attempt.ToString(CultureInfo.InvariantCulture));
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return EventSinkDelivered.Instance;
            if (status == 413 && !single)
                return EventSinkSplit.Instance;
            var code = "http-" + status.ToString(CultureInfo.InvariantCulture);
            return status is 408 or 425 or 429 or >= 500
                ? new EventSinkFailed(code, retryAfter: RetryAfter(response))
                : new EventSinkFailed(code, permanent: true);
        }
        catch (HttpRequestException error) when (Blocked(error))
        {
            return new EventSinkFailed("endpoint-blocked", permanent: true);
        }
        catch (HttpRequestException error) when (error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError)
        {
            return new EventSinkFailed("connection-failed");
        }
        catch (HttpRequestException)
        {
            return new EventSinkFailed("transport-error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EventSinkFailed("timeout");
        }
    }

    public void Dispose() => client.Dispose();

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        TimeSpan? delay = header?.Delta ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
        return delay is { } value && value > TimeSpan.Zero ? (value > options.MaximumRetryAfter ? options.MaximumRetryAfter : value) : null;
    }

    private static bool Blocked(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
            if (error is ApiWebhookBlockedException)
                return true;
        return false;
    }
}
