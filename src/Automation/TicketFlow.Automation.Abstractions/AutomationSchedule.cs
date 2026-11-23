using System.Globalization;
using System.Numerics;

namespace TicketFlow.Automation;

/// <summary>
/// A bounded cron-like schedule evaluated in a time zone: five fields <c>minute hour day-of-month month day-of-week</c> (minute 0-59,
/// hour 0-23, day 1-31, month 1-12, weekday 0-7 with 0 and 7 = Sunday), each <c>*</c>, a value, a range <c>a-b</c>, a step
/// <c>*/s</c> or <c>a-b/s</c>, or a comma list of those. As in classic cron, when both day-of-month and day-of-week are restricted a
/// day matching either fires. At most <see cref="MaximumFiresPerDay"/> times per day (every 15 minutes).
/// <para>DST: local times are interpreted in <see cref="TimeZoneId"/>. A local time that does not exist (spring-forward gap) fires once
/// at the transition instant; a local time that occurs twice (fall-back overlap) fires once, at its first occurrence. Fire instants are
/// therefore strictly increasing and never duplicated.</para>
/// </summary>
public sealed record AutomationSchedule
{
    public const int MaximumExpressionLength = 128;
    public const int MaximumTimeZoneLength = 64;
    public const int MaximumFiresPerDay = 96;

    /// <summary>Searches at most this many days ahead; a schedule that matches no day within it never fires.</summary>
    public const int MaximumSearchDays = 366 * 5;

    private readonly ulong minutesMask;
    private readonly uint hoursMask;
    private readonly uint daysMask;
    private readonly ushort monthsMask;
    private readonly byte weekdaysMask;
    private readonly bool dayRestricted;
    private readonly bool weekdayRestricted;
    private TimeZoneInfo? cachedZone;

    public AutomationSchedule(string expression, string timeZoneId)
    {
        Expression = AutomationText.RequireText(expression, MaximumExpressionLength, nameof(expression));
        TimeZoneId = AutomationText.RequireText(timeZoneId, MaximumTimeZoneLength, nameof(timeZoneId));
        var fields = Expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new ArgumentException("Schedules have five fields: minute hour day-of-month month day-of-week.", nameof(expression));
        Parse(fields[0], 0, 59, out minutesMask, nameof(expression));
        Parse(fields[1], 0, 23, out var hoursBits, nameof(expression));
        hoursMask = (uint)hoursBits;
        dayRestricted = !Parse(fields[2], 1, 31, out var daysBits, nameof(expression));
        daysMask = (uint)daysBits;
        Parse(fields[3], 1, 12, out var monthsBits, nameof(expression));
        monthsMask = (ushort)monthsBits;
        weekdayRestricted = !Parse(fields[4], 0, 7, out var weekdayBits, nameof(expression));
        var sunday = (weekdayBits & 1UL) | ((weekdayBits >> 7) & 1UL);
        weekdaysMask = (byte)((weekdayBits & 0x7EUL) | sunday);
        if (BitOperations.PopCount(minutesMask) * BitOperations.PopCount(hoursMask) > MaximumFiresPerDay)
            throw new ArgumentException($"Schedules fire at most {MaximumFiresPerDay} times per day.", nameof(expression));
        if (monthsMask == 0 || daysMask == 0 || weekdaysMask == 0)
            throw new ArgumentException("The schedule matches no day.", nameof(expression));
    }

    /// <summary>The normalized expression text as supplied.</summary>
    public string Expression { get; }

    /// <summary>An IANA or Windows time zone identifier resolved on the evaluating host.</summary>
    public string TimeZoneId { get; }

