using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TicketFlow.Integrations.PostHog;

/// <summary>How the PostHog <c>distinct_id</c> is chosen for events that concern a contact. Persisted in configuration only.</summary>
public enum PostHogDistinctIdStrategy
{
    /// <summary>The TicketFlow contact identifier (default). Works at every privacy level.</summary>
    ContactId = 1,

    /// <summary>
    /// The contact's first unmasked <c>external-user</c> identity (for example the application's own user id) from hydrated content,
    /// falling back to the contact identifier. Content is present only for <c>Content</c> subscriptions, and masked identities are never
    /// used.
    /// </summary>
    ContactExternalId = 2,
}

/// <summary>A PostHog project: the API host and its public project API key.</summary>
public sealed record PostHogProject
{
    public PostHogProject(Uri host, string projectApiKey)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!host.IsAbsoluteUri || host.Scheme != Uri.UriSchemeHttps || host.UserInfo.Length != 0 || host.Query.Length != 0 || host.Fragment.Length != 0)
            throw new ArgumentException("PostHog hosts are absolute https URIs without credentials, query or fragment.", nameof(host));
        Host = host;
        ProjectApiKey = string.IsNullOrWhiteSpace(projectApiKey) || projectApiKey.Length > 256 || projectApiKey.Any(character => character is < '!' or > '~')
            ? throw new ArgumentException("A printable project API key is required.", nameof(projectApiKey))
            : projectApiKey;
    }

    public Uri Host { get; }
    public string ProjectApiKey { get; }

    public override string ToString() => $"PostHogProject {{ Host = {Host} }}";
}

public sealed class PostHogOptions
{
    /// <summary>Projects by name; a subscription's sink target names one (for example <c>default</c>).</summary>
    public IDictionary<string, PostHogProject> Projects { get; } = new Dictionary<string, PostHogProject>(StringComparer.Ordinal);

    public PostHogDistinctIdStrategy DistinctId { get; set; } = PostHogDistinctIdStrategy.ContactId;

    /// <summary>The PostHog group type for workspaces (group analytics; default <c>workspace</c>). Group keys are workspace identifiers.</summary>
    public string GroupType { get; set; } = "workspace";

    /// <summary>Request timeout (1 second to 2 minutes, default 15 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        if (!Enum.IsDefined(DistinctId) || string.IsNullOrWhiteSpace(GroupType) || GroupType.Length > 64 || Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(2)
            || Projects.Any(pair => !Events.EventText.IsToken(pair.Key, 64) || pair.Value is null))
            throw new InvalidOperationException("PostHog options are invalid.");
    }
}

/// <summary>
/// The <c>posthog</c> sink: posts each delivery to <c>{host}/batch/</c> as <c>{"api_key","historical_migration":false,"batch":[…]}</c>. Each
/// event is <c>{"event": CloudEvents type, "distinct_id", "timestamp", "uuid": the event id (PostHog deduplicates on it),
/// "properties": data plus "$groups", "ticketflow_sequence", "ticketflow_subject", "ticketflow_privacy"}</c>. Properties carry only what the
/// subscription's privacy level rendered. Events without a contact use <c>workspace:&lt;id&gt;</c> with <c>$process_person_profile: false</c>.
/// 2xx is delivered; 408/429/5xx retry; other statuses are permanent.
/// </summary>
public sealed class PostHogEventSink : Events.IEventSink, IDisposable
{
    public const string SinkKind = "posthog";

    private readonly PostHogOptions options;
    private readonly HttpClient client;

    public PostHogEventSink(IOptions<PostHogOptions> options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = this.options.Timeout };
    }

    public string Kind => SinkKind;

    public int MaximumBatchSize => Events.EventSubscription.MaximumBatchSize;

    public bool IsValidTarget(string target) => target is not null && options.Projects.ContainsKey(target);

    public async Task<Events.EventSinkResult> DeliverAsync(Events.EventDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!options.Projects.TryGetValue(delivery.Subscription.Subscription.Sink.Target, out var project))
            return new Events.EventSinkFailed("project-missing", permanent: true);
        using var content = new ByteArrayContent(Body(project, delivery));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        try
        {
            using var response = await client.PostAsync(new Uri(project.Host, "batch/"), content, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return Events.EventSinkDelivered.Instance;
            var code = "http-" + status.ToString(CultureInfo.InvariantCulture);
            return status is 408 or 429 or >= 500 ? new Events.EventSinkFailed(code, retryAfter: response.Headers.RetryAfter?.Delta) : new Events.EventSinkFailed(code, permanent: true);
        }
        catch (HttpRequestException)
        {
            return new Events.EventSinkFailed("transport-error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Events.EventSinkFailed("timeout");
        }
    }

    /// <summary>The batch request body for <paramref name="delivery"/>.</summary>
    public byte[] Body(PostHogProject project, Events.EventDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(delivery);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("api_key", project.ProjectApiKey);
            writer.WriteBoolean("historical_migration", false);
            writer.WriteStartArray("batch");
            foreach (var rendered in delivery.Events)
                WriteEvent(writer, rendered);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteEvent(Utf8JsonWriter writer, Events.RenderedEvent rendered)
    {
        using var document = JsonDocument.Parse(rendered.Json);
        var root = document.RootElement;
        var data = root.GetProperty("data");
        var workspace = root.GetProperty("workspaceid").GetString()!;
        var distinctId = DistinctId(rendered, data);
        writer.WriteStartObject();
        writer.WriteString("event", root.GetProperty("type").GetString());
        writer.WriteString("distinct_id", distinctId ?? "workspace:" + workspace);
        writer.WriteString("timestamp", root.GetProperty("time").GetString());
        writer.WriteString("uuid", root.GetProperty("id").GetString());
        writer.WriteStartObject("properties");
        foreach (var property in data.EnumerateObject())
            property.WriteTo(writer);
        writer.WriteStartObject("$groups");
        writer.WriteString(options.GroupType, workspace);
        writer.WriteEndObject();
        if (distinctId is null)
            writer.WriteBoolean("$process_person_profile", false);
        writer.WriteString("ticketflow_sequence", root.GetProperty("sequence").GetString());
        writer.WriteString("ticketflow_subject", root.GetProperty("subject").GetString());
        writer.WriteString("ticketflow_privacy", root.GetProperty("privacy").GetString());
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private string? DistinctId(Events.RenderedEvent rendered, JsonElement data)
    {
        if (options.DistinctId == PostHogDistinctIdStrategy.ContactExternalId && data.TryGetProperty("content", out var content)
            && content.TryGetProperty("identities", out var identities))
            foreach (var identity in identities.EnumerateArray())
                if (identity.GetProperty("kind").GetString() == "external-user")
                    return identity.GetProperty("value").GetString();
        if (data.TryGetProperty("contact", out var contact) && contact.ValueKind == JsonValueKind.String)
            return contact.GetString();
        return rendered.Event.Resource.Contact?.Value;
    }

    public void Dispose() => client.Dispose();
}
