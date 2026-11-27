using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TicketFlow.Events;

namespace TicketFlow.Integrations.Segment;

/// <summary>
/// How the Segment <c>userId</c> is chosen. Every id is namespaced so kinds never collide: <c>contact:&lt;contact id&gt;</c>,
/// <c>external:&lt;issuer&gt;:&lt;external user id&gt;</c> and, for events without a contact, <c>anonymousId: "workspace:&lt;workspace id&gt;"</c>.
/// </summary>
public enum SegmentUserIdStrategy
{
    /// <summary><c>contact:&lt;id&gt;</c> (default). Works at every privacy level.</summary>
    ContactId = 1,

    /// <summary>
    /// <c>external:&lt;issuer&gt;:&lt;value&gt;</c> from the contact's first unmasked <c>external-user</c> identity in hydrated content,
    /// else <c>contact:&lt;id&gt;</c>. Masked identities are never used.
    /// </summary>
    ContactExternalId = 2,
}

/// <summary>
/// A Segment HTTP API source: the API host (default <c>https://api.segment.io/</c>), its write key and the scopes allowed to send to it.
/// </summary>
public sealed record SegmentSource
{
    public static readonly Uri DefaultHost = new("https://api.segment.io/");

    public SegmentSource(Uri host, string writeKey, IEnumerable<EventScopeRule> allowedScopes)
    {
        ArgumentNullException.ThrowIfNull(allowedScopes);
        AllowedScopes = [.. allowedScopes];
        if (AllowedScopes.Count == 0 || AllowedScopes.Any(rule => rule is null))
            throw new ArgumentException("A Segment source needs at least one allowed scope rule.", nameof(allowedScopes));
        ArgumentNullException.ThrowIfNull(host);
        if (!host.IsAbsoluteUri || !string.Equals(host.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || host.UserInfo.Length != 0 || host.Query.Length != 0 || host.Fragment.Length != 0)
            throw new ArgumentException("Segment hosts are absolute https URIs without credentials, query or fragment.", nameof(host));
        Host = host;
        WriteKey = string.IsNullOrWhiteSpace(writeKey) || writeKey.Length > 256 || writeKey.Any(character => character is < '!' or > '~')
            ? throw new ArgumentException("A printable write key is required.", nameof(writeKey))
            : writeKey;
    }

    public SegmentSource(string writeKey, IEnumerable<EventScopeRule> allowedScopes)
        : this(DefaultHost, writeKey, allowedScopes)
    {
    }

    public Uri Host { get; }
    public string WriteKey { get; }
    public IReadOnlyList<EventScopeRule> AllowedScopes { get; }

    public override string ToString() => $"SegmentSource {{ Host = {Host} }}";
}

public sealed class SegmentOptions
{
    /// <summary>Segment's documented 500 KB HTTP batch ceiling.</summary>
    public const int DefaultMaximumPayloadBytes = 500 * 1024;

    /// <summary>Sources by name; a subscription's sink target names one (for example <c>default</c>).</summary>
    public IDictionary<string, SegmentSource> Sources { get; } = new Dictionary<string, SegmentSource>(StringComparer.Ordinal);

    public SegmentUserIdStrategy UserId { get; set; } = SegmentUserIdStrategy.ContactId;

    /// <summary>Maximum encoded batch size in UTF-8 bytes before splitting (1 KiB to 1 MiB, default 500 KiB).</summary>
    public int MaximumPayloadBytes { get; set; } = DefaultMaximumPayloadBytes;

    /// <summary>Request timeout (1 second to 2 minutes, default 15 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The longest <c>Retry-After</c> honored (default 1 hour, max 1 day).</summary>
    public TimeSpan MaximumRetryAfter { get; set; } = TimeSpan.FromHours(1);

    internal void Validate()
    {
        if (!Enum.IsDefined(UserId) || MaximumPayloadBytes is < 1024 or > 1024 * 1024
            || Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(2)
            || MaximumRetryAfter <= TimeSpan.Zero || MaximumRetryAfter > TimeSpan.FromDays(1)
            || Sources.Any(pair => !EventText.IsToken(pair.Key, 64) || pair.Value is null))
            throw new InvalidOperationException("Segment options are invalid.");
    }
}

/// <summary>
/// The <c>segment</c> sink: posts each delivery to <c>{host}/v1/batch</c> with Basic auth (<c>writeKey:</c>) as
/// <c>{"batch":[{"type":"track",...}]}</c>. Each item carries <c>messageId</c> = the event id (Segment deduplicates on it),
/// <c>event</c> = CloudEvents type, <c>timestamp</c>, <c>userId</c> (or <c>anonymousId</c> when no contact applies), <c>properties</c>
/// and <c>context.groupId</c>. Batches over <see cref="SegmentOptions.MaximumPayloadBytes"/> or receiving HTTP 413 split automatically;
/// 2xx is delivered; 408/429/5xx retry; other statuses are permanent.
/// </summary>
public sealed class SegmentEventSink : IEventSink, IDisposable
{
    public const string SinkKind = "segment";

    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

    private readonly SegmentOptions options;
    private readonly HttpClient client;
    private readonly TimeProvider time;
    private readonly Dictionary<string, (SegmentSource Source, Uri BatchUri, AuthenticationHeaderValue Auth)> sources;

