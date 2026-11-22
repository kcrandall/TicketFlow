namespace TicketFlow.Workspaces;

/// <summary>A half-open UTC interval [<see cref="Start"/>, <see cref="End"/>) of open business time.</summary>
public readonly record struct BusinessInterval(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Business-time arithmetic over a <see cref="BusinessCalendar"/> with wall-clock semantics: an instant is open when its local time
/// in the calendar's zone falls inside that local date's spans. Across DST transitions, local times skipped by a spring-forward gap
/// are never open, and local times repeated by a fall-back overlap are open in both passes, so business time is real elapsed time.
/// Results are UTC (<see cref="DateTimeOffset.Offset"/> zero). Thread-safe and allocation-light.
/// </summary>
/// <remarks>
/// Offset transitions are located by sampling the zone at most one day apart and bisecting to the tick, so a zone that changes offset
/// and changes back within a single day is not supported (no such zone exists in current time-zone data). Searches never look further
/// than <see cref="MaximumHorizon"/>.
/// </remarks>
public sealed class BusinessCalendarCalculator
{
    /// <summary>Longest span searched or measured by one call (about ten years).</summary>
    public static TimeSpan MaximumHorizon { get; } = TimeSpan.FromDays(3660);

    /// <summary>Earliest supported instant.</summary>
    public static DateTimeOffset MinimumInstant { get; } = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Latest supported instant.</summary>
    public static DateTimeOffset MaximumInstant { get; } = new(2300, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Maximum intervals returned by <see cref="GetOpenIntervals"/>.</summary>
    public const int MaximumIntervals = 10_000;

    private static readonly long Day = TimeSpan.TicksPerDay;
    private readonly BusinessCalendar calendar;
    private readonly TimeZoneInfo zone;

    /// <summary>Resolves <see cref="BusinessCalendar.TimeZoneId"/> on this host.</summary>
    /// <exception cref="TimeZoneNotFoundException">The zone is unknown on this host.</exception>
    public BusinessCalendarCalculator(BusinessCalendar calendar)
        : this(calendar, TimeZoneInfo.FindSystemTimeZoneById((calendar ?? throw new ArgumentNullException(nameof(calendar))).TimeZoneId))
    {
    }

    /// <summary>Uses an explicit zone instead of resolving <see cref="BusinessCalendar.TimeZoneId"/> (for custom or test zones).</summary>
    public BusinessCalendarCalculator(BusinessCalendar calendar, TimeZoneInfo timeZone)
    {
        this.calendar = calendar ?? throw new ArgumentNullException(nameof(calendar));
        zone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
    }

    public BusinessCalendar Calendar => calendar;

    public TimeZoneInfo TimeZone => zone;

    /// <summary>True when <paramref name="at"/> is inside open business time.</summary>
    public bool IsOpen(DateTimeOffset at)
    {
        var start = Require(at, nameof(at));
        foreach (var _ in Scan(start, start + 1))
            return true;
        return false;
    }

    /// <summary>
    /// The earliest open instant at or after <paramref name="at"/> (<paramref name="at"/> itself when open), or
    /// <see langword="null"/> when the calendar does not open within <see cref="MaximumHorizon"/>.
    /// </summary>
    public DateTimeOffset? NextOpen(DateTimeOffset at)
    {
        var start = Require(at, nameof(at));
        foreach (var (open, _) in Scan(start, Limit(start)))
            return Utc(open);
        return null;
    }

    /// <summary>
    /// The earliest instant at or after <paramref name="at"/> that is not open (<paramref name="at"/> itself when closed), or
    /// <see langword="null"/> when the calendar does not close within <see cref="MaximumHorizon"/>.
    /// </summary>
    public DateTimeOffset? NextClose(DateTimeOffset at)
    {
        var start = Require(at, nameof(at));
        var cursor = start;
        foreach (var (open, close) in Scan(start, Limit(start)))
        {
            if (open > cursor)
                return Utc(cursor);
            cursor = close;
        }
        return cursor < Limit(start) ? Utc(cursor) : null;
    }

    /// <summary>
    /// The earliest instant <c>t ≥ start</c> such that <see cref="BusinessTimeBetween"/>(<paramref name="start"/>, t) equals
    /// <paramref name="duration"/>; <paramref name="start"/> itself for a zero duration. Returns <see langword="null"/> when the
    /// duration cannot be accumulated within <see cref="MaximumHorizon"/>.
    /// </summary>
    public DateTimeOffset? AddBusinessTime(DateTimeOffset start, TimeSpan duration)
    {
        var from = Require(start, nameof(start));
        if (duration < TimeSpan.Zero || duration > MaximumHorizon)
            throw new ArgumentOutOfRangeException(nameof(duration), "Durations must be between zero and the maximum horizon.");
        var remaining = duration.Ticks;
        if (remaining == 0)
            return Utc(from);
        foreach (var (open, close) in Scan(from, Limit(from)))
        {
            if (remaining <= close - open)
                return Utc(open + remaining);
            remaining -= close - open;
        }
        return null;
    }

    /// <summary>Open business time in [<paramref name="from"/>, <paramref name="to"/>); negative when <paramref name="to"/> precedes <paramref name="from"/>.</summary>
    public TimeSpan BusinessTimeBetween(DateTimeOffset from, DateTimeOffset to)
    {
        var start = Require(from, nameof(from));
        var end = Require(to, nameof(to));
        if (end < start)
            return -BusinessTimeBetween(to, from);
        if (end - start > MaximumHorizon.Ticks)
            throw new ArgumentOutOfRangeException(nameof(to), "The measured span exceeds the maximum horizon.");
        long total = 0;
        foreach (var (open, close) in Scan(start, end))
            total += close - open;
        return TimeSpan.FromTicks(total);
    }

    /// <summary>Merged open intervals intersecting [<paramref name="from"/>, <paramref name="to"/>), clipped to it, in order.</summary>
    public IReadOnlyList<BusinessInterval> GetOpenIntervals(DateTimeOffset from, DateTimeOffset to)
    {
        var start = Require(from, nameof(from));
        var end = Require(to, nameof(to));
        if (end < start || end - start > MaximumHorizon.Ticks)
            throw new ArgumentOutOfRangeException(nameof(to), "The range must be ordered and within the maximum horizon.");
        var result = new List<BusinessInterval>();
        long? pendingStart = null, pendingEnd = null;
        foreach (var (open, close) in Scan(start, end))
        {
            if (pendingEnd == open)
            {
                pendingEnd = close;
                continue;
            }
            if (pendingStart is { } s && pendingEnd is { } e)
                Add(result, s, e);
            pendingStart = open;
            pendingEnd = close;
        }
        if (pendingStart is { } lastStart && pendingEnd is { } lastEnd)
            Add(result, lastStart, lastEnd);
        return result;
    }

    private static void Add(List<BusinessInterval> result, long start, long end)
    {
        if (result.Count >= MaximumIntervals)
            throw new InvalidOperationException($"The range contains more than {MaximumIntervals} open intervals.");
        result.Add(new(Utc(start), Utc(end)));
    }

    /// <summary>
    /// Yields open pieces (UTC ticks, half-open, ordered, possibly adjacent) within [<paramref name="from"/>, <paramref name="limit"/>).
    /// UTC time is walked in segments of constant offset (at most one day each). Within a segment local time is linear, so the open
    /// local spans of each local date intersected with the segment map back to UTC in order. Gap times have no UTC instant and are
    /// never produced; overlap times belong to two segments and are produced in both.
    /// </summary>
    private IEnumerable<(long Start, long End)> Scan(long from, long limit)
    {
        var cursor = from;
        while (cursor < limit)
        {
            var offset = Offset(cursor);
            var segmentEnd = Math.Min(cursor + Day, limit);
            if (Offset(segmentEnd) != offset)
                segmentEnd = FindTransition(cursor, segmentEnd, offset);
            var localStart = cursor + offset;
            var localEnd = segmentEnd + offset;
            var date = DateOnly.FromDateTime(new DateTime(localStart, DateTimeKind.Unspecified));
            for (var dayStart = date.ToDateTime(TimeOnly.MinValue).Ticks; dayStart < localEnd; dayStart += Day, date = date.AddDays(1))
            {
                foreach (var hours in calendar.HoursOn(date))
                {
                    var open = Math.Max(dayStart + hours.Start.Ticks, localStart);
                    var close = Math.Min(dayStart + hours.End.Ticks, localEnd);
                    if (open < close)
                        yield return (open - offset, close - offset);
                }
            }
            cursor = segmentEnd;
        }
    }

    /// <summary>First tick in (<paramref name="low"/>, <paramref name="high"/>] whose offset differs from <paramref name="offset"/>.</summary>
    private long FindTransition(long low, long high, long offset)
    {
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            if (Offset(middle) == offset)
                low = middle;
            else
                high = middle;
        }
        return high;
    }

    private long Offset(long utcTicks) => zone.GetUtcOffset(new DateTime(utcTicks, DateTimeKind.Utc)).Ticks;

    private static long Limit(long start) => Math.Min(start + MaximumHorizon.Ticks, MaximumInstant.UtcTicks + MaximumHorizon.Ticks);

    private static long Require(DateTimeOffset instant, string parameterName)
    {
        if (instant < MinimumInstant || instant > MaximumInstant)
            throw new ArgumentOutOfRangeException(parameterName, "Instants must be between 1900-01-01 and 2300-01-01 UTC.");
        return instant.UtcTicks;
    }

    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);
}
