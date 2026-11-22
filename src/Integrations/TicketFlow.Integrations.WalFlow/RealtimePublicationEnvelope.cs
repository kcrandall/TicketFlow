using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Realtime;

namespace TicketFlow.Integrations.WalFlow;

/// <summary>A decoded cross-node realtime publication with the originating node's identifier.</summary>
public sealed record RealtimeEnvelope(string Origin, RealtimePublication Publication);

/// <summary>
/// Explicit versioned JSON codec (<c>{"v":1,"origin":"...","topic":{...},"event":{...}}</c>) for transporting
/// <see cref="RealtimePublication"/>s across nodes. Unions (<see cref="RealtimeTopic"/>, <see cref="RealtimeEvent"/>,
/// <see cref="RealtimeActor"/>) are never serialized directly; every variant carries an explicit string discriminator <c>"k"</c>.
/// </summary>
public static class RealtimePublicationEnvelope
{
    public const int FormatVersion = 1;

    /// <summary>Serializes <paramref name="publication"/> with <paramref name="origin"/> into compact UTF-8 JSON.</summary>
    /// <exception cref="FormatException">The encoded envelope exceeds <paramref name="maximumBytes"/>.</exception>
    public static string Serialize(RealtimePublication publication, string origin, int maximumBytes = WalFlowRealtimeOptions.MaximumEnvelopeBytesLimit)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (string.IsNullOrWhiteSpace(origin) || origin.Length > 128)
            throw new ArgumentException("An origin identifier (1-128 chars) is required.", nameof(origin));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteString("origin", origin);
            writer.WritePropertyName("topic");
            WriteTopic(writer, publication.Topic);
            writer.WritePropertyName("event");
            WriteEvent(writer, publication.Event);
            writer.WriteEndObject();
        }
        if (buffer.WrittenCount > maximumBytes)
            throw new FormatException($"Serialized realtime envelope ({buffer.WrittenCount} bytes) exceeds limit ({maximumBytes} bytes).");
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Strictly decodes and validates a versioned envelope JSON string.</summary>
    public static RealtimeEnvelope Deserialize(string json, int maximumBytes = WalFlowRealtimeOptions.MaximumEnvelopeBytesLimit)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > maximumBytes)
            throw new FormatException("Realtime envelope exceeds maximum byte size.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Realtime envelope must be a JSON object.");
            if (root.GetProperty("v").GetInt32() != FormatVersion)
                throw new FormatException("Unsupported realtime envelope version.");
            var origin = RequireString(root, "origin");
            if (origin.Length is 0 or > 128)
                throw new FormatException("Invalid origin identifier.");
            var topic = ReadTopic(root.GetProperty("topic"));
            var realtimeEvent = ReadEvent(root.GetProperty("event"), topic.Scope);
            return new RealtimeEnvelope(origin, new RealtimePublication(topic, realtimeEvent));
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new FormatException("Malformed realtime envelope.", error);
        }
    }

    public static bool TryDeserialize(string json, out RealtimeEnvelope? envelope, out string? error, int maximumBytes = WalFlowRealtimeOptions.MaximumEnvelopeBytesLimit)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            envelope = Deserialize(json, maximumBytes);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            envelope = null;
            error = ex.Message;
            return false;
        }
    }

    private static void WriteScope(Utf8JsonWriter writer, TicketScope scope)
    {
        writer.WriteStartObject();
        writer.WriteString("t", scope.TenantId);
        writer.WriteString("p", scope.ProjectId);
        writer.WriteString("e", scope.EnvironmentId);
        writer.WriteString("a", scope.AppId);
        writer.WriteString("w", scope.WorkspaceId.Value);
        writer.WriteEndObject();
    }

    private static TicketScope ReadScope(JsonElement element) =>
        new(RequireString(element, "t"), RequireString(element, "p"), RequireString(element, "e"), RequireString(element, "a"), new WorkspaceId(RequireString(element, "w")));

    private static void WriteTopic(Utf8JsonWriter writer, RealtimeTopic topic)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("scope");
        WriteScope(writer, topic.Scope);
        switch (topic)
        {
            case ConversationTopic c:
                writer.WriteString("k", "conversation");
                writer.WriteString("id", c.Conversation.Value);
                break;
            case InboxQueueTopic q:
                writer.WriteString("k", "inbox-queue");
                writer.WriteString("id", q.Inbox.Value);
                break;
            case AgentTopic a:
                writer.WriteString("k", "agent");
                writer.WriteString("id", a.Agent.Value);
                break;
            case VisitorSessionTopic v:
                writer.WriteString("k", "visitor-session");
                writer.WriteString("id", v.Session.Value);
                break;
        }
        writer.WriteEndObject();
    }

    private static RealtimeTopic ReadTopic(JsonElement element)
    {
        var scope = ReadScope(element.GetProperty("scope"));
        var id = RequireString(element, "id");
        return RequireString(element, "k") switch
        {
            "conversation" => new ConversationTopic(scope, new ConversationId(id)),
            "inbox-queue" => new InboxQueueTopic(scope, new InboxId(id)),
            "agent" => new AgentTopic(scope, new UserId(id)),
            "visitor-session" => new VisitorSessionTopic(scope, new VisitorSessionId(id)),
            var unknown => throw new FormatException($"Unknown topic discriminator '{unknown}'."),
        };
    }

    private static void WriteActor(Utf8JsonWriter writer, RealtimeActor actor)
    {
        writer.WriteStartObject();
        switch (actor)
        {
            case AgentActor a:
                writer.WriteString("k", "agent");
                writer.WriteString("agent", a.Agent.Value);
                break;
            case VisitorActor:
                writer.WriteString("k", "visitor");
                break;
            case SupportActor:
                writer.WriteString("k", "support");
                break;
        }
        writer.WriteEndObject();
    }

    private static RealtimeActor ReadActor(JsonElement element) => RequireString(element, "k") switch
    {
        "agent" => new AgentActor(new UserId(RequireString(element, "agent"))),
        "visitor" => VisitorActor.Instance,
        "support" => SupportActor.Instance,
        var unknown => throw new FormatException($"Unknown realtime actor discriminator '{unknown}'."),
    };

    private static void WriteEvent(Utf8JsonWriter writer, RealtimeEvent realtimeEvent)
    {
        writer.WriteStartObject();
        switch (realtimeEvent)
        {
            case ConversationChangedEvent c:
                writer.WriteString("k", "conversation-changed");
                writer.WriteString("conversation", c.Conversation.Value);
                writer.WriteString("inbox", c.Inbox.Value);
                writer.WriteNumber("revision", c.Revision);
                writer.WriteNumber("kind", (int)c.Kind);
                writer.WriteStartArray("events");
                foreach (var summary in c.Events)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("seq", summary.Sequence);
                    writer.WriteNumber("kind", (int)summary.Kind);
                    if (summary.Message is { } msg)
                        writer.WriteString("msg", msg.Value);
                    if (summary.Direction is { } dir)
                        writer.WriteNumber("dir", (int)dir);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                if (c.Message is { } message)
                    writer.WriteString("message", message.Value);
                if (c.Delivery is { } delivery)
                    writer.WriteNumber("delivery", (int)delivery);
                writer.WriteString("occurredAt", c.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case PresenceChangedEvent p:
                writer.WriteString("k", "presence");
                writer.WriteString("conversation", p.Conversation.Value);
                writer.WriteString("user", p.User.Value);
                if (p.Activity is { } activity)
                    writer.WriteNumber("activity", (int)activity);
                writer.WriteString("at", p.At.ToString("O", CultureInfo.InvariantCulture));
                break;
            case ReplyLockChangedEvent r:
                writer.WriteString("k", "reply-lock");
                writer.WriteString("conversation", r.Conversation.Value);
                if (r.Holder is { } holder)
                    writer.WriteString("holder", holder.Value);
                if (r.ExpiresAt is { } expiresAt)
                    writer.WriteString("expiresAt", expiresAt.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("at", r.At.ToString("O", CultureInfo.InvariantCulture));
                break;
            case AgentStatusChangedEvent a:
                writer.WriteString("k", "agent-status");
                writer.WriteString("agent", a.Agent.Value);
                writer.WriteNumber("status", (int)a.Status);
                writer.WriteString("at", a.At.ToString("O", CultureInfo.InvariantCulture));
                break;
            case ConversationAssignedEvent a:
                writer.WriteString("k", "conversation-assigned");
                writer.WriteString("conversation", a.Conversation.Value);
                writer.WriteString("agent", a.Agent.Value);
                writer.WriteNumber("revision", a.Revision);
                writer.WriteString("at", a.At.ToString("O", CultureInfo.InvariantCulture));
                break;
            case QueueChangedEvent q:
                writer.WriteString("k", "queue-changed");
                writer.WriteString("conversation", q.Conversation.Value);
                writer.WriteString("inbox", q.Inbox.Value);
                writer.WriteNumber("change", (int)q.Change);
                writer.WriteString("at", q.At.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TypingEvent t:
                writer.WriteString("k", "typing");
                writer.WriteString("conversation", t.Conversation.Value);
                writer.WritePropertyName("actor");
                WriteActor(writer, t.Actor);
                writer.WriteBoolean("typing", t.Typing);
                writer.WriteString("at", t.At.ToString("O", CultureInfo.InvariantCulture));
                break;
            case ReadReceiptEvent r:
                writer.WriteString("k", "read-receipt");
                writer.WriteString("conversation", r.Conversation.Value);
                writer.WritePropertyName("actor");
                WriteActor(writer, r.Actor);
                writer.WriteNumber("upToSequence", r.UpToSequence);
                writer.WriteString("at", r.At.ToString("O", CultureInfo.InvariantCulture));
                break;
            case VisitorMessageEvent v:
                writer.WriteString("k", "visitor-message");
                writer.WriteString("session", v.Session.Value);
                writer.WriteString("conversation", v.Conversation.Value);
                writer.WriteString("message", v.Message.Value);
                writer.WriteNumber("sequence", v.Sequence);
                writer.WriteNumber("direction", (int)v.Direction);
                writer.WriteString("body", v.Body);
                writer.WriteNumber("contentType", (int)v.ContentType);
                writer.WriteStartArray("attachments");
                foreach (var attachment in v.Attachments)
                {
                    writer.WriteStartObject();
                    writer.WriteString("storageRef", attachment.StorageRef);
                    writer.WriteString("fileName", attachment.FileName);
                    writer.WriteString("mediaType", attachment.MediaType);
                    writer.WriteNumber("sizeBytes", attachment.SizeBytes);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteString("at", v.At.ToString("O", CultureInfo.InvariantCulture));
                break;
            case VisitorSessionEndedEvent e:
                writer.WriteString("k", "visitor-session-ended");
                writer.WriteString("session", e.Session.Value);
                writer.WriteNumber("reason", (int)e.Reason);
                writer.WriteString("at", e.At.ToString("O", CultureInfo.InvariantCulture));
                break;
        }
        writer.WriteEndObject();
    }

    private static RealtimeEvent ReadEvent(JsonElement element, TicketScope scope) => RequireString(element, "k") switch
    {
        "conversation-changed" => ReadConversationChanged(element, scope),
        "presence" => new PresenceChangedEvent(scope, new ConversationId(RequireString(element, "conversation")), new UserId(RequireString(element, "user")),
            OptionalEnum<RealtimeActivity>(element, "activity"), RequireInstant(element, "at")),
        "reply-lock" => new ReplyLockChangedEvent(scope, new ConversationId(RequireString(element, "conversation")),
            OptionalString(element, "holder") is { } holder ? new UserId(holder) : null, OptionalInstant(element, "expiresAt"), RequireInstant(element, "at")),
        "agent-status" => new AgentStatusChangedEvent(scope, new UserId(RequireString(element, "agent")), RequireEnum<RealtimeAgentStatus>(element, "status"),
            RequireInstant(element, "at")),
        "conversation-assigned" => new ConversationAssignedEvent(scope, new ConversationId(RequireString(element, "conversation")),
            new UserId(RequireString(element, "agent")), element.GetProperty("revision").GetInt64(), RequireInstant(element, "at")),
        "queue-changed" => new QueueChangedEvent(scope, new ConversationId(RequireString(element, "conversation")),
            new InboxId(RequireString(element, "inbox")), RequireEnum<QueueChange>(element, "change"), RequireInstant(element, "at")),
        "typing" => new TypingEvent(scope, new ConversationId(RequireString(element, "conversation")), ReadActor(element.GetProperty("actor")),
            element.GetProperty("typing").GetBoolean(), RequireInstant(element, "at")),
        "read-receipt" => new ReadReceiptEvent(scope, new ConversationId(RequireString(element, "conversation")), ReadActor(element.GetProperty("actor")),
            element.GetProperty("upToSequence").GetInt64(), RequireInstant(element, "at")),
        "visitor-message" => ReadVisitorMessage(element, scope),
        "visitor-session-ended" => new VisitorSessionEndedEvent(scope, new VisitorSessionId(RequireString(element, "session")),
            RequireEnum<VisitorSessionEnd>(element, "reason"), RequireInstant(element, "at")),
        var unknown => throw new FormatException($"Unknown realtime event discriminator '{unknown}'."),
    };

    private static ConversationChangedEvent ReadConversationChanged(JsonElement element, TicketScope scope)
    {
        var summaries = new List<ConversationEventSummary>();
        foreach (var item in element.GetProperty("events").EnumerateArray())
            summaries.Add(new ConversationEventSummary(
                item.GetProperty("seq").GetInt64(),
                RequireEnum<ConversationEventKind>(item, "kind"),
                OptionalString(item, "msg") is { } msg ? new MessageId(msg) : null,
                OptionalEnum<MessageDirection>(item, "dir")));
        return new ConversationChangedEvent(scope, new ConversationId(RequireString(element, "conversation")), new InboxId(RequireString(element, "inbox")),
            element.GetProperty("revision").GetInt64(), RequireEnum<ConversationChangeKind>(element, "kind"), summaries,
            OptionalString(element, "message") is { } message ? new MessageId(message) : null,
            OptionalEnum<MessageDeliveryState>(element, "delivery"), RequireInstant(element, "occurredAt"));
    }

    private static VisitorMessageEvent ReadVisitorMessage(JsonElement element, TicketScope scope)
    {
        var attachments = new List<AttachmentRef>();
        foreach (var item in element.GetProperty("attachments").EnumerateArray())
            attachments.Add(new AttachmentRef(RequireString(item, "storageRef"), RequireString(item, "fileName"), RequireString(item, "mediaType"),
                item.GetProperty("sizeBytes").GetInt64()));
        return new VisitorMessageEvent(scope, new VisitorSessionId(RequireString(element, "session")), new ConversationId(RequireString(element, "conversation")),
            new MessageId(RequireString(element, "message")), element.GetProperty("sequence").GetInt64(), RequireEnum<MessageDirection>(element, "direction"),
            RequireString(element, "body"), RequireEnum<MessageContentType>(element, "contentType"), attachments, RequireInstant(element, "at"));
    }

    private static string RequireString(JsonElement element, string name) =>
        element.GetProperty(name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw new FormatException($"Property '{name}' must be a string.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"Property '{name}' must be a string."))
            : null;

    private static DateTimeOffset RequireInstant(JsonElement element, string name) =>
        DateTimeOffset.ParseExact(RequireString(element, name), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? OptionalInstant(JsonElement element, string name) =>
        OptionalString(element, name) is { } text ? DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null;

    private static TEnum RequireEnum<TEnum>(JsonElement element, string name) where TEnum : struct, Enum
    {
        var raw = element.GetProperty(name).GetInt32();
        var value = (TEnum)Enum.ToObject(typeof(TEnum), raw);
        return Enum.IsDefined(value) ? value : throw new FormatException($"Invalid enum value for '{name}'.");
    }

    private static TEnum? OptionalEnum<TEnum>(JsonElement element, string name) where TEnum : struct, Enum =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null ? RequireEnum<TEnum>(element, name) : null;
}
