using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// Versioned persisted JSON for conversation attribute maps, contact redirects, timeline event payloads and outbox change payloads.
/// Every envelope is <c>{"v":1,...}</c>. Events carry an explicit <c>"k"</c> discriminator (<c>message</c>, <c>note</c>,
/// <c>assignment</c>, <c>team</c>, <c>status</c>, <c>priority</c>, <c>labels</c>, <c>attribute</c>, <c>participant</c>,
/// <c>contact</c>, <c>system</c>, <c>extension</c>); statuses <c>"s"</c>, attribute values <c>"t"</c>, parties <c>"p"</c> and
/// delivery states <c>"s"</c>. Unions are never serialized directly. Decoding re-validates every value and rejects unknown versions,
/// discriminators and properties.
/// </summary>
public static class ConversationJsonCodec
{
    public const int FormatVersion = 1;

    /// <summary>Maximum encoded size in UTF-8 bytes.</summary>
    public const int MaximumEncodedBytes = 1024 * 1024;

    private const string EmptyAttributesJson = "{\"v\":1,\"a\":{}}";
    private const string EmptyConditionAttributesJson = "{}";
    private const string EmptyRedirectsJson = "{\"v\":1,\"r\":[]}";
    private const int MaxPooledBufferCapacity = 64 * 1024;
    private static readonly IReadOnlyDictionary<string, ConversationAttributeValue> EmptyAttributes = FrozenDictionary<string, ConversationAttributeValue>.Empty;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    public static string SerializeAttributes(IReadOnlyDictionary<string, ConversationAttributeValue> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count == 0)
            return EmptyAttributesJson;
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            writer.WritePropertyName("a"u8);
            WriteAttributes(writer, attributes);
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, oversized or of an unknown version.</exception>
    public static IReadOnlyDictionary<string, ConversationAttributeValue> DeserializeAttributes(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json == EmptyAttributesJson)
            return EmptyAttributes;
        return Read(json, static root =>
        {
            RequireProperties(root, "v", "a");
            RequireVersion(root);
            return ReadAttributes(root.GetProperty("a"u8));
        });
    }

    /// <summary>The string-valued attribute map used by Access conditions (<see cref="ConversationAttributeValue.ConditionValue"/>); not versioned.</summary>
    public static string SerializeConditionAttributes(IReadOnlyDictionary<string, ConversationAttributeValue> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count == 0)
            return EmptyConditionAttributesJson;
        return Write(writer =>
        {
            writer.WriteStartObject();
            var keys = new string[attributes.Count];
            var index = 0;
            foreach (var key in attributes.Keys)
                keys[index++] = key;
            if (keys.Length > 1)
                Array.Sort(keys, StringComparer.Ordinal);
            foreach (var key in keys)
                if (attributes[key].ConditionValue is { } value)
                    writer.WriteString(key, value);
            writer.WriteEndObject();
        });
    }

    public static string SerializeRedirects(IReadOnlyList<ContactRedirect> redirects)
    {
        ArgumentNullException.ThrowIfNull(redirects);
        if (redirects.Count == 0)
            return EmptyRedirectsJson;
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            writer.WriteStartArray("r"u8);
            foreach (var redirect in redirects)
            {
                writer.WriteStartObject();
                writer.WriteString("m"u8, redirect.Merge);
                writer.WriteString("p"u8, redirect.Previous.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static IReadOnlyList<ContactRedirect> DeserializeRedirects(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json == EmptyRedirectsJson)
            return [];
        return Read(json, static root =>
        {
            RequireProperties(root, "v", "r");
            RequireVersion(root);
            var arr = root.GetProperty("r"u8);
            var count = arr.GetArrayLength();
            if (count == 0)
                return (IReadOnlyList<ContactRedirect>)[];
            var result = new ContactRedirect[count];
            var i = 0;
            foreach (var item in arr.EnumerateArray())
            {
                RequireProperties(item, "m", "p");
                result[i++] = new ContactRedirect(item.GetProperty("m"u8).GetGuid(), new ContactId(String(item.GetProperty("p"u8))));
            }
            return (IReadOnlyList<ContactRedirect>)result;
        });
    }

    /// <summary>Encodes an event payload (the timeline envelope's sequence, identifier, time and actor are stored by the caller).</summary>
    public static string SerializeEvent(ConversationEvent @event)
    {
        if (@event.Value is null)
            throw new ArgumentException("An event is required.", nameof(@event));
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            switch (@event)
            {
                case MessageEvent message:
                    writer.WriteString("k"u8, "message"u8);
                    writer.WriteString("id"u8, message.Id.Value);
                    writer.WriteString("dir"u8, message.Direction switch
                    {
                        MessageDirection.Incoming => "in"u8,
                        MessageDirection.Outgoing => "out"u8,
                        _ => throw new ArgumentOutOfRangeException(nameof(@event)),
                    });
                    writer.WritePropertyName("from"u8);
                    WriteParty(writer, message.Sender);
                    writer.WriteString("body"u8, message.Body);
                    writer.WriteString("ct"u8, ContentTypeUtf8(message.ContentType));
                    WriteAttachments(writer, message.Attachments);
                    writer.WriteString("ext"u8, message.ExternalId);
                    writer.WritePropertyName("del"u8);
                    WriteDelivery(writer, message.Delivery);
                    break;
                case NoteEvent note:
                    writer.WriteString("k"u8, "note"u8);
                    writer.WriteString("id"u8, note.Id.Value);
                    writer.WriteString("body"u8, note.Body);
                    WriteAttachments(writer, note.Attachments);
                    break;
                case AssignmentChanged assignment:
                    writer.WriteString("k"u8, "assignment"u8);
                    writer.WriteString("prev"u8, assignment.Previous?.Value);
                    writer.WriteString("cur"u8, assignment.Current?.Value);
                    break;
                case TeamChanged team:
                    writer.WriteString("k"u8, "team"u8);
                    writer.WriteString("prev"u8, team.Previous?.Value);
                    writer.WriteString("cur"u8, team.Current?.Value);
                    break;
                case StatusChanged status:
                    writer.WriteString("k"u8, "status"u8);
                    writer.WritePropertyName("prev"u8);
                    WriteStatus(writer, status.Previous);
                    writer.WritePropertyName("cur"u8);
                    WriteStatus(writer, status.Current);
                    break;
                case PriorityChanged priority:
                    writer.WriteString("k"u8, "priority"u8);
                    writer.WriteNumber("prev"u8, (int)priority.Previous);
                    writer.WriteNumber("cur"u8, (int)priority.Current);
                    break;
                case LabelsChanged labels:
                    writer.WriteString("k"u8, "labels"u8);
                    writer.WriteStartArray("add"u8);
                    for (var i = 0; i < labels.Added.Count; i++)
                        writer.WriteStringValue(labels.Added[i].Value);
                    writer.WriteEndArray();
                    writer.WriteStartArray("rem"u8);
                    for (var i = 0; i < labels.Removed.Count; i++)
                        writer.WriteStringValue(labels.Removed[i].Value);
                    writer.WriteEndArray();
                    break;
                case AttributeChanged attribute:
                    writer.WriteString("k"u8, "attribute"u8);
                    writer.WriteString("key"u8, attribute.Key);
                    writer.WritePropertyName("prev"u8);
                    WriteOptionalValue(writer, attribute.Previous);
                    writer.WritePropertyName("cur"u8);
                    WriteOptionalValue(writer, attribute.Current);
                    break;
                case ParticipantChanged participant:
                    writer.WriteString("k"u8, "participant"u8);
                    writer.WriteString("user"u8, participant.Participant.Value);
                    writer.WriteString("change"u8, participant.Change switch
                    {
                        ParticipantChange.Added => "added"u8,
                        ParticipantChange.Removed => "removed"u8,
                        _ => throw new ArgumentOutOfRangeException(nameof(@event)),
                    });
                    break;
                case ContactChanged contact:
                    writer.WriteString("k"u8, "contact"u8);
                    writer.WriteString("prev"u8, contact.Previous.Value);
                    writer.WriteString("cur"u8, contact.Current.Value);
                    if (contact.Merge is { } merge)
                        writer.WriteString("merge"u8, merge);
                    else
                        writer.WriteNull("merge"u8);
                    break;
                case SystemEvent system:
                    writer.WriteString("k"u8, "system"u8);
                    writer.WriteString("code"u8, system.Code);
                    writer.WriteString("detail"u8, system.Detail);
                    break;
                case ExtensionEvent extension:
                    writer.WriteString("k"u8, "extension"u8);
                    writer.WriteString("kind"u8, extension.Kind);
                    writer.WriteNumber("pv"u8, extension.PayloadVersion);
                    writer.WritePropertyName("p"u8);
                    writer.WriteRawValue(extension.Payload, skipInputValidation: true);
                    break;
            }
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, of an unknown version or discriminator, or fails validation.</exception>
    public static ConversationEvent DeserializeEvent(string json) =>
        Read(json, static root =>
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("k"u8, out var kind) || kind.ValueKind != JsonValueKind.String)
                throw new FormatException("An event discriminator is required.");
            ConversationEvent result;
            if (kind.ValueEquals("message"u8))
                result = ReadMessage(root);
            else if (kind.ValueEquals("note"u8))
            {
                RequireProperties(root, "v", "k", "id", "body", "att");
                result = new NoteEvent(new MessageId(String(root.GetProperty("id"u8))), String(root.GetProperty("body"u8)), ReadAttachments(root.GetProperty("att"u8)));
            }
            else if (kind.ValueEquals("assignment"u8))
            {
                RequireProperties(root, "v", "k", "prev", "cur");
                result = new AssignmentChanged(OptionalUser(root.GetProperty("prev"u8)), OptionalUser(root.GetProperty("cur"u8)));
            }
            else if (kind.ValueEquals("team"u8))
            {
                RequireProperties(root, "v", "k", "prev", "cur");
                result = new TeamChanged(OptionalTeam(root.GetProperty("prev"u8)), OptionalTeam(root.GetProperty("cur"u8)));
            }
            else if (kind.ValueEquals("status"u8))
            {
                RequireProperties(root, "v", "k", "prev", "cur");
                result = new StatusChanged(ReadStatus(root.GetProperty("prev"u8)), ReadStatus(root.GetProperty("cur"u8)));
            }
            else if (kind.ValueEquals("priority"u8))
            {
                RequireProperties(root, "v", "k", "prev", "cur");
                result = new PriorityChanged(Priority(root.GetProperty("prev"u8)), Priority(root.GetProperty("cur"u8)));
            }
            else if (kind.ValueEquals("labels"u8))
            {
                RequireProperties(root, "v", "k", "add", "rem");
                result = new LabelsChanged(ReadLabelIds(root.GetProperty("add"u8)), ReadLabelIds(root.GetProperty("rem"u8)));
            }
            else if (kind.ValueEquals("attribute"u8))
            {
                RequireProperties(root, "v", "k", "key", "prev", "cur");
                result = new AttributeChanged(String(root.GetProperty("key"u8)), ReadOptionalValue(root.GetProperty("prev"u8)), ReadOptionalValue(root.GetProperty("cur"u8)));
            }
            else if (kind.ValueEquals("participant"u8))
            {
                RequireProperties(root, "v", "k", "user", "change");
                var changeEl = root.GetProperty("change"u8);
                if (changeEl.ValueKind != JsonValueKind.String)
                    throw new FormatException("A string is required.");
                ParticipantChange change;
                if (changeEl.ValueEquals("added"u8))
                    change = ParticipantChange.Added;
                else if (changeEl.ValueEquals("removed"u8))
                    change = ParticipantChange.Removed;
                else
                    throw new FormatException("Unknown participant change.");
                result = new ParticipantChanged(new UserId(String(root.GetProperty("user"u8))), change);
            }
            else if (kind.ValueEquals("contact"u8))
            {
                RequireProperties(root, "v", "k", "prev", "cur", "merge");
                var mergeEl = root.GetProperty("merge"u8);
                result = new ContactChanged(new ContactId(String(root.GetProperty("prev"u8))), new ContactId(String(root.GetProperty("cur"u8))),
                    mergeEl.ValueKind == JsonValueKind.Null ? null : mergeEl.GetGuid());
            }
            else if (kind.ValueEquals("system"u8))
            {
                RequireProperties(root, "v", "k", "code", "detail");
                result = new SystemEvent(String(root.GetProperty("code"u8)), OptionalString(root.GetProperty("detail"u8)));
            }
            else if (kind.ValueEquals("extension"u8))
            {
                RequireProperties(root, "v", "k", "kind", "pv", "p");
                result = new ExtensionEvent(String(root.GetProperty("kind"u8)), root.GetProperty("pv"u8).GetInt32(), root.GetProperty("p"u8).GetRawText());
            }
            else
                throw new FormatException("Unknown event discriminator.");
            RequireVersion(root);
            return result;
        });

    /// <summary>The outbox change payload: event summaries and the message/delivery reference. Other fields are stored as columns.</summary>
    public static string SerializeChangePayload(ConversationChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            writer.WriteStartArray("e"u8);
            foreach (var summary in change.Events)
            {
                writer.WriteStartObject();
                writer.WriteNumber("q"u8, summary.Sequence);
                writer.WriteNumber("k"u8, (int)summary.Kind);
                writer.WriteString("m"u8, summary.Message?.Value);
                if (summary.Direction is { } direction)
                    writer.WriteNumber("d"u8, (int)direction);
                else
                    writer.WriteNull("d"u8);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("m"u8, change.Message?.Value);
            if (change.Delivery is { } delivery)
                writer.WriteNumber("ds"u8, (int)delivery);
            else
                writer.WriteNull("ds"u8);
            writer.WriteEndObject();
        });
    }

    public static ConversationChange DeserializeChange(string payload, Guid id, TicketScope scope, ConversationId conversation, InboxId inbox, long revision,
        ConversationChangeKind kind, DateTimeOffset occurredAt) =>
        Read(payload, root =>
        {
            RequireProperties(root, "v", "e", "m", "ds");
            RequireVersion(root);
            var arr = root.GetProperty("e"u8);
            var count = arr.GetArrayLength();
            IReadOnlyList<ConversationEventSummary> events;
            if (count == 0)
                events = [];
            else
            {
                var list = new ConversationEventSummary[count];
                var i = 0;
                foreach (var item in arr.EnumerateArray())
                {
                    RequireProperties(item, "q", "k", "m", "d");
                    var message = OptionalString(item.GetProperty("m"u8));
                    var dirEl = item.GetProperty("d"u8);
                    list[i++] = new ConversationEventSummary(item.GetProperty("q"u8).GetInt64(), (ConversationEventKind)item.GetProperty("k"u8).GetInt32(),
                        message is null ? null : new MessageId(message),
                        dirEl.ValueKind == JsonValueKind.Null ? null : (MessageDirection)dirEl.GetInt32());
                }
                events = list;
            }
            var messageId = OptionalString(root.GetProperty("m"u8));
            var state = root.GetProperty("ds"u8);
            return new ConversationChange(id, scope, conversation, inbox, revision, kind, events, messageId is null ? null : new MessageId(messageId),
                state.ValueKind == JsonValueKind.Null ? null : (MessageDeliveryState)state.GetInt32(), occurredAt);
        });

    /// <summary>Validates and compacts an extension payload: a JSON object of depth ≤ 16 and at most <paramref name="maximumBytes"/> UTF-8 bytes.</summary>
    internal static string NormalizeExtensionPayload(string payload, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(payload, parameterName);
        if (payload.Length > maximumBytes * 2 || (payload.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF') && !System.Text.Unicode.Utf16.IsValid(payload)))
            throw new ArgumentException("The extension payload is too large or malformed.", parameterName);
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Extension payloads are JSON objects.", parameterName);
            var (normalized, byteCount) = WriteWithByteCount(writer => document.RootElement.WriteTo(writer));
            if (byteCount > maximumBytes)
                throw new ArgumentException($"Extension payloads are at most {maximumBytes} UTF-8 bytes.", parameterName);
            return normalized;
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The extension payload is not valid JSON.", parameterName, error);
        }
    }

    private static MessageEvent ReadMessage(JsonElement root)
    {
        RequireProperties(root, "v", "k", "id", "dir", "from", "body", "ct", "att", "ext", "del");
        var dirEl = root.GetProperty("dir"u8);
        if (dirEl.ValueKind != JsonValueKind.String)
            throw new FormatException("A string is required.");
        MessageDirection direction;
        if (dirEl.ValueEquals("in"u8))
            direction = MessageDirection.Incoming;
        else if (dirEl.ValueEquals("out"u8))
            direction = MessageDirection.Outgoing;
        else
            throw new FormatException("Unknown message direction.");

        var ctEl = root.GetProperty("ct"u8);
        if (ctEl.ValueKind != JsonValueKind.String)
            throw new FormatException("A string is required.");
        MessageContentType contentType;
        if (ctEl.ValueEquals("text"u8))
            contentType = MessageContentType.PlainText;
        else if (ctEl.ValueEquals("markdown"u8))
            contentType = MessageContentType.Markdown;
        else if (ctEl.ValueEquals("html"u8))
            contentType = MessageContentType.Html;
        else
            throw new FormatException("Unknown content type.");

        return new MessageEvent(new MessageId(String(root.GetProperty("id"u8))), direction, ReadParty(root.GetProperty("from"u8)), String(root.GetProperty("body"u8)),
            contentType, ReadAttachments(root.GetProperty("att"u8)), OptionalString(root.GetProperty("ext"u8)), ReadDelivery(root.GetProperty("del"u8)));
    }

    private static ReadOnlySpan<byte> ContentTypeUtf8(MessageContentType contentType) => contentType switch
    {
        MessageContentType.PlainText => "text"u8,
        MessageContentType.Markdown => "markdown"u8,
        MessageContentType.Html => "html"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
    };

    private static void WriteParty(Utf8JsonWriter writer, ConversationParty party)
    {
        writer.WriteStartObject();
        switch (party)
        {
            case ContactParty contact:
                writer.WriteString("p"u8, "contact"u8);
                writer.WriteString("id"u8, contact.Contact.Value);
                break;
            case PrincipalParty principal:
                writer.WriteString("p"u8, "principal"u8);
                writer.WriteNumber("ak"u8, (int)principal.Actor.Kind);
                writer.WriteString("id"u8, principal.Actor.Id);
                break;
        }
        writer.WriteEndObject();
    }

    private static ConversationParty ReadParty(JsonElement element)
    {
        var p = element.GetProperty("p"u8);
        if (p.ValueKind != JsonValueKind.String)
            throw new FormatException("A string is required.");
        if (p.ValueEquals("contact"u8))
        {
            RequireProperties(element, "p", "id");
            return new ContactParty(new ContactId(String(element.GetProperty("id"u8))));
        }
        if (p.ValueEquals("principal"u8))
        {
            RequireProperties(element, "p", "ak", "id");
            return new PrincipalParty(new AccessActor((AccessActorKind)element.GetProperty("ak"u8).GetInt32(), String(element.GetProperty("id"u8))));
        }
        throw new FormatException("Unknown party discriminator.");
    }

    private static void WriteInstant(Utf8JsonWriter writer, ReadOnlySpan<byte> propertyName, DateTimeOffset value)
    {
        Span<char> buffer = stackalloc char[33];
        value.TryFormat(buffer, out var written, "O", CultureInfo.InvariantCulture);
        writer.WriteString(propertyName, buffer[..written]);
    }

    private static void WriteDelivery(Utf8JsonWriter writer, MessageDelivery delivery)
    {
        writer.WriteStartObject();
        writer.WriteNumber("s"u8, (int)delivery.State);
        WriteInstant(writer, "at"u8, delivery.UpdatedAt);
        writer.WriteString("fc"u8, delivery.FailureCode);
        writer.WriteEndObject();
    }

    private static MessageDelivery ReadDelivery(JsonElement element)
    {
        RequireProperties(element, "s", "at", "fc");
        var state = (MessageDeliveryState)element.GetProperty("s"u8).GetInt32();
        if (!Enum.IsDefined(state))
            throw new FormatException("Unknown delivery state.");
        return new MessageDelivery(state, Instant(element.GetProperty("at"u8)), OptionalString(element.GetProperty("fc"u8)));
    }

    private static void WriteAttachments(Utf8JsonWriter writer, IReadOnlyList<AttachmentRef> attachments)
    {
        writer.WriteStartArray("att"u8);
        for (var i = 0; i < attachments.Count; i++)
        {
            var attachment = attachments[i];
            writer.WriteStartObject();
            writer.WriteString("ref"u8, attachment.StorageRef);
            writer.WriteString("name"u8, attachment.FileName);
            writer.WriteString("type"u8, attachment.MediaType);
            writer.WriteNumber("size"u8, attachment.SizeBytes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static IReadOnlyList<AttachmentRef> ReadAttachments(JsonElement element)
    {
        var count = element.GetArrayLength();
        if (count == 0)
            return [];
        var list = new AttachmentRef[count];
        var i = 0;
        foreach (var item in element.EnumerateArray())
        {
            RequireProperties(item, "ref", "name", "type", "size");
            list[i++] = new AttachmentRef(String(item.GetProperty("ref"u8)), String(item.GetProperty("name"u8)),
                String(item.GetProperty("type"u8)), item.GetProperty("size"u8).GetInt64());
        }
        return list;
    }

    private static IReadOnlyList<LabelId> ReadLabelIds(JsonElement element)
    {
        var count = element.GetArrayLength();
        if (count == 0)
            return [];
        var list = new LabelId[count];
        var i = 0;
        foreach (var item in element.EnumerateArray())
            list[i++] = new LabelId(String(item));
        return list;
    }

    private static void WriteStatus(Utf8JsonWriter writer, ConversationStatus status)
    {
        writer.WriteStartObject();
        switch (status)
        {
            case OpenStatus:
                writer.WriteString("s"u8, "open"u8);
                break;
            case PendingStatus:
                writer.WriteString("s"u8, "pending"u8);
                break;
            case SnoozedStatus snoozed:
                writer.WriteString("s"u8, "snoozed"u8);
                WriteInstant(writer, "until"u8, snoozed.Until);
                break;
            case ResolvedStatus:
                writer.WriteString("s"u8, "resolved"u8);
                break;
            case ClosedStatus:
                writer.WriteString("s"u8, "closed"u8);
                break;
        }
        writer.WriteEndObject();
    }

    private static ConversationStatus ReadStatus(JsonElement element)
    {
        var s = element.GetProperty("s"u8);
        if (s.ValueKind != JsonValueKind.String)
            throw new FormatException("A string is required.");
        if (s.ValueEquals("open"u8))
        {
            RequireProperties(element, "s");
            return ConversationStatus.Open;
        }
        if (s.ValueEquals("pending"u8))
        {
            RequireProperties(element, "s");
            return ConversationStatus.Pending;
        }
        if (s.ValueEquals("snoozed"u8))
        {
            RequireProperties(element, "s", "until");
            return ConversationStatus.Snoozed(Instant(element.GetProperty("until"u8)));
        }
        if (s.ValueEquals("resolved"u8))
        {
            RequireProperties(element, "s");
            return ConversationStatus.Resolved;
        }
        if (s.ValueEquals("closed"u8))
        {
            RequireProperties(element, "s");
            return ConversationStatus.Closed;
        }
        throw new FormatException("Unknown status discriminator.");
    }

    private static ConversationPriority Priority(JsonElement element)
    {
        var priority = (ConversationPriority)element.GetInt32();
        return Enum.IsDefined(priority) ? priority : throw new FormatException("Unknown priority.");
    }

    private static UserId? OptionalUser(JsonElement element) => OptionalString(element) is { } value ? new UserId(value) : null;

    private static TeamId? OptionalTeam(JsonElement element) => OptionalString(element) is { } value ? new TeamId(value) : null;

    private static string Write(Action<Utf8JsonWriter> write) => WriteWithByteCount(write).Json;

    private static (string Json, int ByteCount) WriteWithByteCount(Action<Utf8JsonWriter> write)
    {
        var buffer = sharedBuffer;
        if (buffer is null)
            sharedBuffer = buffer = new ArrayBufferWriter<byte>(512);
        else
            buffer.Clear();

        var writer = sharedWriter;
        if (writer is null)
            sharedWriter = writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);

        try
        {
            write(writer);
            writer.Flush();
            var span = buffer.WrittenSpan;
            return (Encoding.UTF8.GetString(span), span.Length);
        }
        finally
        {
            if (buffer.Capacity > MaxPooledBufferCapacity)
            {
                sharedBuffer = null;
                sharedWriter = null;
                writer.Dispose();
            }
            else
            {
                buffer.Clear();
                writer.Reset(buffer);
            }
        }
    }

    private static T Read<T>(string json, Func<JsonElement, T> read)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumEncodedBytes / 3 && Encoding.UTF8.GetByteCount(json) > MaximumEncodedBytes)
            throw new FormatException("The encoded conversation payload is too large.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return read(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new FormatException("The encoded conversation payload is invalid.", error);
        }
    }

    private static void RequireVersion(JsonElement root)
    {
        if (root.GetProperty("v"u8).GetInt32() != FormatVersion)
            throw new FormatException("Unsupported conversation payload version.");
    }

    private static void RequireProperties(JsonElement element, params ReadOnlySpan<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("A JSON object is required.");
        uint seen = 0;
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            var matched = -1;
            for (var i = 0; i < names.Length; i++)
            {
                if (property.NameEquals(names[i]))
                {
                    matched = i;
                    break;
                }
            }
            if (matched < 0)
                throw new FormatException("Unknown property in conversation payload.");
            var bit = 1u << matched;
            if ((seen & bit) != 0)
                throw new FormatException("Missing or duplicate property in conversation payload.");
            seen |= bit;
            count++;
        }
        if (count != names.Length)
            throw new FormatException("Missing or duplicate property in conversation payload.");
    }

    private static string String(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException("A string is required.");

    private static string? OptionalString(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : String(element);

    private static DateTimeOffset Instant(JsonElement element) =>
        DateTimeOffset.ParseExact(String(element), "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static void WriteAttributes(Utf8JsonWriter writer, IReadOnlyDictionary<string, ConversationAttributeValue> attributes)
    {
        writer.WriteStartObject();
        if (attributes.Count > 0)
        {
            var keys = new string[attributes.Count];
            var index = 0;
            foreach (var key in attributes.Keys)
                keys[index++] = key;
            if (keys.Length > 1)
                Array.Sort(keys, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, attributes[key]);
            }
        }
        writer.WriteEndObject();
    }

    private static IReadOnlyDictionary<string, ConversationAttributeValue> ReadAttributes(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("An attribute object is required.");
        Dictionary<string, ConversationAttributeValue>? attributes = null;
        foreach (var property in element.EnumerateObject())
        {
            attributes ??= new Dictionary<string, ConversationAttributeValue>(StringComparer.Ordinal);
            if (!ConversationAttributeDefinition.IsValidKey(property.Name) || !attributes.TryAdd(property.Name, ReadValue(property.Value)))
                throw new FormatException("Invalid or duplicate attribute key.");
            if (attributes.Count > Conversation.MaximumAttributes)
                throw new FormatException("Too many attributes.");
        }
        return attributes is null ? EmptyAttributes : attributes;
    }

    private static void WriteOptionalValue(Utf8JsonWriter writer, ConversationAttributeValue? value)
    {
        if (value is { } present)
            WriteValue(writer, present);
        else
            writer.WriteNullValue();
    }

    private static ConversationAttributeValue? ReadOptionalValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? (ConversationAttributeValue?)null : ReadValue(element);

    private static void WriteValue(Utf8JsonWriter writer, ConversationAttributeValue value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case ConversationTextValue text:
                writer.WriteString("t"u8, "text"u8);
                writer.WriteString("v"u8, text.Value);
                break;
            case ConversationNumberValue number:
                writer.WriteString("t"u8, "number"u8);
                Span<char> numBuf = stackalloc char[32];
                if (number.Value.TryFormat(numBuf, out var numLen, default, CultureInfo.InvariantCulture))
                    writer.WriteString("v"u8, numBuf[..numLen]);
                else
                    writer.WriteString("v"u8, number.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case ConversationBooleanValue boolean:
                writer.WriteString("t"u8, "bool"u8);
                writer.WriteBoolean("v"u8, boolean.Value);
                break;
            case ConversationDateValue date:
                writer.WriteString("t"u8, "date"u8);
                Span<char> dateBuf = stackalloc char[10];
                date.Value.TryFormat(dateBuf, out var dateLen, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                writer.WriteString("v"u8, dateBuf[..dateLen]);
                break;
            case ConversationListValue list:
                writer.WriteString("t"u8, "list"u8);
                writer.WriteStartArray("v"u8);
                foreach (var item in list.Items)
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
                break;
        }
        writer.WriteEndObject();
    }

    private static ConversationAttributeValue ReadValue(JsonElement element)
    {
        RequireProperties(element, "t", "v");
        var type = element.GetProperty("t"u8);
        if (type.ValueKind != JsonValueKind.String)
            throw new FormatException("A string is required.");
        var value = element.GetProperty("v"u8);
        if (type.ValueEquals("text"u8))
            return new ConversationTextValue(String(value));
        if (type.ValueEquals("number"u8))
            return new ConversationNumberValue(decimal.Parse(String(value), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
        if (type.ValueEquals("bool"u8))
            return new ConversationBooleanValue(value.GetBoolean());
        if (type.ValueEquals("date"u8))
            return new ConversationDateValue(DateOnly.ParseExact(String(value), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (type.ValueEquals("list"u8))
        {
            var items = new List<string>();
            foreach (var item in value.EnumerateArray())
                items.Add(String(item));
            return new ConversationListValue(items);
        }
        throw new FormatException("Unknown attribute value discriminator.");
    }
}
