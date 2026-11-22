using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>Writes an event payload at a privacy level: identifiers always, metadata only when requested.</summary>
internal sealed class EventDataWriter(Utf8JsonWriter writer, bool metadata)
{
    public Utf8JsonWriter Raw => writer;

    public void Id(string name, string value) => writer.WriteString(name, value);

    public void Id(string name, string? value, bool optional)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }

    public void Actor(string name, EventActor actor)
    {
        writer.WriteStartObject(name);
        writer.WriteString("kind", actor.Kind);
        writer.WriteString("id", actor.Id);
        writer.WriteEndObject();
    }

    public void Text(string name, string? value)
    {
        if (metadata && value is not null)
            writer.WriteString(name, value);
    }

    public void Number(string name, long value)
    {
        if (metadata)
            writer.WriteNumber(name, value);
    }

    public void Boolean(string name, bool value)
    {
        if (metadata)
            writer.WriteBoolean(name, value);
    }

    public void Instant(string name, DateTimeOffset? value)
    {
        if (metadata && value is { } instant)
            writer.WriteString(name, EventJson.FormatInstant(instant));
    }

    public void Duration(string name, TimeSpan? value)
    {
        if (metadata && value is { } duration)
            writer.WriteNumber(name, EventJson.Microseconds(duration));
    }

    public void Labels(string name, IReadOnlyList<LabelId> labels)
    {
        if (!metadata)
            return;
        writer.WriteStartArray(name);
        foreach (var label in labels)
            writer.WriteStringValue(label.Value);
        writer.WriteEndArray();
    }
}

/// <summary>Reads one payload object strictly: every property must be read exactly once and known; <see cref="Done"/> enforces it.</summary>
internal sealed class EventDataReader(JsonElement root)
{
    private readonly HashSet<string> known = new(StringComparer.Ordinal) { "v" };

    public string String(string name) => EventJson.String(Get(name));

    public string? OptionalString(string name) => TryGet(name, out var value) ? EventJson.String(value) : null;

    public long Long(string name) => EventJson.Long(Get(name));

    public int Int(string name) => checked((int)EventJson.Long(Get(name)));

    public bool Bool(string name) => Get(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException("Expected a boolean."),
    };

    public DateTimeOffset Instant(string name) => EventJson.ParseInstant(String(name));

    public DateTimeOffset? OptionalInstant(string name) => OptionalString(name) is { } text ? EventJson.ParseInstant(text) : null;

    public TimeSpan Duration(string name) => EventJson.FromMicroseconds(Long(name));

    public TimeSpan? OptionalDuration(string name) => TryGet(name, out var value) ? EventJson.FromMicroseconds(EventJson.Long(value)) : null;

    public IReadOnlyList<LabelId> Labels(string name)
    {
        var array = Get(name);
        if (array.ValueKind != JsonValueKind.Array)
            throw new FormatException("Expected an array.");
        return [.. array.EnumerateArray().Select(item => new LabelId(EventJson.String(item)))];
    }

    public EventActor Actor(string name)
    {
        var value = Get(name);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2)
            throw new FormatException("Expected an actor object.");
        return new EventActor(EventJson.String(value.GetProperty("kind")), EventJson.String(value.GetProperty("id")));
    }

    public void Done()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!known.Contains(property.Name) || !seen.Add(property.Name))
                throw new FormatException("Unknown or duplicate payload property.");
    }

    private JsonElement Get(string name) => TryGet(name, out var value) ? value : throw new FormatException("A required payload property is missing.");

    private bool TryGet(string name, out JsonElement value)
    {
        known.Add(name);
        if (root.TryGetProperty(name, out value))
        {
            if (value.ValueKind == JsonValueKind.Null)
                throw new FormatException("Payload properties are omitted, never null.");
            return true;
        }
        return false;
    }
}

internal static class EventJson
{
    public static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 16 };
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default, Indented = false };

    public static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string FormatInstant(DateTimeOffset value) =>
        EventText.Instant(value).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseInstant(string text)
    {
        if (text.Length != 27 || !DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new FormatException("Instants are UTC ISO-8601 with microseconds.");
        return value;
    }

    public static long Microseconds(TimeSpan value) => value.Ticks / 10;

    public static TimeSpan FromMicroseconds(long value) => TimeSpan.FromTicks(checked(value * 10));

    public static string String(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException("Expected a string.");

    public static long Long(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : throw new FormatException("Expected an integer.");
}
