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

    public static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <exception cref="FormatException">The JSON is malformed, oversized or invalid.</exception>
    public static T Read<T>(string json, Func<JsonElement, T> read)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumEncodedBytes || !System.Text.Unicode.Utf16.IsValid(json))
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
    public static void RequireProperties(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("A JSON object is required.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new FormatException("Unknown or duplicate property.");
    }

    public static void RequireVersion(JsonElement element, int version = 1)
    {
        if (element.GetProperty("v").GetInt32() != version)
            throw new FormatException("Unsupported SLA payload version.");
    }

    public static string String(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException("A string is required.");

    public static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? String(value) : null;

    public static bool Bool(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new FormatException("A boolean is required."),
    };

    public static IEnumerable<JsonElement> Array(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : throw new FormatException("An array is required.");

    public static void WriteInstant(Utf8JsonWriter writer, string name, DateTimeOffset value) =>
        writer.WriteString(name, value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));

    public static void WriteOptionalInstant(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } instant)
            WriteInstant(writer, name, instant);
    }

    public static DateTimeOffset Instant(JsonElement element) =>
        DateTimeOffset.ParseExact(String(element), "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? OptionalInstant(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? Instant(value) : null;

    /// <summary>Durations are integer microseconds.</summary>
    public static void WriteDuration(Utf8JsonWriter writer, string name, TimeSpan value) => writer.WriteNumber(name, value.Ticks / 10);

    public static void WriteOptionalDuration(Utf8JsonWriter writer, string name, TimeSpan? value)
    {
        if (value is { } duration)
            WriteDuration(writer, name, duration);
    }

    public static TimeSpan Duration(JsonElement element) => TimeSpan.FromTicks(checked(element.GetInt64() * 10));

    public static TimeSpan? OptionalDuration(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? Duration(value) : null;

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

    public static string Name(SlaClockBasis basis) => basis switch
    {
        SlaClockBasis.BusinessHours => "business",
        SlaClockBasis.CalendarTime => "calendar",
        _ => throw new ArgumentOutOfRangeException(nameof(basis)),
    };

    public static SlaClockBasis Basis(JsonElement element) => String(element) switch
    {
        "business" => SlaClockBasis.BusinessHours,
        "calendar" => SlaClockBasis.CalendarTime,
        _ => throw new FormatException("Unknown SLA clock basis."),
    };

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

    public static string Name(SlaClockState state) => state switch
    {
        SlaClockState.Running => "running",
        SlaClockState.Paused => "paused",
        SlaClockState.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static SlaClockState State(JsonElement element) => String(element) switch
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

    public static SlaClockOutcome Outcome(JsonElement element) => String(element) switch
    {
        "met" => SlaClockOutcome.Met,
        "missed" => SlaClockOutcome.Missed,
        "cancelled" => SlaClockOutcome.Cancelled,
        _ => throw new FormatException("Unknown clock outcome."),
    };

    public static void WriteActions(Utf8JsonWriter writer, string name, IReadOnlyList<SlaAction> actions)
    {
        writer.WriteStartArray(name);
        foreach (var action in actions)
        {
            writer.WriteStartObject();
            switch (action)
            {
                case NotifyUserAction user:
                    writer.WriteString("a", "notify-user");
                    writer.WriteString("user", user.User.Value);
                    break;
                case NotifyTeamAction team:
                    writer.WriteString("a", "notify-team");
                    writer.WriteString("team", team.Team.Value);
                    break;
                case RaisePriorityAction raise:
                    writer.WriteString("a", "raise-priority");
                    writer.WriteString("priority", Name(raise.Priority));
                    break;
                case ReassignTeamAction reassign:
                    writer.WriteString("a", "reassign-team");
                    writer.WriteString("team", reassign.Team.Value);
                    break;
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    public static IReadOnlyList<SlaAction> Actions(JsonElement element) => [.. Array(element).Select(Action)];

    private static SlaAction Action(JsonElement element)
    {
        var kind = String(element.GetProperty("a"));
        switch (kind)
        {
            case "notify-user":
                RequireProperties(element, "a", "user");
                return new NotifyUserAction(new UserId(String(element.GetProperty("user"))));
            case "notify-team":
                RequireProperties(element, "a", "team");
                return new NotifyTeamAction(new TeamId(String(element.GetProperty("team"))));
            case "raise-priority":
                RequireProperties(element, "a", "priority");
                return new RaisePriorityAction(Priority(element.GetProperty("priority")));
            case "reassign-team":
                RequireProperties(element, "a", "team");
                return new ReassignTeamAction(new TeamId(String(element.GetProperty("team"))));
            default:
                throw new FormatException("Unknown SLA action.");
        }
    }
}
