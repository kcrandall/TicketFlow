using System.Text.Json;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>Rendering settings for CloudEvents output.</summary>
public sealed record CloudEventRenderOptions
{
    public const int MaximumSourceLength = 512;

    public CloudEventRenderOptions(string source = "/ticketflow", string schemaPrefix = "urn:ticketflow:schema:")
    {
        Source = Uri.TryCreate(source, UriKind.RelativeOrAbsolute, out _) ? EventText.RequireText(source, MaximumSourceLength, nameof(source))
            : throw new ArgumentException("The source must be a URI reference.", nameof(source));
        SchemaPrefix = EventText.RequireText(schemaPrefix, MaximumSourceLength, nameof(schemaPrefix));
    }

    public static CloudEventRenderOptions Default { get; } = new();

    /// <summary>The CloudEvents <c>source</c> URI reference (default <c>/ticketflow</c>).</summary>
    public string Source { get; }

    /// <summary>Prefix of <c>dataschema</c>; the event type is appended (default <c>urn:ticketflow:schema:</c>).</summary>
    public string SchemaPrefix { get; }
}

/// <summary>
/// Encodes integration event payloads. <see cref="EncodeData"/>/<see cref="DecodeData"/> are the persisted shape: a compact
/// <c>{"v":1,…}</c> object with every field, discriminated by the stored event type; decoding re-validates every value and rejects unknown
/// versions, types and properties. <see cref="Render"/> produces a CloudEvents 1.0 structured-mode JSON object at a privacy level:
/// <c>specversion</c>, <c>id</c>, <c>source</c>, <c>type</c>, <c>time</c>, <c>subject</c>, <c>datacontenttype</c>, <c>dataschema</c>, the
/// extension attributes <c>sequence</c> (decimal string), <c>tenantid</c>, <c>projectid</c>, <c>environmentid</c>, <c>appid</c>,
/// <c>workspaceid</c>, <c>privacy</c> (<c>ids-only|metadata|content</c>), <c>actorkind</c>/<c>actorid</c> (when known), and <c>data</c>.
/// Instants are UTC ISO-8601 with microseconds; durations integer microseconds.
/// </summary>
public static class IntegrationEventCodec
{
    public const int PayloadVersion = 1;
    public const int MaximumPayloadBytes = 64 * 1024;

