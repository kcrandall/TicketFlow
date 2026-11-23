namespace TicketFlow.Reporting;

/// <summary>Report time buckets. Persisted nowhere; queries choose one.</summary>
public enum ReportGranularity
{
    /// <summary>Local clock hours (a repeated fall-back hour is two buckets; a skipped spring-forward hour none).</summary>
    Hour = 1,

    /// <summary>Local calendar days.</summary>
    Day = 2,

    /// <summary>Local ISO weeks starting on Monday.</summary>
    Week = 3,

    /// <summary>Local calendar months.</summary>
    Month = 4,
}

/// <summary>
/// Rollup cells and time-zone buckets. Facts roll up into 15-minute UTC cells; every current time zone's offsets and DST transitions
/// are multiples of 15 minutes, so local hours, days, weeks and months are exact unions of cells in any zone, and changing a
/// workspace's time zone never requires rebuilding rollups. Bucket starts are computed with <see cref="TimeZoneInfo"/>: a local
/// midnight skipped by a DST gap starts at the first valid local instant after it, an ambiguous local midnight at its first
/// occurrence.
/// </summary>
public static class ReportBuckets
{
    /// <summary>Most buckets one query may span.</summary>
    public const int MaximumBuckets = 1000;

    public static TimeSpan CellLength { get; } = TimeSpan.FromMinutes(15);

    private static readonly long CellTicks = TimeSpan.FromMinutes(15).Ticks;

    /// <summary>The start of the 15-minute UTC cell containing <paramref name="at"/>.</summary>
    public static DateTimeOffset CellStart(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % CellTicks, TimeSpan.Zero);

    /// <summary>The first cell boundary at or after <paramref name="at"/>.</summary>
    public static DateTimeOffset CellCeiling(DateTimeOffset at)
    {
        var start = CellStart(at);
        return start.UtcTicks == at.UtcTicks ? start : start.AddTicks(CellTicks);
    }

    public static bool IsCellAligned(DateTimeOffset at) => at.UtcTicks % CellTicks == 0;

    /// <summary>The UTC start of the <paramref name="granularity"/> bucket of <paramref name="zone"/> containing <paramref name="at"/>.</summary>
    public static DateTimeOffset BucketStart(TimeZoneInfo zone, DateTimeOffset at, ReportGranularity granularity)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (IsUtc(zone))
            return UtcBucketStart(at, granularity);
        switch (granularity)
        {
            case ReportGranularity.Hour:
                var cell = CellStart(at);
                for (var step = 0; step < 4; step++, cell = cell.AddTicks(-CellTicks))
                    if (Local(zone, cell).Minute == 0)
                        return cell;
                return new DateTimeOffset(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
            case ReportGranularity.Day:
            case ReportGranularity.Week:
            case ReportGranularity.Month:
                var date = Local(zone, at).Date;
                var start = LocalStart(zone, granularity switch
                {
                    ReportGranularity.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
                    ReportGranularity.Month => new DateTime(date.Year, date.Month, 1),
                    _ => date,
                });
                return start;
            default:
                throw new ArgumentOutOfRangeException(nameof(granularity));
        }
    }

