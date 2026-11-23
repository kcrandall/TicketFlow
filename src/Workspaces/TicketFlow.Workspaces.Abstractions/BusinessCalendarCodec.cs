using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TicketFlow.Workspaces;

/// <summary>
/// Versioned JSON for a calendar's schedule (weekly spans and date overrides) used by durable stores:
/// <c>{"v":1,"week":[{"d":1,"s":540,"e":1020}],"overrides":[{"date":"2026-12-25","name":"...","hours":[{"s":0,"e":60}]}]}</c>
/// with minutes from local midnight. Decoding enforces every model bound and rejects unknown versions and properties.
/// </summary>
public static class BusinessCalendarCodec
{
    /// <summary>Maximum encoded length accepted by <see cref="DecodeSchedule"/>.</summary>
    public const int MaximumLength = 1_000_000;

    private const int MaximumPooledBufferCapacity = 64 * 1024;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? cachedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? cachedWriter;

    public static string EncodeSchedule(BusinessCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var (buffer, writer) = RentWriter();
        try
        {
            WriteSchedule(writer, calendar);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            ReleaseWriter(buffer, writer);
        }
    }

    private static (ArrayBufferWriter<byte> Buffer, Utf8JsonWriter Writer) RentWriter()
    {
        var buffer = cachedBuffer;
        var writer = cachedWriter;
        cachedBuffer = null;
        cachedWriter = null;
        if (buffer is null)
            buffer = new ArrayBufferWriter<byte>(512);
        else
            buffer.ResetWrittenCount();
        if (writer is null)
            writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);
        return (buffer, writer);
    }

    private static void ReleaseWriter(ArrayBufferWriter<byte> buffer, Utf8JsonWriter writer)
    {
        if (buffer.Capacity <= MaximumPooledBufferCapacity)
        {
            buffer.ResetWrittenCount();
            cachedBuffer = buffer;
            cachedWriter = writer;
        }
        else
            writer.Dispose();
    }

    private static void WriteSchedule(Utf8JsonWriter writer, BusinessCalendar calendar)
    {
        writer.WriteStartObject();
        writer.WriteNumber("v"u8, 1);
        writer.WriteStartArray("week"u8);
        var weekly = calendar.WeeklyHours;
        for (var i = 0; i < weekly.Count; i++)
        {
            var entry = weekly[i];
            writer.WriteStartObject();
            writer.WriteNumber("d"u8, (int)entry.Day);
            WriteSpan(writer, entry.Hours);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("overrides"u8);
        var overrides = calendar.Overrides;
        Span<char> dateChars = stackalloc char[10];
        for (var i = 0; i < overrides.Count; i++)
        {
            var entry = overrides[i];
            writer.WriteStartObject();
            entry.Date.TryFormat(dateChars, out _, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            writer.WriteString("date"u8, dateChars);
            if (entry.Name is not null)
                writer.WriteString("name"u8, entry.Name);
            writer.WriteStartArray("hours"u8);
            var hoursList = entry.Hours;
            for (var h = 0; h < hoursList.Count; h++)
            {
                writer.WriteStartObject();
                WriteSpan(writer, hoursList[h]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>Rebuilds a calendar from its identity columns and an encoded schedule.</summary>
    /// <exception cref="FormatException">The schedule is malformed, of an unknown version or violates a bound.</exception>
    public static BusinessCalendar DecodeSchedule(BusinessCalendarId id, string name, string timeZoneId, string schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Length > MaximumLength)
            throw new FormatException("The encoded schedule is too long.");
        try
        {
            var maxBytes = Encoding.UTF8.GetMaxByteCount(schedule.Length);
            byte[]? rented = null;
            Span<byte> inputBytes = maxBytes <= 1024
                ? stackalloc byte[1024]
                : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
            try
            {
                var inputLength = Encoding.UTF8.GetBytes(schedule, inputBytes);
                var inputUtf8 = inputBytes[..inputLength];
                var reader = new Utf8JsonReader(inputUtf8, new JsonReaderOptions { MaxDepth = 8 });
                if (!JsonDocument.TryParseValue(ref reader, out var document))
                    throw new FormatException("The encoded schedule is invalid.");
                using (document)
                {
                    var root = Object(document.RootElement, "v", "week", "overrides");
                    var version = Required(root, "v"u8);
                    if (version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
                        throw new FormatException("Unsupported schedule version.");
                    var weekElement = Required(root, "week"u8);
                    var weekEnum = Array(weekElement, 7 * BusinessCalendar.MaximumSpansPerDay);
                    var week = new List<WeeklyBusinessHours>(weekElement.GetArrayLength());
                    foreach (var item in weekEnum)
                    {
                        var entry = Object(item, "d", "s", "e");
                        var day = Required(entry, "d"u8).GetInt32();
                        if (day is < 0 or > 6)
                            throw new FormatException("Invalid weekday.");
                        week.Add(new((DayOfWeek)day, ReadSpan(entry)));
                    }
                    var overridesElement = Required(root, "overrides"u8);
                    var overridesEnum = Array(overridesElement, BusinessCalendar.MaximumOverrides);
                    var overrides = new List<CalendarDateOverride>(overridesElement.GetArrayLength());
                    foreach (var item in overridesEnum)
                    {
                        var entry = Object(item, "date", "name", "hours");
                        var date = DateOnly.ParseExact(Required(entry, "date"u8).GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                        string? overrideName = entry.TryGetProperty("name"u8, out var nameElement) ? nameElement.GetString() ?? throw new FormatException("Invalid name.") : null;
                        var hoursElement = Required(entry, "hours"u8);
                        var hoursEnum = Array(hoursElement, BusinessCalendar.MaximumSpansPerDay);
                        var hours = new BusinessHours[hoursElement.GetArrayLength()];
                        var hIndex = 0;
                        foreach (var span in hoursEnum)
                            hours[hIndex++] = ReadSpan(Object(span, "s", "e"));
                        overrides.Add(new(date, hours, overrideName));
                    }
                    var calendar = new BusinessCalendar(id, name, timeZoneId, week, overrides);
                    // Only canonical encodings decode, so a stored schedule never silently changes meaning on re-encoding.
                    var (canonicalBuffer, canonicalWriter) = RentWriter();
                    try
                    {
                        WriteSchedule(canonicalWriter, calendar);
                        if (!canonicalBuffer.WrittenSpan.SequenceEqual(inputUtf8))
                            throw new FormatException("The schedule is not in canonical form.");
                    }
                    finally
                    {
                        ReleaseWriter(canonicalBuffer, canonicalWriter);
                    }
                    return calendar;
                }
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("The encoded schedule is invalid.", error);
        }
    }

    private static void WriteSpan(Utf8JsonWriter writer, BusinessHours hours)
    {
        writer.WriteNumber("s"u8, (int)hours.Start.TotalMinutes);
        writer.WriteNumber("e"u8, (int)hours.End.TotalMinutes);
    }

    private static BusinessHours ReadSpan(JsonElement element)
    {
        var start = Required(element, "s"u8).GetInt32();
        var end = Required(element, "e"u8).GetInt32();
        if (start is < 0 or > 1440 || end is < 0 or > 1440)
            throw new FormatException("Invalid business-hour span.");
        return new(TimeSpan.FromMinutes(start), TimeSpan.FromMinutes(end));
    }

    private static JsonElement Object(JsonElement element, params ReadOnlySpan<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected an object.");
        foreach (var property in element.EnumerateObject())
        {
            var found = false;
            for (var i = 0; i < allowed.Length; i++)
                if (property.NameEquals(allowed[i]))
                {
                    found = true;
                    break;
                }
            if (!found)
                throw new FormatException("Unknown schedule property.");
        }
        return element;
    }

    private static JsonElement Required(JsonElement element, ReadOnlySpan<byte> name) =>
        element.TryGetProperty(name, out var value) ? value : throw new FormatException("Missing required schedule property.");

    private static JsonElement.ArrayEnumerator Array(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximum)
            throw new FormatException("Expected a bounded array.");
        return element.EnumerateArray();
    }
}
