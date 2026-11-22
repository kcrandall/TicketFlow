using System.Collections.Frozen;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// A half-open span of local wall-clock time within one day, [<see cref="Start"/>, <see cref="End"/>), in whole minutes from local
/// midnight: <c>0 ≤ Start &lt; End ≤ 24:00</c>. Overnight shifts are two spans on consecutive days (for example 22:00-24:00 and
/// 00:00-06:00); the calculator joins them seamlessly.
/// </summary>
public readonly record struct BusinessHours : IComparable<BusinessHours>
{
    public static TimeSpan EndOfDay { get; } = TimeSpan.FromHours(24);

    public BusinessHours(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end > EndOfDay || start >= end)
            throw new ArgumentException("Business hours must satisfy 00:00 <= start < end <= 24:00.", nameof(end));
        if (start.Ticks % TimeSpan.TicksPerMinute != 0 || end.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentException("Business hours use whole minutes.", nameof(start));
        Start = start;
        End = end;
    }

    /// <summary>Hours from whole <paramref name="startHour"/>:<paramref name="startMinute"/> to <paramref name="endHour"/>:<paramref name="endMinute"/>.</summary>
    public static BusinessHours From(int startHour, int startMinute, int endHour, int endMinute) =>
        new(new TimeSpan(startHour, startMinute, 0), new TimeSpan(endHour, endMinute, 0));

    public TimeSpan Start { get; }
    public TimeSpan End { get; }

    public int CompareTo(BusinessHours other) => Start != other.Start ? Start.CompareTo(other.Start) : End.CompareTo(other.End);

    public override string ToString() => $"{(int)Start.TotalHours:D2}:{Start.Minutes:D2}-{(int)End.TotalHours:D2}:{End.Minutes:D2}";

    /// <summary>Sorts, rejects overlaps and merges touching spans, so equal schedules have one canonical form.</summary>
    internal static BusinessHours[] Normalize(IEnumerable<BusinessHours> hours, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(hours, parameterName);
        var sorted = new List<BusinessHours>();
        foreach (var span in hours)
        {
            if (span.End == TimeSpan.Zero)
                throw new ArgumentException("Business hours must be initialized.", parameterName);
            sorted.Add(span);
            if (sorted.Count > maximum)
                throw new ArgumentException($"At most {maximum} business-hour spans are allowed per day.", parameterName);
        }
        sorted.Sort();
        var merged = new List<BusinessHours>(sorted.Count);
        foreach (var span in sorted)
        {
            if (merged.Count > 0 && merged[^1].End > span.Start)
                throw new ArgumentException("Business-hour spans on the same day cannot overlap.", parameterName);
            if (merged.Count > 0 && merged[^1].End == span.Start)
                merged[^1] = new(merged[^1].Start, span.End);
            else
                merged.Add(span);
        }
        return [.. merged];
    }
}

/// <summary>Recurring weekly business hours for one weekday.</summary>
public sealed record WeeklyBusinessHours
{
    public WeeklyBusinessHours(DayOfWeek day, BusinessHours hours)
    {
        if (!Enum.IsDefined(day))
            throw new ArgumentOutOfRangeException(nameof(day));
        if (hours.End == TimeSpan.Zero)
            throw new ArgumentException("Business hours must be initialized.", nameof(hours));
        Day = day;
        Hours = hours;
    }

    public DayOfWeek Day { get; }
    public BusinessHours Hours { get; }
}

/// <summary>
/// Replaces the weekly hours on one local date. Empty <see cref="Hours"/> means closed all day (a holiday); otherwise the listed
/// spans are the only open time that date.
/// </summary>
public sealed record CalendarDateOverride
{
    public const int MaximumNameLength = 128;

    public CalendarDateOverride(DateOnly date, IEnumerable<BusinessHours>? hours = null, string? name = null)
    {
        Date = date;
        Hours = BusinessHours.Normalize(hours ?? [], BusinessCalendar.MaximumSpansPerDay, nameof(hours));
        Name = name is null ? null : WorkspaceText.RequireText(name, MaximumNameLength, nameof(name));
    }

    /// <summary>A closed-all-day holiday.</summary>
    public static CalendarDateOverride Holiday(DateOnly date, string? name = null) => new(date, [], name);

