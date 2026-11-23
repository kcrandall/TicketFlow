using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TicketFlow.Conversations;
using TicketFlow.Sla;

namespace TicketFlow.Automation;

/// <summary>Shared JSON reading/writing for the automation codecs: strict properties, explicit enum names and invariant instants.</summary>
internal static class AutomationJson
{
    public const int MaximumEncodedLength = 1024 * 1024;

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
            writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false });
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

    /// <exception cref="FormatException">The JSON is malformed, oversized or invalid.</exception>
    public static T Read<T>(string json, Func<JsonElement, T> read)
    {
        ArgumentNullException.ThrowIfNull(json);
        var span = json.AsSpan();
        if (span.Length > MaximumEncodedLength || (span.ContainsAnyInRange('\uD800', '\uDFFF') && !System.Text.Unicode.Utf16.IsValid(span)))
            throw new FormatException("The automation payload is oversized or malformed.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Automation payloads are JSON objects.");
            return read(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new FormatException("The automation payload is invalid.", error);
        }
    }

    /// <summary>Rejects properties outside <paramref name="allowed"/> and duplicates.</summary>
    public static void RequireProperties(JsonElement element, params ReadOnlySpan<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("A JSON object is required.");
        ulong seenMask = 0;
        foreach (var property in element.EnumerateObject())
        {
            var matched = -1;
            for (var i = 0; i < allowed.Length; i++)
            {
                if (property.NameEquals(allowed[i]))
                {
                    matched = i;
                    break;
                }
            }
            if (matched < 0)
                throw new FormatException("Unknown or duplicate property.");
            var bit = 1UL << matched;
            if ((seenMask & bit) != 0)
                throw new FormatException("Unknown or duplicate property.");
            seenMask |= bit;
        }
    }

    public static void RequireVersion(JsonElement element, int version)
    {
        if (Int(element.GetProperty("v")) != version)
            throw new FormatException("Unsupported automation payload version.");
    }

    public static string String(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException("A string is required.");

    public static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? String(value) : null;

    public static int Int(JsonElement element) => element.ValueKind == JsonValueKind.Number ? element.GetInt32() : throw new FormatException("A number is required.");

    public static long Long(JsonElement element) => element.ValueKind == JsonValueKind.Number ? element.GetInt64() : throw new FormatException("A number is required.");

    public static bool Bool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException("A boolean is required."),
    };

    public static JsonElement.ArrayEnumerator Array(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : throw new FormatException("An array is required.");

    public static IReadOnlyList<string> Strings(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new FormatException("An array is required.");
        var count = element.GetArrayLength();
        if (count == 0)
            return [];
        var result = new string[count];
        var index = 0;
        foreach (var item in element.EnumerateArray())
            result[index++] = String(item);
        return result;
    }

    public static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    public static void WriteInstant(Utf8JsonWriter writer, string name, DateTimeOffset value)
    {
        Span<char> chars = stackalloc char[32];
        value.UtcDateTime.TryFormat(chars, out var written, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        writer.WriteString(name, chars[..written]);
    }

    public static DateTimeOffset Instant(JsonElement element) =>
        DateTimeOffset.ParseExact(String(element), "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? OptionalInstant(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? Instant(value) : null;

    public static string Name(ConversationPriority priority) => priority switch
    {
        ConversationPriority.None => "none",
        ConversationPriority.Low => "low",
        ConversationPriority.Medium => "medium",
        ConversationPriority.High => "high",
        ConversationPriority.Urgent => "urgent",
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };

    public static ConversationPriority Priority(JsonElement element) => String(element) switch
    {
        "none" => ConversationPriority.None,
        "low" => ConversationPriority.Low,
        "medium" => ConversationPriority.Medium,
        "high" => ConversationPriority.High,
        "urgent" => ConversationPriority.Urgent,
        _ => throw new FormatException("Unknown priority."),
    };

    public static string Name(ConversationStatusKind status) => status switch
    {
        ConversationStatusKind.Open => "open",
        ConversationStatusKind.Pending => "pending",
        ConversationStatusKind.Snoozed => "snoozed",
        ConversationStatusKind.Resolved => "resolved",
        ConversationStatusKind.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static ConversationStatusKind Status(JsonElement element) => String(element) switch
    {
        "open" => ConversationStatusKind.Open,
        "pending" => ConversationStatusKind.Pending,
        "snoozed" => ConversationStatusKind.Snoozed,
        "resolved" => ConversationStatusKind.Resolved,
        "closed" => ConversationStatusKind.Closed,
        _ => throw new FormatException("Unknown status."),
    };

    public static string Name(SlaMetric metric) => metric switch
    {
        SlaMetric.FirstResponse => "first-response",
        SlaMetric.NextResponse => "next-response",
        SlaMetric.Resolution => "resolution",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    public static SlaMetric Metric(JsonElement element) => String(element) switch
    {
        "first-response" => SlaMetric.FirstResponse,
        "next-response" => SlaMetric.NextResponse,
        "resolution" => SlaMetric.Resolution,
        _ => throw new FormatException("Unknown SLA metric."),
    };

    /// <summary>Kebab-case name of any enum member (for enums owned by this feature).</summary>
    public static string Kebab<T>(T value) where T : struct, Enum =>
        EnumKebabCache<T>.ToKebab.TryGetValue(value, out var name) ? name : throw new ArgumentOutOfRangeException(nameof(value));

    public static T ParseKebab<T>(JsonElement element) where T : struct, Enum
    {
        var text = String(element);
        return EnumKebabCache<T>.FromKebab.TryGetValue(text, out var value)
            ? value
            : throw new FormatException($"Unknown {typeof(T).Name} value.");
    }

    private static class EnumKebabCache<T> where T : struct, Enum
    {
        public static readonly FrozenDictionary<T, string> ToKebab;
        public static readonly FrozenDictionary<string, T> FromKebab;

        static EnumKebabCache()
        {
            var values = Enum.GetValues<T>();
            var to = new Dictionary<T, string>(values.Length);
            var from = new Dictionary<string, T>(values.Length, StringComparer.Ordinal);
            foreach (var value in values)
            {
                var name = value.ToString();
                var builder = new StringBuilder(name.Length + 4);
                for (var index = 0; index < name.Length; index++)
                {
                    if (char.IsUpper(name[index]) && index > 0)
                        builder.Append('-');
                    builder.Append(char.ToLowerInvariant(name[index]));
                }
                var kebab = builder.ToString();
                to[value] = kebab;
                from[kebab] = value;
            }
            ToKebab = to.ToFrozenDictionary();
            FromKebab = from.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }
}