    public SegmentEventSink(IOptions<SegmentOptions> options, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? TimeProvider.System;
        sources = new Dictionary<string, (SegmentSource, Uri, AuthenticationHeaderValue)>(this.options.Sources.Count, StringComparer.Ordinal);
        foreach (var pair in this.options.Sources)
            sources[pair.Key] = (pair.Value, new Uri(pair.Value.Host, "v1/batch"),
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(pair.Value.WriteKey + ":"))));
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = this.options.Timeout };
    }

    public string Kind => SinkKind;

    public int MaximumBatchSize => EventSubscription.MaximumBatchSize;

    public bool IsValidTarget(Core.TicketScope scope, string target) =>
        scope is not null && target is not null && sources.TryGetValue(target, out var entry) && EventScopeRule.Allows(entry.Source.AllowedScopes, scope);

    public async Task<EventSinkResult> DeliverAsync(EventDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!sources.TryGetValue(delivery.Subscription.Subscription.Sink.Target, out var entry) || !EventScopeRule.Allows(entry.Source.AllowedScopes, delivery.Scope))
            return new EventSinkFailed("source-missing", permanent: true);
        var payload = TryBody(delivery, options.MaximumPayloadBytes);
        if (payload is null)
            return delivery.Events.Count > 1 ? EventSinkSplit.Instance : new EventSinkFailed("payload-too-large", permanent: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, entry.BatchUri);
        request.Headers.Authorization = entry.Auth;
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = JsonContentType;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return EventSinkDelivered.Instance;
            if (status == 413 && delivery.Events.Count > 1)
                return EventSinkSplit.Instance;
            var code = "http-" + status.ToString(CultureInfo.InvariantCulture);
            return status is 408 or 429 or >= 500 ? new EventSinkFailed(code, retryAfter: RetryAfter(response)) : new EventSinkFailed(code, permanent: true);
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

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
            return null;
        TimeSpan? delay = header.Delta ?? (header.Date is { } date ? date - time.GetUtcNow() : null);
        if (delay is not { } value)
            return null;
        if (value <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return value > options.MaximumRetryAfter ? options.MaximumRetryAfter : value;
    }

    /// <summary>The Segment <c>/v1/batch</c> request body for <paramref name="delivery"/>.</summary>
    public byte[] Body(EventDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return TryBody(delivery, int.MaxValue)!;
    }

    private byte[]? TryBody(EventDelivery delivery, int maxBytes)
    {
        var buffer = new ArrayBufferWriter<byte>(Math.Min(delivery.Events.Count * 512, 65_536));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("batch"u8);
            foreach (var rendered in delivery.Events)
            {
                WriteEvent(writer, rendered);
                if (maxBytes < int.MaxValue && buffer.WrittenCount + writer.BytesPending > maxBytes)
                    return null;
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > maxBytes)
            return null;
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteEvent(Utf8JsonWriter writer, RenderedEvent rendered)
    {
        using var document = JsonDocument.Parse(rendered.Json);
        var root = document.RootElement;
        var data = root.GetProperty("data");
        var workspace = root.GetProperty("workspaceid").GetString()!;
        var userId = UserId(rendered, data);
        writer.WriteStartObject();
        writer.WriteString("type", "track");
        writer.WriteString("event", root.GetProperty("type").GetString());
        writer.WriteString("messageId", root.GetProperty("id").GetString());
        writer.WriteString("timestamp", root.GetProperty("time").GetString());
        if (userId is not null)
            writer.WriteString("userId", userId);
        else
            writer.WriteString("anonymousId", "workspace:" + workspace);
        writer.WriteStartObject("properties");
        foreach (var property in data.EnumerateObject())
            property.WriteTo(writer);
        writer.WriteString("ticketflow_workspace", workspace);
        writer.WriteString("ticketflow_sequence", root.GetProperty("sequence").GetString());
        writer.WriteString("ticketflow_subject", root.GetProperty("subject").GetString());
        writer.WriteString("ticketflow_privacy", root.GetProperty("privacy").GetString());
        writer.WriteEndObject();
        writer.WriteStartObject("context");
        writer.WriteString("groupId", workspace);
        writer.WriteStartObject("library");
        writer.WriteString("name", "TicketFlow.Integrations.Segment");
        writer.WriteString("version", "1");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private string? UserId(RenderedEvent rendered, JsonElement data)
    {
        if (options.UserId == SegmentUserIdStrategy.ContactExternalId && data.TryGetProperty("content", out var content))
        {
            var holder = content.TryGetProperty("contact", out var related) ? related : content;
            if (holder.TryGetProperty("identities", out var identities))
                foreach (var identity in identities.EnumerateArray())
                    if (string.Equals(identity.GetProperty("kind").GetString(), "external-user", StringComparison.Ordinal))
                        return "external:" + (identity.TryGetProperty("namespace", out var issuer) ? issuer.GetString() : "") + ":" + identity.GetProperty("value").GetString();
        }
        var contact = rendered.Event.Event.Data.Value switch
        {
            ContactCreatedData created => created.Contact.Value,
            ContactUpdatedData updated => updated.Contact.Value,
            ContactDeletedData deleted => deleted.Contact.Value,
            ContactMergedData merged => merged.Contact.Value,
            ContactUnmergedData unmerged => unmerged.Contact.Value,
            ContactIdentityLinkedData linked => linked.Contact.Value,
            ConversationCreatedData created => created.Contact.Value,
            ConversationMessageReceivedData received => received.Contact.Value,
            SurveyRespondedData responded => responded.Contact.Value,
            _ => rendered.Event.Resource.Contact?.Value,
        };
        return contact is null ? null : "contact:" + contact;
    }

    public void Dispose() => client.Dispose();
}
