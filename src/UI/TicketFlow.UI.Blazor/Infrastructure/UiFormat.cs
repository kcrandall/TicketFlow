using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using TicketFlow.Conversations;

namespace TicketFlow.UI.Blazor.Infrastructure;

/// <summary>Formatting helpers shared by the pages (invariant culture, UTC unless a time zone is supplied).</summary>
public static class UiFormat
{
    public static string Relative(DateTimeOffset? instant, DateTimeOffset now)
    {
        if (instant is not { } value)
            return "—";
        var delta = now - value;
        var future = delta < TimeSpan.Zero;
        var span = future ? -delta : delta;
        var text = span.TotalSeconds < 60 ? "just now"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours}h"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays}d"
            : value.ToString("MMM d", CultureInfo.InvariantCulture);
        return string.Equals(text, "just now", StringComparison.Ordinal) || span.TotalDays >= 30 ? text : future ? $"in {text}" : $"{text} ago";
    }

    public static string Short(DateTimeOffset? instant) => instant?.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture) ?? "—";

    public static string Date(DateTimeOffset? instant) => instant?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) ?? "—";

    public static string Duration(TimeSpan? value)
    {
        if (value is not { } span)
            return "—";
        if (span < TimeSpan.Zero)
            span = -span;
        return span.TotalMinutes < 1 ? $"{Math.Max(0, (int)span.TotalSeconds)}s"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
            : span.TotalDays < 1 ? (span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m")
            : span.Hours == 0 ? $"{(int)span.TotalDays}d" : $"{(int)span.TotalDays}d {span.Hours}h";
    }

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Percent(double? value) => value is { } percent ? percent.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";

    private static readonly ConcurrentDictionary<Enum, string> TokenCache = new();

    public static string Token<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        EnumTokenCache<TEnum>.Map.TryGetValue(value, out var formatted) ? formatted : FormatToken(value.ToString());

    public static string Token(Enum value) => TokenCache.GetOrAdd(value, static item => FormatToken(item.ToString()));

    private static string FormatToken(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c))
                sb.Append(' ').Append(char.ToLowerInvariant(c));
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static class EnumTokenCache<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly FrozenDictionary<TEnum, string> Map = Build();

        private static FrozenDictionary<TEnum, string> Build()
        {
            var values = Enum.GetValues<TEnum>();
            var dict = new Dictionary<TEnum, string>(values.Length);
            for (var i = 0; i < values.Length; i++)
                dict[values[i]] = FormatToken(values[i].ToString());
            return dict.ToFrozenDictionary();
        }
    }

    public static string Status(ConversationStatusKind kind) => kind switch
    {
        ConversationStatusKind.Open => "Open",
        ConversationStatusKind.Pending => "Pending",
        ConversationStatusKind.Snoozed => "Snoozed",
        ConversationStatusKind.Resolved => "Resolved",
        _ => "Closed",
    };

    public static string Channel(string kind) => kind switch
    {
        "livechat" => "Live chat",
        "email" => "Email",
        "sms" => "SMS",
        "whatsapp" => "WhatsApp",
        "slack" => "Slack",
        "teams" => "Teams",
        "api" => "API",
        "push" => "Push",
        _ => kind,
    };

    public static string ChannelIcon(string kind) => kind switch
    {
        "livechat" => "message-circle",
        "email" => "mail",
        "sms" => "smartphone",
        "whatsapp" => "message-square-text",
        "slack" or "teams" => "hash",
        "api" => "code",
        _ => "radio-tower",
    };

    private static readonly DateTime MinimumRangeDate = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaximumRangeDate = new(9998, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A report range from optional <c>yyyy-MM-dd</c> query values: default the last 14 days ending tomorrow (UTC midnight).</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Range(string? from, string? to, DateTimeOffset now)
    {
        var defaultEndDate = ClampDate(now.UtcDateTime.Date);
        var end = (TryDate(to) ?? new DateTimeOffset(defaultEndDate, TimeSpan.Zero)).AddDays(1);
        var start = TryDate(from) ?? end.AddDays(-14);
        if (start >= end)
            start = end.AddDays(-1);
        if (end - start > TimeSpan.FromDays(366))
            start = end.AddDays(-366);
        return (start, end);
    }

    private static DateTimeOffset? TryDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? new DateTimeOffset(ClampDate(date), TimeSpan.Zero) : null;

    private static DateTime ClampDate(DateTime date) =>
        date < MinimumRangeDate ? MinimumRangeDate : date > MaximumRangeDate ? MaximumRangeDate : date;
}
