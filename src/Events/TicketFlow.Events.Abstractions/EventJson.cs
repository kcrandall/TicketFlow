using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>Writes an event payload at a privacy level: identifiers always, metadata only when requested.</summary>
internal readonly struct EventDataWriter(Utf8JsonWriter writer, bool metadata)
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
        writer.WriteString("kind"u8, actor.Kind);
        writer.WriteString("id"u8, actor.Id);
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
            EventJson.WriteInstant(writer, name, instant);
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
        for (var i = 0; i < labels.Count; i++)
            writer.WriteStringValue(labels[i].Value);
        writer.WriteEndArray();
    }
}

/// <summary>Reads one payload object strictly: every property must be read exactly once and known; <see cref="Done"/> enforces it.</summary>
internal struct EventDataReader(JsonElement root)
{
    private int matchedPresent;

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
        var count = array.GetArrayLength();
        if (count == 0)
            return [];
        var labels = new LabelId[count];
        var index = 0;
        foreach (var item in array.EnumerateArray())
            labels[index++] = new LabelId(EventJson.String(item));
        return labels;
    }

    public EventActor Actor(string name)
    {
        var value = Get(name);
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected an actor object.");
        var count = 0;
        foreach (var _ in value.EnumerateObject())
            count++;
        if (count != 2 || !value.TryGetProperty("kind"u8, out var kind) || !value.TryGetProperty("id"u8, out var id))
            throw new FormatException("Expected an actor object.");
        return new EventActor(EventJson.String(kind), EventJson.String(id));
    }

    public readonly void Done()
    {
        // Every known property is requested at most once by IntegrationEventCodec.Read, plus the required "v" property.
        // If any unknown or duplicate property exists in root, the total enumerated property count exceeds matchedPresent + 1.
        var total = 0;
        foreach (var _ in root.EnumerateObject())
            total++;
        if (total != matchedPresent + 1)
            throw new FormatException("Unknown or duplicate payload property.");
    }

    private JsonElement Get(string name) => TryGet(name, out var value) ? value : throw new FormatException("A required payload property is missing.");

    private bool TryGet(string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
        {
            if (value.ValueKind == JsonValueKind.Null)
                throw new FormatException("Payload properties are omitted, never null.");
            matchedPresent++;
            return true;
        }
        return false;
    }
}

internal static class EventJson
{
    public static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 16 };
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default, Indented = false };

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    public static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = sharedBuffer;
        var writer = sharedWriter;
        sharedBuffer = null;
        sharedWriter = null;
        if (buffer is null)
            buffer = new ArrayBufferWriter<byte>(512);
        else
            buffer.ResetWrittenCount();
        if (writer is null)
            writer = new Utf8JsonWriter(buffer, WriterOptions);
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
            if (buffer.Capacity <= 65536)
            {
                sharedBuffer = buffer;
                sharedWriter = writer;
            }
            else
                writer.Dispose();
        }
    }

    public static void WriteInstant(Utf8JsonWriter writer, string name, DateTimeOffset value)
    {
        Span<char> chars = stackalloc char[27];
        EventText.Instant(value).UtcDateTime.TryFormat(chars, out var written, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        writer.WriteString(name, chars[..written]);
    }

    public static void WriteInstant(Utf8JsonWriter writer, ReadOnlySpan<byte> name, DateTimeOffset value)
    {
        Span<char> chars = stackalloc char[27];
        EventText.Instant(value).UtcDateTime.TryFormat(chars, out var written, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        writer.WriteString(name, chars[..written]);
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