    /// <summary>The start of the bucket after the one starting at <paramref name="start"/>.</summary>
    public static DateTimeOffset NextBucket(TimeZoneInfo zone, DateTimeOffset start, ReportGranularity granularity)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (IsUtc(zone))
            return UtcNextBucket(start, granularity);
        switch (granularity)
        {
            case ReportGranularity.Hour:
                var cell = CellStart(start).AddTicks(CellTicks);
                for (var step = 0; step < 4; step++, cell = cell.AddTicks(CellTicks))
                    if (Local(zone, cell).Minute == 0)
                        return cell;
                return new DateTimeOffset(start.UtcTicks - start.UtcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero).AddHours(1);
            case ReportGranularity.Day:
            case ReportGranularity.Week:
            case ReportGranularity.Month:
                // The bucket's local start date: a gap-shifted midnight still falls on the same local date.
                var date = Local(zone, start).Date;
                return LocalStart(zone, granularity switch
                {
                    ReportGranularity.Day => date.AddDays(1),
                    ReportGranularity.Week => date.AddDays(7),
                    _ => date.AddMonths(1),
                });
            default:
                throw new ArgumentOutOfRangeException(nameof(granularity));
        }
    }

    /// <summary>
    /// Bucket boundaries covering [<paramref name="from"/>, <paramref name="to"/>): the start of the bucket containing
    /// <paramref name="from"/>, every following bucket start, and the first bucket start at or after <paramref name="to"/>. At most
    /// <paramref name="maximumBuckets"/> buckets.
    /// </summary>
    /// <exception cref="ArgumentException">The range is empty or spans more buckets than allowed.</exception>
    public static IReadOnlyList<DateTimeOffset> Boundaries(TimeZoneInfo zone, DateTimeOffset from, DateTimeOffset to, ReportGranularity granularity, int maximumBuckets = MaximumBuckets)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (to <= from)
            throw new ArgumentException("The range must be non-empty.", nameof(to));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBuckets, 1);
        var isUtc = IsUtc(zone);
        var current = isUtc ? UtcBucketStart(from, granularity) : BucketStart(zone, from, granularity);
        var boundaries = new List<DateTimeOffset> { current };
        while (current < to)
        {
            if (boundaries.Count > maximumBuckets)
                throw new ArgumentException($"The range spans more than {maximumBuckets} buckets.", nameof(to));
            var next = isUtc ? UtcNextBucket(current, granularity) : NextBucket(zone, current, granularity);
            if (next <= current)
                throw new InvalidOperationException("Bucket boundaries must increase.");
            boundaries.Add(next);
            current = next;
        }
        return boundaries;
    }

    /// <summary>Two boundaries covering [<paramref name="from"/>, <paramref name="to"/>) aligned outward to whole cells.</summary>
    public static IReadOnlyList<DateTimeOffset> Range(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
            throw new ArgumentException("The range must be non-empty.", nameof(to));
        return [CellStart(from), CellCeiling(to)];
    }

    /// <summary>The index of the bucket containing <paramref name="at"/>, or -1 outside the boundaries.</summary>
    public static int IndexOf(IReadOnlyList<DateTimeOffset> boundaries, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (boundaries.Count < 2 || at < boundaries[0] || at >= boundaries[^1])
            return -1;
        int low = 0, high = boundaries.Count - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (boundaries[middle] <= at)
                low = middle;
            else
                high = middle;
        }
        return low;
    }

    /// <summary>The first instant of local <paramref name="date"/> in <paramref name="zone"/>.</summary>
    public static DateTimeOffset LocalStart(TimeZoneInfo zone, DateTime date)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        for (var step = 0; zone.IsInvalidTime(local) && step < 96; step++)
            local = local.AddMinutes(15);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local.Ticks - offset.Ticks, TimeSpan.Zero);
    }

    private static DateTime Local(TimeZoneInfo zone, DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).DateTime;

    private static bool IsUtc(TimeZoneInfo zone) =>
        ReferenceEquals(zone, TimeZoneInfo.Utc)
        || (zone.BaseUtcOffset == TimeSpan.Zero && !zone.SupportsDaylightSavingTime && zone.Id is "UTC" or "Etc/UTC");

    private static DateTimeOffset UtcBucketStart(DateTimeOffset at, ReportGranularity granularity)
    {
        var utcTicks = at.UtcTicks;
        switch (granularity)
        {
            case ReportGranularity.Hour:
                return new DateTimeOffset(utcTicks - utcTicks % TimeSpan.TicksPerHour, TimeSpan.Zero);
            case ReportGranularity.Day:
                return new DateTimeOffset(utcTicks - utcTicks % TimeSpan.TicksPerDay, TimeSpan.Zero);
            case ReportGranularity.Week:
                var date = new DateTime(utcTicks - utcTicks % TimeSpan.TicksPerDay, DateTimeKind.Utc);
                return new DateTimeOffset(date.AddDays(-(((int)date.DayOfWeek + 6) % 7)), TimeSpan.Zero);
            case ReportGranularity.Month:
                var utc = at.UtcDateTime;
                return new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
            default:
                throw new ArgumentOutOfRangeException(nameof(granularity));
        }
    }

    private static DateTimeOffset UtcNextBucket(DateTimeOffset start, ReportGranularity granularity)
    {
        var utcTicks = start.UtcTicks;
        switch (granularity)
        {
            case ReportGranularity.Hour:
                return new DateTimeOffset(utcTicks - utcTicks % TimeSpan.TicksPerHour + TimeSpan.TicksPerHour, TimeSpan.Zero);
            case ReportGranularity.Day:
                return new DateTimeOffset(utcTicks - utcTicks % TimeSpan.TicksPerDay + TimeSpan.TicksPerDay, TimeSpan.Zero);
            case ReportGranularity.Week:
                return new DateTimeOffset(utcTicks - utcTicks % TimeSpan.TicksPerDay + TimeSpan.TicksPerDay * 7, TimeSpan.Zero);
            case ReportGranularity.Month:
                var utc = start.UtcDateTime;
                return new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1), TimeSpan.Zero);
            default:
                throw new ArgumentOutOfRangeException(nameof(granularity));
        }
    }
}
