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

    public static string SerializeAttributes(IReadOnlyDictionary<string, ConversationAttributeValue> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WritePropertyName("a");
            WriteAttributes(writer, attributes);
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, oversized or of an unknown version.</exception>
    public static IReadOnlyDictionary<string, ConversationAttributeValue> DeserializeAttributes(string json) =>
        Read(json, root =>
        {
            RequireProperties(root, "v", "a");
            RequireVersion(root);
            return ReadAttributes(root.GetProperty("a"));
        });

    /// <summary>The string-valued attribute map used by Access conditions (<see cref="ConversationAttributeValue.ConditionValue"/>); not versioned.</summary>
    public static string SerializeConditionAttributes(IReadOnlyDictionary<string, ConversationAttributeValue> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return Write(writer =>
        {
            writer.WriteStartObject();
            foreach (var key in attributes.Keys.Order(StringComparer.Ordinal))
                if (attributes[key].ConditionValue is { } value)
                    writer.WriteString(key, value);
            writer.WriteEndObject();
        });
    }

    public static string SerializeRedirects(IReadOnlyList<ContactRedirect> redirects)
    {
        ArgumentNullException.ThrowIfNull(redirects);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteStartArray("r");
            foreach (var redirect in redirects)
            {
                writer.WriteStartObject();
                writer.WriteString("m", redirect.Merge);
                writer.WriteString("p", redirect.Previous.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static IReadOnlyList<ContactRedirect> DeserializeRedirects(string json) =>
        Read(json, root =>
        {
            RequireProperties(root, "v", "r");
            RequireVersion(root);
            return (IReadOnlyList<ContactRedirect>)[.. root.GetProperty("r").EnumerateArray().Select(item =>
            {
                RequireProperties(item, "m", "p");
                return new ContactRedirect(item.GetProperty("m").GetGuid(), new ContactId(String(item.GetProperty("p"))));
            })];
        });

    /// <summary>Encodes an event payload (the timeline envelope's sequence, identifier, time and actor are stored by the caller).</summary>
    public static string SerializeEvent(ConversationEvent @event)
    {
        if (@event.Value is null)
            throw new ArgumentException("An event is required.", nameof(@event));
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            switch (@event)
            {
                case MessageEvent message:
                    writer.WriteString("k", "message");
                    writer.WriteString("id", message.Id.Value);
                    writer.WriteString("dir", message.Direction switch
                    {
                        MessageDirection.Incoming => "in",
                        MessageDirection.Outgoing => "out",
                        _ => throw new ArgumentOutOfRangeException(nameof(@event)),
                    });
                    writer.WritePropertyName("from");
                    WriteParty(writer, message.Sender);
                    writer.WriteString("body", message.Body);
                    writer.WriteString("ct", ContentType(message.ContentType));
                    WriteAttachments(writer, message.Attachments);
                    writer.WriteString("ext", message.ExternalId);
                    writer.WritePropertyName("del");
                    WriteDelivery(writer, message.Delivery);
                    break;
                case NoteEvent note:
                    writer.WriteString("k", "note");
                    writer.WriteString("id", note.Id.Value);
                    writer.WriteString("body", note.Body);
                    WriteAttachments(writer, note.Attachments);
                    break;
                case AssignmentChanged assignment:
                    writer.WriteString("k", "assignment");
                    writer.WriteString("prev", assignment.Previous?.Value);
                    writer.WriteString("cur", assignment.Current?.Value);
                    break;
                case TeamChanged team:
                    writer.WriteString("k", "team");
                    writer.WriteString("prev", team.Previous?.Value);
                    writer.WriteString("cur", team.Current?.Value);
                    break;
                case StatusChanged status:
                    writer.WriteString("k", "status");
                    writer.WritePropertyName("prev");
                    WriteStatus(writer, status.Previous);
                    writer.WritePropertyName("cur");
                    WriteStatus(writer, status.Current);
                    break;
                case PriorityChanged priority:
                    writer.WriteString("k", "priority");
                    writer.WriteNumber("prev", (int)priority.Previous);
                    writer.WriteNumber("cur", (int)priority.Current);
                    break;
                case LabelsChanged labels:
                    writer.WriteString("k", "labels");
                    WriteStrings(writer, "add", labels.Added.Select(label => label.Value));
                    WriteStrings(writer, "rem", labels.Removed.Select(label => label.Value));
                    break;
                case AttributeChanged attribute:
                    writer.WriteString("k", "attribute");
                    writer.WriteString("key", attribute.Key);
                    writer.WritePropertyName("prev");
                    WriteOptionalValue(writer, attribute.Previous);
                    writer.WritePropertyName("cur");
                    WriteOptionalValue(writer, attribute.Current);
                    break;
                case ParticipantChanged participant:
                    writer.WriteString("k", "participant");
                    writer.WriteString("user", participant.Participant.Value);
                    writer.WriteString("change", participant.Change switch
                    {
                        ParticipantChange.Added => "added",
                        ParticipantChange.Removed => "removed",
                        _ => throw new ArgumentOutOfRangeException(nameof(@event)),
                    });
                    break;
                case ContactChanged contact:
                    writer.WriteString("k", "contact");
                    writer.WriteString("prev", contact.Previous.Value);
                    writer.WriteString("cur", contact.Current.Value);
                    if (contact.Merge is { } merge)
                        writer.WriteString("merge", merge);
                    else
                        writer.WriteNull("merge");
                    break;
                case SystemEvent system:
                    writer.WriteString("k", "system");
                    writer.WriteString("code", system.Code);
                    writer.WriteString("detail", system.Detail);
                    break;
                case ExtensionEvent extension:
                    writer.WriteString("k", "extension");
                    writer.WriteString("kind", extension.Kind);
                    writer.WriteNumber("pv", extension.PayloadVersion);
                    writer.WritePropertyName("p");
                    writer.WriteRawValue(extension.Payload, skipInputValidation: false);
                    break;
            }
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, of an unknown version or discriminator, or fails validation.</exception>
    public static ConversationEvent DeserializeEvent(string json) =>
        Read(json, root =>
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("k", out var kind))
                throw new FormatException("An event discriminator is required.");
            ConversationEvent result = String(kind) switch
            {
                "message" => ReadMessage(root),
                "note" => Checked(root, ["v", "k", "id", "body", "att"], () => new NoteEvent(new MessageId(String(root.GetProperty("id"))),
                    String(root.GetProperty("body")), ReadAttachments(root.GetProperty("att")))),
                "assignment" => Checked(root, ["v", "k", "prev", "cur"], () => new AssignmentChanged(OptionalUser(root.GetProperty("prev")), OptionalUser(root.GetProperty("cur")))),
                "team" => Checked(root, ["v", "k", "prev", "cur"], () => new TeamChanged(OptionalTeam(root.GetProperty("prev")), OptionalTeam(root.GetProperty("cur")))),
                "status" => Checked(root, ["v", "k", "prev", "cur"], () => new StatusChanged(ReadStatus(root.GetProperty("prev")), ReadStatus(root.GetProperty("cur")))),
                "priority" => Checked(root, ["v", "k", "prev", "cur"], () => new PriorityChanged(Priority(root.GetProperty("prev")), Priority(root.GetProperty("cur")))),
                "labels" => Checked(root, ["v", "k", "add", "rem"], () => new LabelsChanged(
                    root.GetProperty("add").EnumerateArray().Select(label => new LabelId(String(label))).ToList(),
                    root.GetProperty("rem").EnumerateArray().Select(label => new LabelId(String(label))).ToList())),
                "attribute" => Checked(root, ["v", "k", "key", "prev", "cur"], () => new AttributeChanged(String(root.GetProperty("key")),
                    ReadOptionalValue(root.GetProperty("prev")), ReadOptionalValue(root.GetProperty("cur")))),
                "participant" => Checked(root, ["v", "k", "user", "change"], () => new ParticipantChanged(new UserId(String(root.GetProperty("user"))),
                    String(root.GetProperty("change")) switch
                    {
                        "added" => ParticipantChange.Added,
                        "removed" => ParticipantChange.Removed,
                        _ => throw new FormatException("Unknown participant change."),
                    })),
                "contact" => Checked(root, ["v", "k", "prev", "cur", "merge"], () => new ContactChanged(new ContactId(String(root.GetProperty("prev"))),
                    new ContactId(String(root.GetProperty("cur"))), root.GetProperty("merge").ValueKind == JsonValueKind.Null ? null : root.GetProperty("merge").GetGuid())),
                "system" => Checked(root, ["v", "k", "code", "detail"], () => new SystemEvent(String(root.GetProperty("code")), OptionalString(root.GetProperty("detail")))),
                "extension" => Checked(root, ["v", "k", "kind", "pv", "p"], () => new ExtensionEvent(String(root.GetProperty("kind")), root.GetProperty("pv").GetInt32(),
                    root.GetProperty("p").GetRawText())),
                _ => throw new FormatException("Unknown event discriminator."),
            };
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
            writer.WriteNumber("v", FormatVersion);
            writer.WriteStartArray("e");
            foreach (var summary in change.Events)
            {
                writer.WriteStartObject();
                writer.WriteNumber("q", summary.Sequence);
                writer.WriteNumber("k", (int)summary.Kind);
                writer.WriteString("m", summary.Message?.Value);
                if (summary.Direction is { } direction)
                    writer.WriteNumber("d", (int)direction);
                else
                    writer.WriteNull("d");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("m", change.Message?.Value);
            if (change.Delivery is { } delivery)
                writer.WriteNumber("ds", (int)delivery);
            else
                writer.WriteNull("ds");
            writer.WriteEndObject();
        });
    }

    public static ConversationChange DeserializeChange(string payload, Guid id, TicketScope scope, ConversationId conversation, InboxId inbox, long revision,
        ConversationChangeKind kind, DateTimeOffset occurredAt) =>
        Read(payload, root =>
        {
            RequireProperties(root, "v", "e", "m", "ds");
            RequireVersion(root);
            var events = root.GetProperty("e").EnumerateArray().Select(item =>
            {
                RequireProperties(item, "q", "k", "m", "d");
                var message = OptionalString(item.GetProperty("m"));
                return new ConversationEventSummary(item.GetProperty("q").GetInt64(), (ConversationEventKind)item.GetProperty("k").GetInt32(),
                    message is null ? null : new MessageId(message),
                    item.GetProperty("d").ValueKind == JsonValueKind.Null ? null : (MessageDirection)item.GetProperty("d").GetInt32());
            }).ToList();
            var messageId = OptionalString(root.GetProperty("m"));
            var state = root.GetProperty("ds");
            return new ConversationChange(id, scope, conversation, inbox, revision, kind, events, messageId is null ? null : new MessageId(messageId),
                state.ValueKind == JsonValueKind.Null ? null : (MessageDeliveryState)state.GetInt32(), occurredAt);
        });

    /// <summary>Validates and compacts an extension payload: a JSON object of depth ≤ 16 and at most <paramref name="maximumBytes"/> UTF-8 bytes.</summary>
    internal static string NormalizeExtensionPayload(string payload, int maximumBytes, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(payload, parameterName);
        if (payload.Length > maximumBytes * 2 || !System.Text.Unicode.Utf16.IsValid(payload))
            throw new ArgumentException("The extension payload is too large or malformed.", parameterName);
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Extension payloads are JSON objects.", parameterName);
            var normalized = Write(writer => document.RootElement.WriteTo(writer));
            if (Encoding.UTF8.GetByteCount(normalized) > maximumBytes)
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
        var direction = String(root.GetProperty("dir")) switch
        {
            "in" => MessageDirection.Incoming,
            "out" => MessageDirection.Outgoing,
            _ => throw new FormatException("Unknown message direction."),
        };
        var contentType = String(root.GetProperty("ct")) switch
        {
            "text" => MessageContentType.PlainText,
            "markdown" => MessageContentType.Markdown,
            "html" => MessageContentType.Html,
            _ => throw new FormatException("Unknown content type."),
        };
        return new MessageEvent(new MessageId(String(root.GetProperty("id"))), direction, ReadParty(root.GetProperty("from")), String(root.GetProperty("body")),
            contentType, ReadAttachments(root.GetProperty("att")), OptionalString(root.GetProperty("ext")), ReadDelivery(root.GetProperty("del")));
    }

    private static T Checked<T>(JsonElement root, string[] properties, Func<T> read)
    {
        RequireProperties(root, properties);
        return read();
    }

    private static string ContentType(MessageContentType contentType) => contentType switch
    {
        MessageContentType.PlainText => "text",
        MessageContentType.Markdown => "markdown",
        MessageContentType.Html => "html",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
    };

    private static void WriteParty(Utf8JsonWriter writer, ConversationParty party)
    {
        writer.WriteStartObject();
        switch (party)
        {
            case ContactParty contact:
                writer.WriteString("p", "contact");
                writer.WriteString("id", contact.Contact.Value);
                break;
            case PrincipalParty principal:
                writer.WriteString("p", "principal");
                writer.WriteNumber("ak", (int)principal.Actor.Kind);
                writer.WriteString("id", principal.Actor.Id);
                break;
        }
        writer.WriteEndObject();
    }

    private static ConversationParty ReadParty(JsonElement element) =>
        String(element.GetProperty("p")) switch
        {
            "contact" => Checked(element, ["p", "id"], () => new ContactParty(new ContactId(String(element.GetProperty("id"))))),
            "principal" => Checked(element, ["p", "ak", "id"], () => new PrincipalParty(new AccessActor((AccessActorKind)element.GetProperty("ak").GetInt32(),
                String(element.GetProperty("id"))))),
            _ => throw new FormatException("Unknown party discriminator."),
        };

    private static void WriteDelivery(Utf8JsonWriter writer, MessageDelivery delivery)
    {
        writer.WriteStartObject();
        writer.WriteNumber("s", (int)delivery.State);
        writer.WriteString("at", delivery.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("fc", delivery.FailureCode);
        writer.WriteEndObject();
    }

    private static MessageDelivery ReadDelivery(JsonElement element)
    {
        RequireProperties(element, "s", "at", "fc");
        var state = (MessageDeliveryState)element.GetProperty("s").GetInt32();
        if (!Enum.IsDefined(state))
            throw new FormatException("Unknown delivery state.");
        return new MessageDelivery(state, Instant(element.GetProperty("at")), OptionalString(element.GetProperty("fc")));
    }

    private static void WriteAttachments(Utf8JsonWriter writer, IReadOnlyList<AttachmentRef> attachments)
    {
        writer.WriteStartArray("att");
        foreach (var attachment in attachments)
        {
            writer.WriteStartObject();
            writer.WriteString("ref", attachment.StorageRef);
            writer.WriteString("name", attachment.FileName);
            writer.WriteString("type", attachment.MediaType);
            writer.WriteNumber("size", attachment.SizeBytes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static List<AttachmentRef> ReadAttachments(JsonElement element) =>
        [.. element.EnumerateArray().Select(item => Checked(item, ["ref", "name", "type", "size"], () => new AttachmentRef(String(item.GetProperty("ref")),
            String(item.GetProperty("name")), String(item.GetProperty("type")), item.GetProperty("size").GetInt64())))];

    private static void WriteStatus(Utf8JsonWriter writer, ConversationStatus status)
    {
        writer.WriteStartObject();
        switch (status)
        {
            case OpenStatus:
                writer.WriteString("s", "open");
                break;
            case PendingStatus:
                writer.WriteString("s", "pending");
                break;
            case SnoozedStatus snoozed:
                writer.WriteString("s", "snoozed");
                writer.WriteString("until", snoozed.Until.ToString("O", CultureInfo.InvariantCulture));
                break;
            case ResolvedStatus:
                writer.WriteString("s", "resolved");
                break;
            case ClosedStatus:
                writer.WriteString("s", "closed");
                break;
        }
        writer.WriteEndObject();
    }

    private static ConversationStatus ReadStatus(JsonElement element) =>
        String(element.GetProperty("s")) switch
        {
            "open" => Checked(element, ["s"], () => ConversationStatus.Open),
            "pending" => Checked(element, ["s"], () => ConversationStatus.Pending),
            "snoozed" => Checked(element, ["s", "until"], () => ConversationStatus.Snoozed(Instant(element.GetProperty("until")))),
            "resolved" => Checked(element, ["s"], () => ConversationStatus.Resolved),
            "closed" => Checked(element, ["s"], () => ConversationStatus.Closed),
            _ => throw new FormatException("Unknown status discriminator."),
        };

    private static ConversationPriority Priority(JsonElement element)
    {
        var priority = (ConversationPriority)element.GetInt32();
        return Enum.IsDefined(priority) ? priority : throw new FormatException("Unknown priority.");
    }

    private static UserId? OptionalUser(JsonElement element) => OptionalString(element) is { } value ? new UserId(value) : null;

    private static TeamId? OptionalTeam(JsonElement element) => OptionalString(element) is { } value ? new TeamId(value) : null;

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static T Read<T>(string json, Func<JsonElement, T> read)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumEncodedBytes)
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
        if (root.GetProperty("v").GetInt32() != FormatVersion)
            throw new FormatException("Unsupported conversation payload version.");
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("A JSON object is required.");
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException("Unknown property in conversation payload.");
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
        foreach (var key in attributes.Keys.Order(StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            WriteValue(writer, attributes[key]);
        }
        writer.WriteEndObject();
    }

    private static Dictionary<string, ConversationAttributeValue> ReadAttributes(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("An attribute object is required.");
        var attributes = new Dictionary<string, ConversationAttributeValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!ConversationAttributeDefinition.IsValidKey(property.Name) || !attributes.TryAdd(property.Name, ReadValue(property.Value)))
                throw new FormatException("Invalid or duplicate attribute key.");
            if (attributes.Count > Conversation.MaximumAttributes)
                throw new FormatException("Too many attributes.");
        }
        return attributes;
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
                writer.WriteString("t", "text");
                writer.WriteString("v", text.Value);
                break;
            case ConversationNumberValue number:
                writer.WriteString("t", "number");
                writer.WriteString("v", number.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case ConversationBooleanValue boolean:
                writer.WriteString("t", "bool");
                writer.WriteBoolean("v", boolean.Value);
                break;
            case ConversationDateValue date:
                writer.WriteString("t", "date");
                writer.WriteString("v", date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case ConversationListValue list:
                writer.WriteString("t", "list");
                writer.WriteStartArray("v");
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
        var value = element.GetProperty("v");
        return String(element.GetProperty("t")) switch
        {
            "text" => new ConversationTextValue(String(value)),
            "number" => new ConversationNumberValue(decimal.Parse(String(value), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)),
            "bool" => new ConversationBooleanValue(value.GetBoolean()),
            "date" => new ConversationDateValue(DateOnly.ParseExact(String(value), "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            "list" => new ConversationListValue(value.EnumerateArray().Select(String).ToList()),
            _ => throw new FormatException("Unknown attribute value discriminator."),
        };
    }
}