    public static string EncodeData(IntegrationEventData data)
    {
        if (data.Value is null)
            throw new ArgumentException("Event data is required.", nameof(data));
        return EventJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", PayloadVersion);
            WriteData(new EventDataWriter(writer, metadata: true), data);
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The type, version or payload is unknown or invalid.</exception>
    public static IntegrationEventData DecodeData(string type, string json)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumPayloadBytes)
            throw new FormatException("The payload exceeds its bound.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var version) || EventJson.Long(version) != PayloadVersion)
                throw new FormatException("Unsupported payload version.");
            var reader = new EventDataReader(root);
            var data = Read(type, ref reader);
            reader.Done();
            if (data.Type != type)
                throw new FormatException("The payload type does not match.");
            return data;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new FormatException("The event payload is invalid.", error);
        }
    }

    public static string PrivacyName(EventPrivacy privacy) => privacy switch
    {
        EventPrivacy.IdsOnly => "ids-only",
        EventPrivacy.Metadata => "metadata",
        EventPrivacy.Content => "content",
        _ => throw new ArgumentOutOfRangeException(nameof(privacy)),
    };

    /// <summary>
    /// Renders <paramref name="stored"/> as a CloudEvents JSON object. <see cref="EventPrivacy.IdsOnly"/> writes identifiers only,
    /// <see cref="EventPrivacy.Metadata"/> adds metadata, <see cref="EventPrivacy.Content"/> adds metadata plus <paramref name="content"/>
    /// under <c>data.content</c> when supplied. Content passed at a lower level is rejected.
    /// </summary>
    public static string Render(StoredIntegrationEvent stored, EventPrivacy privacy, EventContent? content = null, CloudEventRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!Enum.IsDefined(privacy))
            throw new ArgumentOutOfRangeException(nameof(privacy));
        if (content is not null && privacy != EventPrivacy.Content)
            throw new ArgumentException("Content is rendered only at the content privacy level.", nameof(content));
        options ??= CloudEventRenderOptions.Default;
        var scope = stored.Scope;
        var @event = stored.Event;
        return EventJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("specversion"u8, "1.0"u8);
            Span<char> guidChars = stackalloc char[36];
            @event.Id.TryFormat(guidChars, out _, "D");
            writer.WriteString("id"u8, guidChars);
            writer.WriteString("source"u8, options.Source);
            writer.WriteString("type"u8, @event.Type);
            EventJson.WriteInstant(writer, "time"u8, @event.Time);
            writer.WriteString("subject"u8, @event.Subject);
            writer.WriteString("datacontenttype"u8, "application/json"u8);
            writer.WriteString("dataschema"u8, options.SchemaPrefix + @event.Type);
            Span<char> seqChars = stackalloc char[20];
            stored.Sequence.TryFormat(seqChars, out var seqLen, default, System.Globalization.CultureInfo.InvariantCulture);
            writer.WriteString("sequence"u8, seqChars[..seqLen]);
            writer.WriteString("tenantid"u8, scope.TenantId);
            writer.WriteString("projectid"u8, scope.ProjectId);
            writer.WriteString("environmentid"u8, scope.EnvironmentId);
            writer.WriteString("appid"u8, scope.AppId);
            writer.WriteString("workspaceid"u8, scope.WorkspaceId.Value);
            writer.WriteString("privacy"u8, PrivacyName(privacy));
            if (@event.Actor is { } actor)
            {
                writer.WriteString("actorkind"u8, actor.Kind);
                writer.WriteString("actorid"u8, actor.Id);
            }
            writer.WriteStartObject("data"u8);
            WriteData(new EventDataWriter(writer, privacy != EventPrivacy.IdsOnly), @event.Data);
            if (content is not null)
                WriteContent(writer, content);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    private static void WriteContent(Utf8JsonWriter writer, EventContent content)
    {
        writer.WriteStartObject("content");
        switch (content.Value)
        {
            case null:
                writer.WriteString("kind", "none");
                break;
            case MessageEventContent message:
                writer.WriteString("kind", "message");
                writer.WriteString("body", message.Body);
                writer.WriteString("contentType", message.ContentType);
                writer.WriteStartArray("attachments");
                foreach (var attachment in message.Attachments)
                {
                    writer.WriteStartObject();
                    writer.WriteString("fileName", attachment.FileName);
                    writer.WriteString("mediaType", attachment.MediaType);
                    writer.WriteNumber("sizeBytes", attachment.SizeBytes);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                if (message.ExternalId is not null)
                    writer.WriteString("externalId", message.ExternalId);
                break;
            case SurveyCommentContent survey:
                writer.WriteString("kind", "survey-comment");
                if (survey.Comment is not null)
                    writer.WriteString("comment", survey.Comment);
                break;
            case ContactEventContent contact:
                writer.WriteString("kind", "contact");
                WriteContact(writer, contact);
                break;
        }
        if (content.Contact is { } related)
        {
            writer.WriteStartObject("contact");
            WriteContact(writer, related);
            writer.WriteEndObject();
        }
        writer.WriteStartArray("maskedFields");
        foreach (var field in content.MaskedFields)
            writer.WriteStringValue(field);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteContact(Utf8JsonWriter writer, ContactEventContent contact)
    {
        if (contact.DisplayName is not null)
            writer.WriteString("displayName", contact.DisplayName);
        writer.WriteStartArray("identities");
        foreach (var identity in contact.Identities)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", identity.Kind);
            if (identity.Namespace.Length > 0)
                writer.WriteString("namespace", identity.Namespace);
            writer.WriteString("value", identity.Value);
            writer.WriteBoolean("verified", identity.Verified);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteData(EventDataWriter w, IntegrationEventData data)
    {
        switch (data)
        {
            case ConversationCreatedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("contact", d.Contact.Value);
                w.Id("assignee", d.Assignee?.Value, true);
                w.Id("team", d.Team?.Value, true);
                w.Text("status", d.Status);
                w.Text("priority", d.Priority);
                w.Labels("labels", d.Labels);
                w.Text("channel", d.Channel);
                break;
            case ConversationStatusChangedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Text("previous", d.Previous);
                w.Text("current", d.Current);
                w.Instant("snoozedUntil", d.SnoozedUntil);
                break;
            case ConversationAssignedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("previous", d.Previous?.Value, true);
                w.Id("current", d.Current?.Value, true);
                break;
            case ConversationTeamChangedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("previous", d.Previous?.Value, true);
                w.Id("current", d.Current?.Value, true);
                break;
            case ConversationPriorityChangedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Text("previous", d.Previous);
                w.Text("current", d.Current);
                break;
            case ConversationLabelsChangedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Labels("added", d.Added);
                w.Labels("removed", d.Removed);
                break;
            case ConversationMessageReceivedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("message", d.Message.Value);
                w.Id("contact", d.Contact.Value);
                w.Number("timelineSequence", d.TimelineSequence);
                w.Text("contentType", d.ContentType);
                w.Number("attachmentCount", d.AttachmentCount);
                break;
            case ConversationMessageSentData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("message", d.Message.Value);
                w.Actor("sender", d.Sender);
                w.Number("timelineSequence", d.TimelineSequence);
                w.Text("contentType", d.ContentType);
                w.Number("attachmentCount", d.AttachmentCount);
                break;
            case ConversationNoteAddedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("note", d.Note.Value);
                w.Actor("author", d.Author);
                break;
            case ConversationResolvedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Text("status", d.Status);
                break;
            case ConversationReopenedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Text("previous", d.Previous);
                w.Text("current", d.Current);
                break;
            case ConversationDeletedData d:
                Conversation(w, d.Conversation, d.Inbox);
                break;
            case ContactCreatedData d:
                w.Id("contact", d.Contact.Value);
                break;
            case ContactUpdatedData d:
                w.Id("contact", d.Contact.Value);
                break;
            case ContactDeletedData d:
                w.Id("contact", d.Contact.Value);
                break;
            case ContactMergedData d:
                w.Id("contact", d.Contact.Value);
                w.Id("target", d.Target.Value);
                w.Id("merge", d.Merge.ToString("D"));
                break;
            case ContactUnmergedData d:
                w.Id("contact", d.Contact.Value);
                w.Id("target", d.Target.Value);
                w.Id("merge", d.Merge.ToString("D"));
                break;
            case ContactIdentityLinkedData d:
                w.Id("contact", d.Contact.Value);
                w.Text("identityKind", d.IdentityKind);
                w.Boolean("verified", d.Verified);
                break;
            case SlaAppliedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("policy", d.Policy);
                w.Text("key", d.Key);
                w.Text("basis", d.Basis);
                w.Text("calendar", d.Calendar);
                w.Duration("firstResponseTarget", d.FirstResponseTarget);
                w.Duration("nextResponseTarget", d.NextResponseTarget);
                w.Duration("resolutionTarget", d.ResolutionTarget);
                w.Instant("at", d.At);
                break;
            case SlaWarningData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("policy", d.Policy);
                w.Text("key", d.Key);
                w.Text("metric", d.Metric);
                w.Number("cycle", d.Cycle);
                w.Number("thresholdPercent", d.ThresholdPercent);
                w.Duration("target", d.Target);
                w.Instant("at", d.At);
                w.Instant("dueAt", d.DueAt);
                break;
            case SlaBreachedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("policy", d.Policy);
                w.Text("key", d.Key);
                w.Text("metric", d.Metric);
                w.Number("cycle", d.Cycle);
                w.Duration("target", d.Target);
                w.Instant("at", d.At);
                break;
            case SlaMetData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("policy", d.Policy);
                w.Text("key", d.Key);
                w.Text("metric", d.Metric);
                w.Number("cycle", d.Cycle);
                w.Duration("target", d.Target);
                w.Duration("elapsed", d.Elapsed);
                w.Boolean("breached", d.Breached);
                w.Instant("at", d.At);
                break;
            case RoutingAssignedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("agent", d.Agent.Value);
                w.Number("revision", d.Revision);
                break;
            case RoutingQueuedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("targetInbox", d.TargetInbox.Value);
                w.Text("reason", d.Reason);
                break;
            case RoutingOverflowedData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("targetTeam", d.TargetTeam?.Value, true);
                w.Id("targetInbox", d.TargetInbox?.Value, true);
                break;
            case RoutingQueueWaitExceededData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("targetInbox", d.TargetInbox.Value);
                break;
            case SurveyRespondedData d:
                w.Id("conversation", d.Conversation.Value);
                w.Id("contact", d.Contact.Value);
                w.Id("survey", d.Survey);
                w.Id("invitation", d.Invitation);
                w.Id("inbox", d.Inbox?.Value, true);
                w.Id("team", d.Team?.Value, true);
                w.Id("agent", d.Agent?.Value, true);
                w.Text("kind", d.Kind);
                w.Number("score", d.Score);
                w.Instant("respondedAt", d.RespondedAt);
                break;
            case ChannelDeliveryStatusData d:
                Conversation(w, d.Conversation, d.Inbox);
                w.Id("message", d.Message.Value);
                w.Text("state", d.State);
                break;
        }
    }

    private static void Conversation(EventDataWriter w, ConversationId conversation, InboxId inbox)
    {
        w.Id("conversation", conversation.Value);
        w.Id("inbox", inbox.Value);
    }

    private static IntegrationEventData Read(string type, ref EventDataReader r)
    {
        static UserId? U(ref EventDataReader r, string name) => r.OptionalString(name) is { } value ? new UserId(value) : null;
        static TeamId? T(ref EventDataReader r, string name) => r.OptionalString(name) is { } value ? new TeamId(value) : null;
        static InboxId? OI(ref EventDataReader r, string name) => r.OptionalString(name) is { } value ? new InboxId(value) : null;
        static Guid G(ref EventDataReader r, string name) => Guid.TryParseExact(r.String(name), "D", out var value) ? value : throw new FormatException("Expected a UUID.");
        return type switch
        {
            IntegrationEventTypes.ConversationCreated => new ConversationCreatedData(new(r.String("conversation")), new(r.String("inbox")), new ContactId(r.String("contact")), U(ref r, "assignee"), T(ref r, "team"), r.String("status"),
                r.String("priority"), r.Labels("labels"), r.OptionalString("channel")),
            IntegrationEventTypes.ConversationStatusChanged => new ConversationStatusChangedData(new(r.String("conversation")), new(r.String("inbox")), r.String("previous"), r.String("current"), r.OptionalInstant("snoozedUntil")),
            IntegrationEventTypes.ConversationAssigned => new ConversationAssignedData(new(r.String("conversation")), new(r.String("inbox")), U(ref r, "previous"), U(ref r, "current")),
            IntegrationEventTypes.ConversationTeamChanged => new ConversationTeamChangedData(new(r.String("conversation")), new(r.String("inbox")), T(ref r, "previous"), T(ref r, "current")),
            IntegrationEventTypes.ConversationPriorityChanged => new ConversationPriorityChangedData(new(r.String("conversation")), new(r.String("inbox")), r.String("previous"), r.String("current")),
            IntegrationEventTypes.ConversationLabelsChanged => new ConversationLabelsChangedData(new(r.String("conversation")), new(r.String("inbox")), r.Labels("added"), r.Labels("removed")),
            IntegrationEventTypes.ConversationMessageReceived => new ConversationMessageReceivedData(new(r.String("conversation")), new(r.String("inbox")), new MessageId(r.String("message")), new ContactId(r.String("contact")),
                r.Long("timelineSequence"), r.String("contentType"), r.Int("attachmentCount")),
            IntegrationEventTypes.ConversationMessageSent => new ConversationMessageSentData(new(r.String("conversation")), new(r.String("inbox")), new MessageId(r.String("message")), r.Actor("sender"),
                r.Long("timelineSequence"), r.String("contentType"), r.Int("attachmentCount")),
            IntegrationEventTypes.ConversationNoteAdded => new ConversationNoteAddedData(new(r.String("conversation")), new(r.String("inbox")), new MessageId(r.String("note")), r.Actor("author")),
            IntegrationEventTypes.ConversationResolved => new ConversationResolvedData(new(r.String("conversation")), new(r.String("inbox")), r.String("status")),
            IntegrationEventTypes.ConversationReopened => new ConversationReopenedData(new(r.String("conversation")), new(r.String("inbox")), r.String("previous"), r.String("current")),
            IntegrationEventTypes.ConversationDeleted => new ConversationDeletedData(new(r.String("conversation")), new(r.String("inbox"))),
            IntegrationEventTypes.ContactCreated => new ContactCreatedData(new ContactId(r.String("contact"))),
            IntegrationEventTypes.ContactUpdated => new ContactUpdatedData(new ContactId(r.String("contact"))),
            IntegrationEventTypes.ContactDeleted => new ContactDeletedData(new ContactId(r.String("contact"))),
            IntegrationEventTypes.ContactMerged => new ContactMergedData(new ContactId(r.String("contact")), new ContactId(r.String("target")), G(ref r, "merge")),
            IntegrationEventTypes.ContactUnmerged => new ContactUnmergedData(new ContactId(r.String("contact")), new ContactId(r.String("target")), G(ref r, "merge")),
            IntegrationEventTypes.ContactIdentityLinked => new ContactIdentityLinkedData(new ContactId(r.String("contact")), r.String("identityKind"), r.Bool("verified")),
            IntegrationEventTypes.SlaApplied => new SlaAppliedData(new(r.String("conversation")), new(r.String("inbox")), r.String("policy"), r.String("key"), r.String("basis"), r.OptionalString("calendar"),
                r.OptionalDuration("firstResponseTarget"), r.OptionalDuration("nextResponseTarget"), r.OptionalDuration("resolutionTarget"), r.Instant("at")),
            IntegrationEventTypes.SlaWarning => new SlaWarningData(new(r.String("conversation")), new(r.String("inbox")), r.String("policy"), r.String("key"), r.String("metric"), r.Int("cycle"), r.Int("thresholdPercent"),
                r.Duration("target"), r.Instant("at"), r.OptionalInstant("dueAt")),
            IntegrationEventTypes.SlaBreached => new SlaBreachedData(new(r.String("conversation")), new(r.String("inbox")), r.String("policy"), r.String("key"), r.String("metric"), r.Int("cycle"), r.Duration("target"),
                r.Instant("at")),
            IntegrationEventTypes.SlaMet => new SlaMetData(new(r.String("conversation")), new(r.String("inbox")), r.String("policy"), r.String("key"), r.String("metric"), r.Int("cycle"), r.Duration("target"),
                r.Duration("elapsed"), r.Bool("breached"), r.Instant("at")),
            IntegrationEventTypes.RoutingAssigned => new RoutingAssignedData(new(r.String("conversation")), new(r.String("inbox")), new UserId(r.String("agent")), r.Long("revision")),
            IntegrationEventTypes.RoutingQueued => new RoutingQueuedData(new(r.String("conversation")), new(r.String("inbox")), new InboxId(r.String("targetInbox")), r.String("reason")),
            IntegrationEventTypes.RoutingOverflowed => new RoutingOverflowedData(new(r.String("conversation")), new(r.String("inbox")), T(ref r, "targetTeam"), OI(ref r, "targetInbox")),
            IntegrationEventTypes.RoutingQueueWaitExceeded => new RoutingQueueWaitExceededData(new(r.String("conversation")), new(r.String("inbox")), new InboxId(r.String("targetInbox"))),
            IntegrationEventTypes.SurveyResponded => new SurveyRespondedData(new ConversationId(r.String("conversation")), new ContactId(r.String("contact")), r.String("survey"),
                r.String("invitation"), OI(ref r, "inbox"), T(ref r, "team"), U(ref r, "agent"), r.String("kind"), r.Int("score"), r.Instant("respondedAt")),
            IntegrationEventTypes.ChannelDeliveryStatus => new ChannelDeliveryStatusData(new(r.String("conversation")), new(r.String("inbox")), new MessageId(r.String("message")), r.String("state")),
            _ => throw new FormatException("Unknown event type."),
        };
    }
}