    public DateOnly Date { get; }
    public IReadOnlyList<BusinessHours> Hours { get; }
    public string? Name { get; }
    public bool IsClosed => Hours.Count == 0;

    public bool Equals(CalendarDateOverride? other) =>
        other is not null && Date == other.Date && Name == other.Name && Hours.SequenceEqual(other.Hours);

    public override int GetHashCode() => HashCode.Combine(Date, Name, Hours.Count);
}

/// <summary>
/// Business hours of a workspace or inbox: weekly spans per weekday interpreted as local wall-clock time in
/// <see cref="TimeZoneId"/>, plus date overrides (holidays and special hours). Use <see cref="BusinessCalendarCalculator"/> for
/// open/closed and business-time arithmetic.
/// </summary>
public sealed record BusinessCalendar
{
    public const int MaximumNameLength = 128;
    public const int MaximumSpansPerDay = 8;
    public const int MaximumOverrides = 1024;

    private static readonly DayOfWeek[] Days = [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday];

    private readonly BusinessHours[][] weekly;
    private readonly FrozenDictionary<DateOnly, CalendarDateOverride> overridesByDate;

    public BusinessCalendar(
        BusinessCalendarId id,
        string name,
        string timeZoneId,
        IEnumerable<WeeklyBusinessHours>? weeklyHours = null,
        IEnumerable<CalendarDateOverride>? overrides = null)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A calendar identifier is required.", nameof(id));
        Id = id;
        Name = WorkspaceText.RequireText(name, MaximumNameLength, nameof(name));
        TimeZoneId = WorkspaceTimeZones.RequireId(timeZoneId, nameof(timeZoneId));

        var byDay = Days.ToDictionary(day => day, _ => new List<BusinessHours>());
        foreach (var entry in weeklyHours ?? [])
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(weeklyHours));
            byDay[entry.Day].Add(entry.Hours);
        }
        weekly = [.. Days.Select(day => BusinessHours.Normalize(byDay[day], MaximumSpansPerDay, nameof(weeklyHours)))];

        var dated = new Dictionary<DateOnly, CalendarDateOverride>();
        foreach (var entry in overrides ?? [])
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(overrides));
            if (!dated.TryAdd(entry.Date, entry))
                throw new ArgumentException("Date overrides must have unique dates.", nameof(overrides));
            if (dated.Count > MaximumOverrides)
                throw new ArgumentException($"A calendar can have at most {MaximumOverrides} date overrides.", nameof(overrides));
        }
        overridesByDate = dated.ToFrozenDictionary();
        Overrides = [.. dated.Values.OrderBy(entry => entry.Date)];
        WeeklyHours = [.. Days.SelectMany(day => weekly[(int)day].Select(hours => new WeeklyBusinessHours(day, hours)))];
    }

    public BusinessCalendarId Id { get; }
    public string Name { get; }

    /// <summary>IANA or Windows time-zone identifier the hours are expressed in.</summary>
    public string TimeZoneId { get; }

    /// <summary>Canonical weekly spans ordered by weekday (Sunday first) then start.</summary>
    public IReadOnlyList<WeeklyBusinessHours> WeeklyHours { get; }

    /// <summary>Date overrides ordered by date.</summary>
    public IReadOnlyList<CalendarDateOverride> Overrides { get; }

    /// <summary>Canonical weekly spans of <paramref name="day"/>.</summary>
    public IReadOnlyList<BusinessHours> HoursFor(DayOfWeek day) => weekly[(int)day];

    /// <summary>The open spans on local <paramref name="date"/>: its override if any, otherwise the weekday's hours.</summary>
    public IReadOnlyList<BusinessHours> HoursOn(DateOnly date) =>
        overridesByDate.TryGetValue(date, out var entry) ? entry.Hours : weekly[(int)date.DayOfWeek];

    public bool Equals(BusinessCalendar? other) =>
        other is not null && Id == other.Id && Name == other.Name && TimeZoneId == other.TimeZoneId
        && WeeklyHours.SequenceEqual(other.WeeklyHours) && Overrides.SequenceEqual(other.Overrides);

    public override int GetHashCode() => HashCode.Combine(Id, Name, TimeZoneId, WeeklyHours.Count, Overrides.Count);
}

/// <summary>A calendar with its scope and revision (first revision is 1).</summary>
public sealed record StoredBusinessCalendar(TicketScope Scope, BusinessCalendar Calendar, long Revision);
