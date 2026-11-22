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
    public static string EncodeWidget(LiveChatWidget widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartArray("origins");
            foreach (var origin in widget.AllowedOrigins)
                writer.WriteStringValue(origin);
            writer.WriteEndArray();
            if (widget.ThemeRef is not null)
                writer.WriteString("theme", widget.ThemeRef);
            writer.WriteStartArray("prechat");
            foreach (var field in widget.PrechatFields)
            {
                writer.WriteStartObject();
                writer.WriteString("key", field.Key);
                writer.WriteString("label", field.Label);
                writer.WriteNumber("kind", (int)field.Kind);
                writer.WriteBoolean("required", field.Required);
                writer.WriteNumber("max", field.MaximumLength);
                writer.WriteBoolean("sensitive", field.Sensitive);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (widget.GreetingMessage is not null)
                writer.WriteString("greeting", widget.GreetingMessage);
            if (widget.AwayMessage is not null)
                writer.WriteString("away", widget.AwayMessage);
            if (widget.Deflection is { } deflection)
            {
                writer.WriteStartObject("deflection");
                writer.WriteString("portal", deflection.Portal.Value);
                writer.WriteString("locale", deflection.Locale.Value);
                writer.WriteNumber("max", deflection.MaximumResults);
                writer.WriteEndObject();
            }
            if (widget.Attachments is { } attachments)
            {
                writer.WriteStartObject("attachments");
                writer.WriteNumber("maxBytes", attachments.MaximumBytes);
                writer.WriteStartArray("types");
                foreach (var type in attachments.AllowedMediaTypes)
                    writer.WriteStringValue(type);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteStartObject("identity");
            writer.WriteString("namespace", widget.Identity.Namespace);
            writer.WriteBoolean("hmac", widget.Identity.AllowHmac);
            writer.WriteBoolean("token", widget.Identity.AllowSignedToken);
            writer.WriteBoolean("required", widget.Identity.RequireVerified);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static LiveChatWidget DecodeWidget(string configurationId, InboxId inbox, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = Object(document.RootElement, "v", "origins", "theme", "prechat", "greeting", "away", "deflection", "attachments", "identity");
            if (Required(root, "v").GetInt32() != 1)
                throw new FormatException("Unsupported widget version.");
            var origins = Array(Required(root, "origins"), LiveChatWidget.MaximumOrigins).Select(item => item.GetString()!).ToList();
            var prechat = Array(Required(root, "prechat"), LiveChatWidget.MaximumPrechatFields).Select(item =>
            {
                var field = Object(item, "key", "label", "kind", "required", "max", "sensitive");
                return new LiveChatPrechatField(Required(field, "key").GetString()!, Required(field, "label").GetString()!, (PrechatFieldKind)Required(field, "kind").GetInt32(),
                    Required(field, "required").GetBoolean(), Required(field, "max").GetInt32(), Required(field, "sensitive").GetBoolean());
            }).ToList();
            LiveChatDeflection? deflection = null;
            if (root.TryGetProperty("deflection", out var deflectionElement))
            {
                var value = Object(deflectionElement, "portal", "locale", "max");
                deflection = new LiveChatDeflection(new PortalId(Required(value, "portal").GetString()!), new HelpCenterLocale(Required(value, "locale").GetString()!),
                    Required(value, "max").GetInt32());
            }
            LiveChatAttachmentPolicy? attachments = null;
            if (root.TryGetProperty("attachments", out var attachmentsElement))
            {
                var value = Object(attachmentsElement, "maxBytes", "types");
                attachments = new LiveChatAttachmentPolicy(Required(value, "maxBytes").GetInt64(),
                    Array(Required(value, "types"), LiveChatAttachmentPolicy.MaximumMediaTypes).Select(item => item.GetString()!));
            }
            var identity = Object(Required(root, "identity"), "namespace", "hmac", "token", "required");
            return new LiveChatWidget(configurationId, inbox, origins, Optional(root, "theme"), prechat, Optional(root, "greeting"), Optional(root, "away"), deflection, attachments,
                new LiveChatIdentityPolicy(Required(identity, "namespace").GetString()!, Required(identity, "hmac").GetBoolean(), Required(identity, "token").GetBoolean(),
                    Required(identity, "required").GetBoolean()));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new FormatException("The widget configuration is invalid.", error);
        }
    }

    public static string EncodePrechat(IReadOnlyDictionary<string, string> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartObject("answers");
            foreach (var (key, value) in answers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteString(key, value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static IReadOnlyDictionary<string, string> DecodePrechat(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = Object(document.RootElement, "v", "answers");
            if (Required(root, "v").GetInt32() != 1)
                throw new FormatException("Unsupported pre-chat version.");
            var answers = new Dictionary<string, string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var property in Required(root, "answers").EnumerateObject())
            {
                if (++count > VisitorSession.MaximumPrechatAnswers || property.Value.ValueKind != JsonValueKind.String || !answers.TryAdd(property.Name, property.Value.GetString()!))
                    throw new FormatException("Invalid pre-chat answers.");
            }
            return answers;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The pre-chat answers are invalid.", error);
        }
    }

    private static JsonElement Object(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected an object.");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException("Unknown property.");
        return element;
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new FormatException("Missing property.");

    private static string? Optional(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException("Expected a string.") : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximum)
            throw new FormatException("Expected a bounded array.");
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Object))
                throw new FormatException("Unexpected array item.");
            yield return item;
        }
    }
}
