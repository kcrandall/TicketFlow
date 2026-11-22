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

    public static string EncodeSchedule(BusinessCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartArray("week");
            foreach (var entry in calendar.WeeklyHours)
            {
                writer.WriteStartObject();
                writer.WriteNumber("d", (int)entry.Day);
                WriteSpan(writer, entry.Hours);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("overrides");
            foreach (var entry in calendar.Overrides)
            {
                writer.WriteStartObject();
                writer.WriteString("date", entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (entry.Name is not null)
                    writer.WriteString("name", entry.Name);
                writer.WriteStartArray("hours");
                foreach (var hours in entry.Hours)
                {
                    writer.WriteStartObject();
                    WriteSpan(writer, hours);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
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
            using var document = JsonDocument.Parse(schedule, new JsonDocumentOptions { MaxDepth = 8 });
            var root = Object(document.RootElement, "v", "week", "overrides");
            if (Required(root, "v").ValueKind != JsonValueKind.Number || Required(root, "v").GetInt32() != 1)
                throw new FormatException("Unsupported schedule version.");
            var week = new List<WeeklyBusinessHours>();
            foreach (var item in Array(Required(root, "week"), 7 * BusinessCalendar.MaximumSpansPerDay))
            {
                var entry = Object(item, "d", "s", "e");
                var day = Required(entry, "d").GetInt32();
                if (day is < 0 or > 6)
                    throw new FormatException("Invalid weekday.");
                week.Add(new((DayOfWeek)day, ReadSpan(entry)));
            }
            var overrides = new List<CalendarDateOverride>();
            foreach (var item in Array(Required(root, "overrides"), BusinessCalendar.MaximumOverrides))
            {
                var entry = Object(item, "date", "name", "hours");
                var date = DateOnly.ParseExact(Required(entry, "date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                string? overrideName = entry.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? throw new FormatException("Invalid name.") : null;
                var hours = Array(Required(entry, "hours"), BusinessCalendar.MaximumSpansPerDay).Select(span => ReadSpan(Object(span, "s", "e"))).ToList();
                overrides.Add(new(date, hours, overrideName));
            }
            var calendar = new BusinessCalendar(id, name, timeZoneId, week, overrides);
            // Only canonical encodings decode, so a stored schedule never silently changes meaning on re-encoding.
            if (EncodeSchedule(calendar) != schedule)
                throw new FormatException("The schedule is not in canonical form.");
            return calendar;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("The encoded schedule is invalid.", error);
        }
    }

    private static void WriteSpan(Utf8JsonWriter writer, BusinessHours hours)
    {
        writer.WriteNumber("s", (int)hours.Start.TotalMinutes);
        writer.WriteNumber("e", (int)hours.End.TotalMinutes);
    }

    private static BusinessHours ReadSpan(JsonElement element)
    {
        var start = Required(element, "s").GetInt32();
        var end = Required(element, "e").GetInt32();
        if (start is < 0 or > 1440 || end is < 0 or > 1440)
            throw new FormatException("Invalid business-hour span.");
        return new(TimeSpan.FromMinutes(start), TimeSpan.FromMinutes(end));
    }

    private static JsonElement Object(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected an object.");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException("Unknown schedule property.");
        return element;
    }

    private static JsonElement Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new FormatException($"Missing schedule property '{name}'.");

    private static IEnumerable<JsonElement> Array(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximum)
            throw new FormatException("Expected a bounded array.");
        return element.EnumerateArray();
    }
}
