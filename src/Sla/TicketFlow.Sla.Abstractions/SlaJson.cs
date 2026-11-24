using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Sla;

/// <summary>Shared JSON reading/writing for the SLA codecs: strict properties, explicit names for enums and invariant instants.</summary>
internal static class SlaJson
{
    public const int MaximumEncodedBytes = 1024 * 1024;

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
        if (span.Length > MaximumEncodedBytes || (span.ContainsAnyInRange('\uD800', '\uDFFF') && !System.Text.Unicode.Utf16.IsValid(span)))
            throw new FormatException("The SLA payload is oversized or malformed.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("SLA payloads are JSON objects.");
            return read(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new FormatException("The SLA payload is invalid.", error);
        }
    }

    /// <summary>Rejects properties outside <paramref name="allowed"/> and duplicates; every allowed property is optional unless read.</summary>
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

    extension(JsonElement element)
    {
        public JsonElement? this[string name] => element.TryGetProperty(name, out var value) ? value : null;

        public string RequiredString() => element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException("A string is required.");

        public string RequiredString(string name) => element.GetProperty(name).RequiredString();

        public string? OptionalString(string name) =>
            element[name] is { ValueKind: not JsonValueKind.Null } value ? value.RequiredString() : null;

        public bool RequiredBool() => element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException("A boolean is required."),
        };

        public bool RequiredBool(string name) => element.GetProperty(name).RequiredBool();

        public JsonElement.ArrayEnumerator RequiredArray() =>
            element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : throw new FormatException("An array is required.");

        public JsonElement.ArrayEnumerator RequiredArray(string name) => element.GetProperty(name).RequiredArray();

        public DateTimeOffset RequiredInstant() =>
            DateTimeOffset.ParseExact(element.RequiredString(), "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public DateTimeOffset RequiredInstant(string name) => element.GetProperty(name).RequiredInstant();

        public DateTimeOffset? OptionalInstant(string name) => element[name]?.RequiredInstant();

        public TimeSpan RequiredDuration() => TimeSpan.FromTicks(checked(element.GetInt64() * 10));

        public TimeSpan RequiredDuration(string name) => element.GetProperty(name).RequiredDuration();

        public TimeSpan? OptionalDuration(string name) => element[name]?.RequiredDuration();
    }

    public static void RequireVersion(JsonElement element, int version = 1)
    {
        if (element.GetProperty("v"u8).GetInt32() != version)
            throw new FormatException("Unsupported SLA payload version.");
    }

