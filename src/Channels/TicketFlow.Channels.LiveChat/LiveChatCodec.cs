using System.Buffers;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Channels.LiveChat;

/// <summary>
/// Versioned JSON shapes for durable live-chat stores: widget configurations (<c>{"v":1,...}</c>) and pre-chat answers
/// (<c>{"v":1,"answers":{...}}</c>). Decoding enforces the model bounds and rejects unknown versions and properties.
/// </summary>
public static class LiveChatCodec
{
    private const string EmptyPrechatJson = """{"v":1,"answers":{}}""";
    private static readonly FrozenDictionary<string, string> EmptyPrechat = FrozenDictionary<string, string>.Empty;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    private static string WritePooled(Action<Utf8JsonWriter> write)
    {
        var buffer = sharedBuffer;
        var writer = sharedWriter;
        sharedBuffer = null;
        sharedWriter = null;
        if (buffer is null)
            buffer = new ArrayBufferWriter<byte>(512);
        else
            buffer.Clear();
        if (writer is null)
            writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);
        try
        {
            write(writer);
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            buffer.Clear();
            if (buffer.Capacity <= 65536)
            {
                sharedBuffer = buffer;
                sharedWriter = writer;
            }
            else
                writer.Dispose();
        }
    }

    public static string EncodeWidget(LiveChatWidget widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return WritePooled(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, 1);
            writer.WriteStartArray("origins"u8);
            for (var i = 0; i < widget.AllowedOrigins.Count; i++)
                writer.WriteStringValue(widget.AllowedOrigins[i]);
            writer.WriteEndArray();
            if (widget.ThemeRef is not null)
                writer.WriteString("theme"u8, widget.ThemeRef);
            writer.WriteStartArray("prechat"u8);
            for (var i = 0; i < widget.PrechatFields.Count; i++)
            {
                var field = widget.PrechatFields[i];
                writer.WriteStartObject();
                writer.WriteString("key"u8, field.Key);
                writer.WriteString("label"u8, field.Label);
                writer.WriteNumber("kind"u8, (int)field.Kind);
                writer.WriteBoolean("required"u8, field.Required);
                writer.WriteNumber("max"u8, field.MaximumLength);
                writer.WriteBoolean("sensitive"u8, field.Sensitive);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (widget.GreetingMessage is not null)
                writer.WriteString("greeting"u8, widget.GreetingMessage);
            if (widget.AwayMessage is not null)
                writer.WriteString("away"u8, widget.AwayMessage);
            if (widget.Deflection is { } deflection)
            {
                writer.WriteStartObject("deflection"u8);
                writer.WriteString("portal"u8, deflection.Portal.Value);
                writer.WriteString("locale"u8, deflection.Locale.Value);
                writer.WriteNumber("max"u8, deflection.MaximumResults);
                writer.WriteEndObject();
            }
            if (widget.Attachments is { } attachments)
            {
                writer.WriteStartObject("attachments"u8);
                writer.WriteNumber("maxBytes"u8, attachments.MaximumBytes);
                writer.WriteStartArray("types"u8);
                for (var i = 0; i < attachments.AllowedMediaTypes.Count; i++)
                    writer.WriteStringValue(attachments.AllowedMediaTypes[i]);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteStartObject("identity"u8);
            writer.WriteString("namespace"u8, widget.Identity.Namespace);
            writer.WriteBoolean("hmac"u8, widget.Identity.AllowHmac);
            writer.WriteBoolean("token"u8, widget.Identity.AllowSignedToken);
            writer.WriteBoolean("required"u8, widget.Identity.RequireVerified);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    public static LiveChatWidget DecodeWidget(string configurationId, InboxId inbox, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = Object(document.RootElement, "v", "origins", "theme", "prechat", "greeting", "away", "deflection", "attachments", "identity");
            if (root.Required("v"u8).GetInt32() != 1)
                throw new FormatException("Unsupported widget version.");
            var originsEl = root.Required("origins"u8).RequireArray(LiveChatWidget.MaximumOrigins);
            var origins = new List<string>(originsEl.GetArrayLength());
            foreach (var item in originsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new FormatException("Unexpected array item.");
                origins.Add(item.GetString()!);
            }
            var prechatEl = root.Required("prechat"u8).RequireArray(LiveChatWidget.MaximumPrechatFields);
            var prechat = new List<LiveChatPrechatField>(prechatEl.GetArrayLength());
            foreach (var item in prechatEl.EnumerateArray())
            {
                var field = Object(item, "key", "label", "kind", "required", "max", "sensitive");
                prechat.Add(new LiveChatPrechatField(field.Required("key"u8).GetString()!, field.Required("label"u8).GetString()!,
                    (PrechatFieldKind)field.Required("kind"u8).GetInt32(), field.Required("required"u8).GetBoolean(),
                    field.Required("max"u8).GetInt32(), field.Required("sensitive"u8).GetBoolean()));
            }
            LiveChatDeflection? deflection = null;
            if (root["deflection"u8] is { } deflectionElement)
            {
                var value = Object(deflectionElement, "portal", "locale", "max");
                deflection = new LiveChatDeflection(new PortalId(value.Required("portal"u8).GetString()!), new HelpCenterLocale(value.Required("locale"u8).GetString()!),
                    value.Required("max"u8).GetInt32());
            }
            LiveChatAttachmentPolicy? attachments = null;
            if (root["attachments"u8] is { } attachmentsElement)
            {
                var value = Object(attachmentsElement, "maxBytes", "types");
                var typesEl = value.Required("types"u8).RequireArray(LiveChatAttachmentPolicy.MaximumMediaTypes);
                var types = new List<string>(typesEl.GetArrayLength());
                foreach (var item in typesEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new FormatException("Unexpected array item.");
                    types.Add(item.GetString()!);
                }
                attachments = new LiveChatAttachmentPolicy(value.Required("maxBytes"u8).GetInt64(), types);
            }
            var identity = Object(root.Required("identity"u8), "namespace", "hmac", "token", "required");
            return new LiveChatWidget(configurationId, inbox, origins, root.OptionalString("theme"u8), prechat, root.OptionalString("greeting"u8), root.OptionalString("away"u8), deflection, attachments,
                new LiveChatIdentityPolicy(identity.Required("namespace"u8).GetString()!, identity.Required("hmac"u8).GetBoolean(), identity.Required("token"u8).GetBoolean(),
                    identity.Required("required"u8).GetBoolean()));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new FormatException("The widget configuration is invalid.", error);
        }
    }

    public static string EncodePrechat(IReadOnlyDictionary<string, string> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count == 0)
            return EmptyPrechatJson;
        var keys = new string[answers.Count];
        var idx = 0;
        foreach (var key in answers.Keys)
            keys[idx++] = key;
        if (keys.Length > 1)
            Array.Sort(keys, StringComparer.Ordinal);
        return WritePooled(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, 1);
            writer.WriteStartObject("answers"u8);
            for (var i = 0; i < keys.Length; i++)
                writer.WriteString(keys[i], answers[keys[i]]);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    public static IReadOnlyDictionary<string, string> DecodePrechat(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.Equals(json, EmptyPrechatJson, StringComparison.Ordinal))
            return EmptyPrechat;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = Object(document.RootElement, "v", "answers");
            if (root.Required("v"u8).GetInt32() != 1)
                throw new FormatException("Unsupported pre-chat version.");
            var answersEl = root.Required("answers"u8);
            if (answersEl.ValueKind != JsonValueKind.Object)
                throw new FormatException("Invalid pre-chat answers.");
            Dictionary<string, string>? answers = null;
            var count = 0;
            foreach (var property in answersEl.EnumerateObject())
            {
                answers ??= new Dictionary<string, string>(StringComparer.Ordinal);
                if (++count > VisitorSession.MaximumPrechatAnswers || property.Value.ValueKind != JsonValueKind.String || !answers.TryAdd(property.Name, property.Value.GetString()!))
                    throw new FormatException("Invalid pre-chat answers.");
            }
            return answers ?? (IReadOnlyDictionary<string, string>)EmptyPrechat;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The pre-chat answers are invalid.", error);
        }
    }

    private static JsonElement Object(JsonElement element, params ReadOnlySpan<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected an object.");
        foreach (var property in element.EnumerateObject())
        {
            var matched = false;
            for (var i = 0; i < allowed.Length; i++)
            {
                if (property.NameEquals(allowed[i]))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
                throw new FormatException("Unknown property.");
        }
        return element;
    }
}

internal static class LiveChatJsonExtensions
{
    extension(JsonElement element)
    {
        public JsonElement? this[ReadOnlySpan<byte> name] => element.TryGetProperty(name, out var value) ? value : null;

        public JsonElement Required(ReadOnlySpan<byte> name) =>
            element[name] ?? throw new FormatException("Missing property.");

        public string? OptionalString(ReadOnlySpan<byte> name) =>
            element[name] is not { } value ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException("Expected a string.");

        public JsonElement RequireArray(int maximum) =>
            element.ValueKind == JsonValueKind.Array && element.GetArrayLength() <= maximum ? element : throw new FormatException("Expected a bounded array.");
    }
}
