using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TicketFlow.Events;
using Microsoft.Extensions.Options;

namespace TicketFlow.Integrations.PostHog;

/// <summary>
/// How the PostHog <c>distinct_id</c> is chosen. Every id is namespaced so the kinds can never collide: <c>contact:&lt;contact id&gt;</c>,
/// <c>external:&lt;issuer&gt;:&lt;external user id&gt;</c> and, for events that concern no contact, <c>workspace:&lt;workspace id&gt;</c>. Conversation
/// events (messages, SLA, routing, delivery status) use the conversation's contact, so all of a contact's events share one id.
/// </summary>
public enum PostHogDistinctIdStrategy
{
    /// <summary><c>contact:&lt;id&gt;</c> (default). Works at every privacy level.</summary>
    ContactId = 1,

    /// <summary>
    /// <c>external:&lt;issuer&gt;:&lt;value&gt;</c> from the contact's first unmasked <c>external-user</c> identity in hydrated content (the event's own
    /// contact content, or <c>content.contact</c> of conversation events), else <c>contact:&lt;id&gt;</c>. Content is present only for
    /// <c>Content</c> subscriptions, so use one privacy level per project to keep ids consistent; masked identities are never used.
    /// </summary>
    ContactExternalId = 2,
}

/// <summary>
/// A PostHog project: the API host, its public project API key and the scopes allowed to send to it (at least one
/// <see cref="EventScopeRule"/>; subscriptions of other tenants or workspaces cannot target the project).
/// </summary>
public sealed record PostHogProject
{
    public PostHogProject(Uri host, string projectApiKey, IEnumerable<EventScopeRule> allowedScopes)
    {
        ArgumentNullException.ThrowIfNull(allowedScopes);
        AllowedScopes = [.. allowedScopes];
        if (AllowedScopes.Count == 0 || AllowedScopes.Any(rule => rule is null))
            throw new ArgumentException("A PostHog project needs at least one allowed scope rule.", nameof(allowedScopes));
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
    public IReadOnlyList<EventScopeRule> AllowedScopes { get; }

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
/// subscription's privacy level rendered. Distinct ids are namespaced (see <see cref="PostHogDistinctIdStrategy"/>); events without a contact
/// use <c>workspace:&lt;id&gt;</c> with <c>$process_person_profile: false</c>. Only scopes in the project's allow-list may target it. 2xx is
/// delivered; 413 on a batch splits it; 408/429/5xx retry; other statuses are permanent.
/// </summary>
public sealed class PostHogEventSink : Events.IEventSink, IDisposable
{
    public const string SinkKind = "posthog";

    private static readonly System.Net.Http.Headers.MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

    private readonly PostHogOptions options;
    private readonly HttpClient client;
    private readonly Dictionary<string, (PostHogProject Project, Uri BatchUri)> projects;

    public PostHogEventSink(IOptions<PostHogOptions> options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        projects = new Dictionary<string, (PostHogProject, Uri)>(this.options.Projects.Count, StringComparer.Ordinal);
        foreach (var pair in this.options.Projects)
            projects[pair.Key] = (pair.Value, new Uri(pair.Value.Host, "batch/"));
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = this.options.Timeout };
    }

    public string Kind => SinkKind;

    public int MaximumBatchSize => Events.EventSubscription.MaximumBatchSize;

    public bool IsValidTarget(Core.TicketScope scope, string target) =>
        scope is not null && target is not null && projects.TryGetValue(target, out var entry) && EventScopeRule.Allows(entry.Project.AllowedScopes, scope);

    public async Task<Events.EventSinkResult> DeliverAsync(Events.EventDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (!projects.TryGetValue(delivery.Subscription.Subscription.Sink.Target, out var entry) || !EventScopeRule.Allows(entry.Project.AllowedScopes, delivery.Scope))
            return new Events.EventSinkFailed("project-missing", permanent: true);
        using var content = new ByteArrayContent(Body(entry.Project, delivery));
        content.Headers.ContentType = JsonContentType;
        try
        {
            using var response = await client.PostAsync(entry.BatchUri, content, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return Events.EventSinkDelivered.Instance;
            if (status == 413 && delivery.Events.Count > 1)
                return Events.EventSinkSplit.Instance;
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
        if (options.DistinctId == PostHogDistinctIdStrategy.ContactExternalId && data.TryGetProperty("content", out var content))
        {
            var holder = content.TryGetProperty("contact", out var related) ? related : content;
            if (holder.TryGetProperty("identities", out var identities))
                foreach (var identity in identities.EnumerateArray())
                    if (identity.GetProperty("kind").GetString() == "external-user")
                        return "external:" + (identity.TryGetProperty("namespace", out var issuer) ? issuer.GetString() : "") + ":" + identity.GetProperty("value").GetString();
        }
        // The event's subject contact (contact events), else the conversation's contact from the resource snapshot.
        var contact = rendered.Event.Event.Data.Value switch
        {
            Events.ContactCreatedData created => created.Contact.Value,
            Events.ContactUpdatedData updated => updated.Contact.Value,
            Events.ContactDeletedData deleted => deleted.Contact.Value,
            Events.ContactMergedData merged => merged.Contact.Value,
            Events.ContactUnmergedData unmerged => unmerged.Contact.Value,
            Events.ContactIdentityLinkedData linked => linked.Contact.Value,
            Events.ConversationCreatedData created => created.Contact.Value,
            Events.ConversationMessageReceivedData received => received.Contact.Value,
            Events.SurveyRespondedData responded => responded.Contact.Value,
            _ => rendered.Event.Resource.Contact?.Value,
        };
        return contact is null ? null : "contact:" + contact;
    }

    public void Dispose() => client.Dispose();
}