    public static void WriteInstant(Utf8JsonWriter writer, string name, DateTimeOffset value)
    {
        Span<char> chars = stackalloc char[27];
        value.UtcDateTime.TryFormat(chars, out var written, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        writer.WriteString(name, chars[..written]);
    }

    public static void WriteOptionalInstant(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
            WriteInstant(writer, name, instant);
    }

    /// <summary>Durations are integer microseconds.</summary>
    public static void WriteDuration(Utf8JsonWriter writer, string name, TimeSpan value) => writer.WriteNumber(name, value.Ticks / 10);

    public static void WriteOptionalDuration(Utf8JsonWriter writer, string name, TimeSpan? value)
    {
        if (value is { } duration)
            WriteDuration(writer, name, duration);
    }

    public static SlaMetric Metric(JsonElement element) => element.RequiredString() switch
    {
        "first-response" => SlaMetric.FirstResponse,
        "next-response" => SlaMetric.NextResponse,
        "resolution" => SlaMetric.Resolution,
        _ => throw new FormatException("Unknown SLA metric."),
    };

    public static string Name(SlaClockBasis basis) => basis switch
    {
        SlaClockBasis.BusinessHours => "business",
        SlaClockBasis.CalendarTime => "calendar",
        _ => throw new ArgumentOutOfRangeException(nameof(basis)),
    };

    public static SlaClockBasis Basis(JsonElement element) => element.RequiredString() switch
    {
        "business" => SlaClockBasis.BusinessHours,
        "calendar" => SlaClockBasis.CalendarTime,
        _ => throw new FormatException("Unknown SLA clock basis."),
    };

    public static ConversationPriority Priority(JsonElement element) => element.RequiredString() switch
    {
        "none" => ConversationPriority.None,
        "low" => ConversationPriority.Low,
        "medium" => ConversationPriority.Medium,
        "high" => ConversationPriority.High,
        "urgent" => ConversationPriority.Urgent,
        _ => throw new FormatException("Unknown priority."),
    };

    public static ConversationStatusKind Status(JsonElement element) => element.RequiredString() switch
    {
        "open" => ConversationStatusKind.Open,
        "pending" => ConversationStatusKind.Pending,
        "snoozed" => ConversationStatusKind.Snoozed,
        "resolved" => ConversationStatusKind.Resolved,
        "closed" => ConversationStatusKind.Closed,
        _ => throw new FormatException("Unknown status."),
    };

    public static string Name(SlaClockState state) => state switch
    {
        SlaClockState.Running => "running",
        SlaClockState.Paused => "paused",
        SlaClockState.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static SlaClockState State(JsonElement element) => element.RequiredString() switch
    {
        "running" => SlaClockState.Running,
        "paused" => SlaClockState.Paused,
        "completed" => SlaClockState.Completed,
        _ => throw new FormatException("Unknown clock state."),
    };

    public static string Name(SlaClockOutcome outcome) => outcome switch
    {
        SlaClockOutcome.Met => "met",
        SlaClockOutcome.Missed => "missed",
        SlaClockOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static SlaClockOutcome Outcome(JsonElement element) => element.RequiredString() switch
    {
        "met" => SlaClockOutcome.Met,
        "missed" => SlaClockOutcome.Missed,
        "cancelled" => SlaClockOutcome.Cancelled,
        _ => throw new FormatException("Unknown clock outcome."),
    };

    public static void WriteActions(Utf8JsonWriter writer, string name, IReadOnlyList<SlaAction> actions)
    {
        writer.WriteStartArray(name);
        for (var i = 0; i < actions.Count; i++)
        {
            writer.WriteStartObject();
            switch (actions[i])
            {
                case NotifyUserAction user:
                    writer.WriteString("a"u8, "notify-user"u8);
                    writer.WriteString("user"u8, user.User.Value);
                    break;
                case NotifyTeamAction team:
                    writer.WriteString("a"u8, "notify-team"u8);
                    writer.WriteString("team"u8, team.Team.Value);
                    break;
                case RaisePriorityAction raise:
                    writer.WriteString("a"u8, "raise-priority"u8);
                    writer.WriteString("priority"u8, raise.Priority.WireName);
                    break;
                case ReassignTeamAction reassign:
                    writer.WriteString("a"u8, "reassign-team"u8);
                    writer.WriteString("team"u8, reassign.Team.Value);
                    break;
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public static IReadOnlyList<SlaAction> Actions(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new FormatException("An array is required.");
        var len = element.GetArrayLength();
        if (len == 0)
            return [];
        var list = new List<SlaAction>(len);
        foreach (var item in element.EnumerateArray())
            list.Add(Action(item));
        return list;
    }

    private static SlaAction Action(JsonElement element)
    {
        var kind = element.GetProperty("a"u8).RequiredString();
        switch (kind)
        {
            case "notify-user":
                RequireProperties(element, "a", "user");
                return new NotifyUserAction(new UserId(element.GetProperty("user"u8).RequiredString()));
            case "notify-team":
                RequireProperties(element, "a", "team");
                return new NotifyTeamAction(new TeamId(element.GetProperty("team"u8).RequiredString()));
            case "raise-priority":
                RequireProperties(element, "a", "priority");
                return new RaisePriorityAction(Priority(element.GetProperty("priority"u8)));
            case "reassign-team":
                RequireProperties(element, "a", "team");
                return new ReassignTeamAction(new TeamId(element.GetProperty("team"u8).RequiredString()));
            default:
                throw new FormatException("Unknown SLA action.");
        }
    }
}