    /// <summary>Resolves <see cref="TimeZoneId"/> on this host.</summary>
    /// <exception cref="TimeZoneNotFoundException">The zone is unknown on this host.</exception>
    public TimeZoneInfo ResolveTimeZone() => cachedZone ??= TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>
    /// The first fire instant strictly after <paramref name="after"/> (UTC, whole minutes), or <see langword="null"/> when none occurs
    /// within <see cref="MaximumSearchDays"/>. <paramref name="zone"/> overrides <see cref="TimeZoneId"/> (custom or test zones).
    /// </summary>
    public DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= ResolveTimeZone();
        var localAfter = TimeZoneInfo.ConvertTime(after, zone);
        var hasDst = zone.SupportsDaylightSavingTime;
        var backOneDay = hasDst && localAfter.Hour < 3;
        var startDate = backOneDay ? localAfter.Date.AddDays(-1) : localAfter.Date;
        var maxOffset = backOneDay ? MaximumSearchDays : MaximumSearchDays - 1;
        for (var offset = 0; offset <= maxOffset; offset++)
        {
            var date = startDate.AddDays(offset);
            if (!DayMatches(date))
                continue;
            var isSameDay = date == localAfter.Date;
            var minHour = isSameDay ? (hasDst ? Math.Max(0, localAfter.Hour - 2) : localAfter.Hour) : 0;
            var hMask = hoursMask & (~0U << minHour);
            while (hMask != 0)
            {
                var hour = BitOperations.TrailingZeroCount(hMask);
                hMask &= hMask - 1;
                var minMinute = isSameDay && !hasDst && hour == localAfter.Hour ? localAfter.Minute : 0;
                var mMask = minutesMask & (~0UL << minMinute);
                while (mMask != 0)
                {
                    var minute = BitOperations.TrailingZeroCount(mMask);
                    mMask &= mMask - 1;
                    var instant = ToInstant(DateTime.SpecifyKind(date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified), zone);
                    if (instant > after)
                        return instant;
                }
            }
        }
        return null;
    }

    public bool Equals(AutomationSchedule? other) => other is not null && Expression == other.Expression && TimeZoneId == other.TimeZoneId;

    public override int GetHashCode() => HashCode.Combine(Expression, TimeZoneId);

    private bool DayMatches(DateTime date)
    {
        if ((monthsMask & (1 << date.Month)) == 0)
            return false;
        var day = (daysMask & (1U << date.Day)) != 0;
        var weekday = (weekdaysMask & (1 << (int)date.DayOfWeek)) != 0;
        return (dayRestricted, weekdayRestricted) switch
        {
            (true, true) => day || weekday,
            (true, false) => day,
            (false, true) => weekday,
            (false, false) => true,
        };
    }

    /// <summary>Maps a local wall-clock time to UTC: gaps map to the transition instant, overlaps to the first occurrence.</summary>
    internal static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            var end = local;
            for (var step = 0; step < 24 * 60 && zone.IsInvalidTime(end); step++)
                end = end.AddMinutes(1);
            // The first valid local minute after the gap; gaps are minute-aligned in every real zone.
            return new DateTimeOffset(end - zone.GetUtcOffset(end), TimeSpan.Zero);
        }
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var max = offsets[0];
            for (var i = 1; i < offsets.Length; i++)
                if (offsets[i] > max)
                    max = offsets[i];
            return new DateTimeOffset(local - max, TimeSpan.Zero);
        }
        return new DateTimeOffset(local - zone.GetUtcOffset(local), TimeSpan.Zero);
    }

    /// <summary>Parses one field into <paramref name="mask"/>; returns true when the field is an unrestricted <c>*</c>.</summary>
    private static bool Parse(string field, int minimum, int maximum, out ulong mask, string parameterName)
    {
        mask = 0;
        if (field == "*")
        {
            for (var value = minimum; value <= maximum; value++)
                mask |= 1UL << value;
            return true;
        }
        var parts = field.Split(',');
        if (parts.Length > 64)
            throw new ArgumentException("Schedule fields have at most 64 list items.", parameterName);
        foreach (var part in parts)
        {
            var step = 1;
            var range = part.AsSpan();
            var slash = range.IndexOf('/');
            if (slash >= 0)
            {
                step = Number(range[(slash + 1)..], 1, maximum - minimum + 1, parameterName);
                range = range[..slash];
            }
            int start, end;
            if (range.SequenceEqual("*"))
                (start, end) = (minimum, maximum);
            else if (range.IndexOf('-') is var dash and > 0)
            {
                start = Number(range[..dash], minimum, maximum, parameterName);
                end = Number(range[(dash + 1)..], minimum, maximum, parameterName);
                if (end < start)
                    throw new ArgumentException("Schedule ranges must ascend.", parameterName);
            }
            else
            {
                start = Number(range, minimum, maximum, parameterName);
                end = slash >= 0 ? maximum : start;
            }
            for (var value = start; value <= end; value += step)
                mask |= 1UL << value;
        }
        return false;
    }

    private static int Number(ReadOnlySpan<char> text, int minimum, int maximum, string parameterName)
    {
        if (text.Length is 0 or > 2)
            throw new ArgumentException($"Schedule values must be {minimum}-{maximum}.", parameterName);
        for (var i = 0; i < text.Length; i++)
            if (!char.IsAsciiDigit(text[i]))
                throw new ArgumentException($"Schedule values must be {minimum}-{maximum}.", parameterName);
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            throw new ArgumentException($"Schedule values must be {minimum}-{maximum}.", parameterName);
        return value;
    }
}
