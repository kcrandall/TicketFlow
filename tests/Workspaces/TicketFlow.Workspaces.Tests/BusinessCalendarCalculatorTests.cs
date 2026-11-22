namespace TicketFlow.Workspaces.Tests;

[TestClass]
public sealed class BusinessCalendarCalculatorTests
{
    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    private static readonly DayOfWeek[] AllDays = Enum.GetValues<DayOfWeek>();

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static BusinessCalendarCalculator Calculator(string zone, IEnumerable<WeeklyBusinessHours> hours, IEnumerable<CalendarDateOverride>? overrides = null) =>
        new(new BusinessCalendar(new BusinessCalendarId("calendar"), "Calendar", zone, hours, overrides));

    private static IEnumerable<WeeklyBusinessHours> Every(IEnumerable<DayOfWeek> days, BusinessHours hours) => days.Select(day => new WeeklyBusinessHours(day, hours));

    private static BusinessCalendarCalculator NineToFive(string zone = "America/New_York", IEnumerable<CalendarDateOverride>? overrides = null) =>
        Calculator(zone, Every(Weekdays, BusinessHours.From(9, 0, 17, 0)), overrides);

    private static string Intervals(IEnumerable<BusinessInterval> intervals) =>
        string.Join(" ", intervals.Select(interval => $"{interval.Start:MM-dd HH:mm}/{interval.End:MM-dd HH:mm}"));

    [TestMethod]
    public void WeeklyHoursUseTheCalendarZone()
    {
        var calculator = NineToFive();
        // Monday 2026-03-02 09:00 EST is 14:00 UTC.
        Assert.IsFalse(calculator.IsOpen(Utc(2026, 3, 2, 13, 59)));
        Assert.IsTrue(calculator.IsOpen(Utc(2026, 3, 2, 14, 0)));
        Assert.IsTrue(calculator.IsOpen(Utc(2026, 3, 2, 21, 59)));
        Assert.IsFalse(calculator.IsOpen(Utc(2026, 3, 2, 22, 0)));
        Assert.IsFalse(calculator.IsOpen(Utc(2026, 2, 28, 15, 0)));
        Assert.AreEqual(Utc(2026, 3, 2, 14, 0), calculator.NextOpen(Utc(2026, 2, 28, 15, 0)));
        Assert.AreEqual(Utc(2026, 3, 2, 15, 30), calculator.NextOpen(Utc(2026, 3, 2, 15, 30)));
        Assert.AreEqual(Utc(2026, 3, 2, 22, 0), calculator.NextClose(Utc(2026, 3, 2, 15, 30)));
        Assert.AreEqual(Utc(2026, 2, 28, 15, 0), calculator.NextClose(Utc(2026, 2, 28, 15, 0)));
        Assert.AreEqual(TimeSpan.Zero, calculator.NextOpen(Utc(2026, 3, 2, 15, 30))!.Value.Offset);

        // Friday 16:00 EST + 2 business hours = Monday 10:00 EST.
        Assert.AreEqual(Utc(2026, 3, 2, 15, 0), calculator.AddBusinessTime(Utc(2026, 2, 27, 21, 0), TimeSpan.FromHours(2)));
        Assert.AreEqual(TimeSpan.FromHours(2), calculator.BusinessTimeBetween(Utc(2026, 2, 27, 21, 0), Utc(2026, 3, 2, 15, 0)));
        Assert.AreEqual(TimeSpan.FromHours(-2), calculator.BusinessTimeBetween(Utc(2026, 3, 2, 15, 0), Utc(2026, 2, 27, 21, 0)));
        Assert.AreEqual(TimeSpan.FromHours(40), calculator.BusinessTimeBetween(Utc(2026, 3, 1, 0), Utc(2026, 3, 8, 0)));
        // Ending exactly at close returns the close instant, not the next opening.
        Assert.AreEqual(Utc(2026, 3, 2, 22, 0), calculator.AddBusinessTime(Utc(2026, 3, 2, 14, 0), TimeSpan.FromHours(8)));
        Assert.AreEqual(Utc(2026, 2, 28, 15, 0), calculator.AddBusinessTime(Utc(2026, 2, 28, 15, 0), TimeSpan.Zero));
    }

    [TestMethod]
    public void SpringForwardShortensTheLocalDay()
    {
        // US DST starts Sunday 2026-03-08 at 02:00 EST (07:00 UTC); 02:00-02:59 local does not exist.
        var allDaySunday = Calculator("America/New_York", Every([DayOfWeek.Sunday], BusinessHours.From(0, 0, 24, 0)));
        Assert.AreEqual(TimeSpan.FromHours(23), allDaySunday.BusinessTimeBetween(Utc(2026, 3, 7, 0), Utc(2026, 3, 10, 0)));
        Assert.AreEqual("03-08 05:00/03-09 04:00", Intervals(allDaySunday.GetOpenIntervals(Utc(2026, 3, 7, 0), Utc(2026, 3, 10, 0))));
        Assert.AreEqual(TimeSpan.FromHours(24), allDaySunday.BusinessTimeBetween(Utc(2026, 3, 14, 0), Utc(2026, 3, 17, 0)));

        // Only the nonexistent hour is open: never open that Sunday.
        var gapOnly = Calculator("America/New_York", Every([DayOfWeek.Sunday], BusinessHours.From(2, 0, 3, 0)));
        Assert.AreEqual(TimeSpan.Zero, gapOnly.BusinessTimeBetween(Utc(2026, 3, 8, 0), Utc(2026, 3, 9, 0)));
        Assert.AreEqual(Utc(2026, 3, 15, 6, 0), gapOnly.NextOpen(Utc(2026, 3, 8, 0)));
        Assert.AreEqual(Utc(2026, 3, 1, 7, 0), gapOnly.NextOpen(Utc(2026, 3, 1, 0)));

        // 01:00 EST (06:00 UTC) to 04:00 EDT (08:00 UTC) is two real hours.
        var straddle = Calculator("America/New_York", Every([DayOfWeek.Sunday], BusinessHours.From(1, 0, 4, 0)));
        Assert.AreEqual("03-08 06:00/03-08 08:00", Intervals(straddle.GetOpenIntervals(Utc(2026, 3, 8, 0), Utc(2026, 3, 9, 0))));
        Assert.IsTrue(straddle.IsOpen(Utc(2026, 3, 8, 6, 59)));
        Assert.IsTrue(straddle.IsOpen(Utc(2026, 3, 8, 7, 0)));
        Assert.AreEqual(Utc(2026, 3, 8, 7, 30), straddle.AddBusinessTime(Utc(2026, 3, 8, 6, 0), TimeSpan.FromMinutes(90)));
    }

    [TestMethod]
    public void FallBackRepeatsLocalTimeAndBothPassesAreOpen()
    {
        // US DST ends Sunday 2026-11-01 at 02:00 EDT (06:00 UTC); 01:00-01:59 local happens twice.
        var allDaySunday = Calculator("America/New_York", Every([DayOfWeek.Sunday], BusinessHours.From(0, 0, 24, 0)));
        Assert.AreEqual(TimeSpan.FromHours(25), allDaySunday.BusinessTimeBetween(Utc(2026, 10, 31, 0), Utc(2026, 11, 3, 0)));
        Assert.AreEqual("11-01 04:00/11-02 05:00", Intervals(allDaySunday.GetOpenIntervals(Utc(2026, 10, 31, 0), Utc(2026, 11, 3, 0))));

        var repeatedHour = Calculator("America/New_York", Every([DayOfWeek.Sunday], BusinessHours.From(1, 0, 2, 0)));
        Assert.AreEqual("11-01 05:00/11-01 07:00", Intervals(repeatedHour.GetOpenIntervals(Utc(2026, 11, 1, 0), Utc(2026, 11, 2, 0))));
        Assert.AreEqual(TimeSpan.FromHours(2), repeatedHour.BusinessTimeBetween(Utc(2026, 11, 1, 0), Utc(2026, 11, 2, 0)));

        // A span ending inside the repeated hour is open in both passes and closed in between.
        var halfHour = Calculator("America/New_York", Every([DayOfWeek.Sunday], BusinessHours.From(1, 0, 1, 30)));
        Assert.AreEqual("11-01 05:00/11-01 05:30 11-01 06:00/11-01 06:30", Intervals(halfHour.GetOpenIntervals(Utc(2026, 11, 1, 0), Utc(2026, 11, 2, 0))));
        Assert.IsFalse(halfHour.IsOpen(Utc(2026, 11, 1, 5, 45)));
        Assert.AreEqual(Utc(2026, 11, 1, 5, 30), halfHour.NextClose(Utc(2026, 11, 1, 5, 0)));
        Assert.AreEqual(Utc(2026, 11, 1, 6, 0), halfHour.NextOpen(Utc(2026, 11, 1, 5, 30)));
        Assert.AreEqual(Utc(2026, 11, 1, 6, 15), halfHour.AddBusinessTime(Utc(2026, 11, 1, 5, 0), TimeSpan.FromMinutes(45)));
    }

    [TestMethod]
    public void OvernightShiftsJoinAcrossMidnightIncludingDstNights()
    {
        var night = Calculator("America/New_York",
            [new(DayOfWeek.Saturday, BusinessHours.From(22, 0, 24, 0)), new(DayOfWeek.Sunday, BusinessHours.From(0, 0, 6, 0))]);
        // Saturday 2026-10-24 22:00 EDT = 02:00 UTC Sunday; Sunday 06:00 EDT = 10:00 UTC.
        Assert.AreEqual("10-25 02:00/10-25 10:00", Intervals(night.GetOpenIntervals(Utc(2026, 10, 24, 0), Utc(2026, 10, 26, 0))));
        // Fall-back night: Saturday 22:00 EDT (02:00 UTC) to Sunday 06:00 EST (11:00 UTC) is nine hours.
        Assert.AreEqual("11-01 02:00/11-01 11:00", Intervals(night.GetOpenIntervals(Utc(2026, 10, 31, 0), Utc(2026, 11, 2, 0))));
        Assert.AreEqual(TimeSpan.FromHours(9), night.BusinessTimeBetween(Utc(2026, 10, 31, 0), Utc(2026, 11, 2, 0)));
        // Spring-forward night: Saturday 22:00 EST (03:00 UTC) to Sunday 06:00 EDT (10:00 UTC) is seven hours.
        Assert.AreEqual(TimeSpan.FromHours(7), night.BusinessTimeBetween(Utc(2026, 3, 7, 0), Utc(2026, 3, 9, 0)));
        Assert.AreEqual(Utc(2026, 3, 8, 10, 0), night.NextClose(Utc(2026, 3, 8, 4, 0)));
    }

    [TestMethod]
    public void HolidaysAndDateOverridesReplaceWeeklyHours()
    {
        var calculator = NineToFive(overrides:
        [
            new CalendarDateOverride(new(2026, 12, 24), [BusinessHours.From(9, 0, 12, 0)], "Christmas Eve"),
            CalendarDateOverride.Holiday(new(2026, 12, 25), "Christmas"),
            new CalendarDateOverride(new(2026, 12, 27), [BusinessHours.From(10, 0, 14, 0)], "Year-end Sunday"),
        ]);
        // Thursday 24th 11:00 EST + 3h: 1h on the 24th, closed on the 25th, 4h on Sunday the 27th covers the rest.
        Assert.AreEqual(Utc(2026, 12, 27, 17, 0), calculator.AddBusinessTime(Utc(2026, 12, 24, 16, 0), TimeSpan.FromHours(3)));
        Assert.IsFalse(calculator.IsOpen(Utc(2026, 12, 24, 17, 30)));
        Assert.IsFalse(calculator.IsOpen(Utc(2026, 12, 25, 15, 0)));
        Assert.AreEqual(Utc(2026, 12, 27, 15, 0), calculator.NextOpen(Utc(2026, 12, 24, 17, 0)));
        Assert.AreEqual(TimeSpan.FromHours(3 + 4 + 8), calculator.BusinessTimeBetween(Utc(2026, 12, 24, 0), Utc(2026, 12, 29, 0)));

        var holidaysOnly = NineToFive(overrides: Enumerable.Range(0, 14).Select(day => CalendarDateOverride.Holiday(new DateOnly(2026, 7, 1).AddDays(day))));
        Assert.AreEqual(Utc(2026, 7, 15, 13, 0), holidaysOnly.NextOpen(Utc(2026, 7, 1, 0)));
    }

    [TestMethod]
    public void HalfHourDstShiftsAreHandled()
    {
        // Lord Howe Island: +11 in summer, +10:30 in winter. DST ends Sunday 2026-04-05 at 02:00 (+11) → 01:30 (+10:30).
        var ends = Calculator("Australia/Lord_Howe", Every([DayOfWeek.Sunday], BusinessHours.From(1, 0, 2, 0)));
        Assert.AreEqual("04-04 14:00/04-04 15:30", Intervals(ends.GetOpenIntervals(Utc(2026, 4, 4, 0), Utc(2026, 4, 6, 0))));
        // DST starts Sunday 2026-10-04 at 02:00 (+10:30) → 02:30 (+11): only 02:30-03:00 exists.
        var starts = Calculator("Australia/Lord_Howe", Every([DayOfWeek.Sunday], BusinessHours.From(2, 0, 3, 0)));
        Assert.AreEqual("10-03 15:30/10-03 16:00", Intervals(starts.GetOpenIntervals(Utc(2026, 10, 3, 0), Utc(2026, 10, 5, 0))));
    }

    [TestMethod]
    public void EuropeanTransitionsAtOneUtc()
    {
        // London: 2026-03-29 01:00 GMT → 02:00 BST; 2026-10-25 02:00 BST → 01:00 GMT.
        var calculator = Calculator("Europe/London", Every(AllDays, BusinessHours.From(0, 0, 24, 0)));
        Assert.AreEqual(TimeSpan.FromHours(23), calculator.BusinessTimeBetween(Utc(2026, 3, 29, 0), Utc(2026, 3, 29, 23)));
        var mornings = Calculator("Europe/London", Every([DayOfWeek.Sunday], BusinessHours.From(0, 30, 2, 30)));
        Assert.AreEqual("03-29 00:30/03-29 01:30", Intervals(mornings.GetOpenIntervals(Utc(2026, 3, 29, 0), Utc(2026, 3, 30, 0))));
        Assert.AreEqual("10-24 23:30/10-25 02:30", Intervals(mornings.GetOpenIntervals(Utc(2026, 10, 24, 0), Utc(2026, 10, 26, 0))));
    }

    [TestMethod]
    public void SkippedLocalDatesAreNeverOpen()
    {
        // Samoa moved across the date line: 2011-12-29 23:59:59 (-10) was followed by 2011-12-31 00:00 (+14).
        var calculator = Calculator("Pacific/Apia", Every(AllDays, BusinessHours.From(9, 0, 17, 0)));
        var start = Utc(2011, 12, 29, 10, 0);
        var end = Utc(2011, 12, 31, 10, 0);
        Assert.AreEqual(TimeSpan.FromHours(16), calculator.BusinessTimeBetween(start, end));
        Assert.AreEqual("12-29 19:00/12-30 03:00 12-30 19:00/12-31 03:00", Intervals(calculator.GetOpenIntervals(start, end)));
    }

    /// <summary>A custom zone whose transitions happen at local midnight, the hardest case for date-based schedules.</summary>
    private static TimeZoneInfo MidnightZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2100, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test/Midnight", TimeSpan.Zero, "Midnight", "Midnight Standard", "Midnight Daylight", [rule]);
    }

    [TestMethod]
    public void MidnightTransitionsMoveTimeBetweenLocalDates()
    {
        var zone = MidnightZone();
        // Sanity: 2026-03-01 00:00 local standard becomes 01:00 daylight; 2026-11-01 00:00 daylight becomes Saturday 23:00 standard.
        Assert.AreEqual(TimeSpan.Zero, zone.GetUtcOffset(Utc(2026, 2, 28, 23, 59)));
        Assert.AreEqual(TimeSpan.FromHours(1), zone.GetUtcOffset(Utc(2026, 3, 1, 0, 0)));
        Assert.AreEqual(TimeSpan.FromHours(1), zone.GetUtcOffset(Utc(2026, 10, 31, 22, 59)));
        Assert.AreEqual(TimeSpan.Zero, zone.GetUtcOffset(Utc(2026, 10, 31, 23, 0)));

        var calendar = new BusinessCalendar(new BusinessCalendarId("midnight"), "Midnight", "UTC",
            [new(DayOfWeek.Saturday, BusinessHours.From(23, 0, 24, 0)), new(DayOfWeek.Sunday, BusinessHours.From(0, 0, 2, 0))]);
        var calculator = new BusinessCalendarCalculator(calendar, zone);
        // Spring: Saturday 23:00 (+0) then Sunday 01:00-02:00 (+1); Sunday 00:00-00:59 does not exist.
        Assert.AreEqual("02-28 23:00/03-01 01:00", Intervals(calculator.GetOpenIntervals(Utc(2026, 2, 28, 0), Utc(2026, 3, 2, 0))));
        // Fall: Saturday 23:00 happens twice (22:00 and 23:00 UTC), then Sunday 00:00-02:00 (+0).
        Assert.AreEqual("10-31 22:00/11-01 02:00", Intervals(calculator.GetOpenIntervals(Utc(2026, 10, 31, 0), Utc(2026, 11, 2, 0))));
        Assert.AreEqual(TimeSpan.FromHours(4), calculator.BusinessTimeBetween(Utc(2026, 10, 31, 0), Utc(2026, 11, 2, 0)));

        var saturdayOnly = new BusinessCalendarCalculator(new BusinessCalendar(new BusinessCalendarId("sat"), "Sat", "UTC",
            [new(DayOfWeek.Saturday, BusinessHours.From(23, 0, 24, 0))]), zone);
        Assert.AreEqual("10-31 22:00/11-01 00:00", Intervals(saturdayOnly.GetOpenIntervals(Utc(2026, 10, 31, 0), Utc(2026, 11, 2, 0))));
    }

    [TestMethod]
    public void AlwaysAndNeverOpenCalendars()
    {
        var always = Calculator("America/New_York", Every(AllDays, BusinessHours.From(0, 0, 24, 0)));
        Assert.IsNull(always.NextClose(Utc(2026, 1, 1, 0)));
        // Business time is real elapsed time, including across DST changes.
        Assert.AreEqual(Utc(2026, 3, 10, 0), always.AddBusinessTime(Utc(2026, 3, 5, 0), TimeSpan.FromDays(5)));
        Assert.AreEqual(TimeSpan.FromDays(365), always.BusinessTimeBetween(Utc(2026, 1, 1, 0), Utc(2027, 1, 1, 0)));
        Assert.HasCount(1, always.GetOpenIntervals(Utc(2026, 1, 1, 0), Utc(2027, 1, 1, 0)));

        var never = Calculator("America/New_York", []);
        Assert.IsFalse(never.IsOpen(Utc(2026, 1, 1, 0)));
        Assert.IsNull(never.NextOpen(Utc(2026, 1, 1, 0)));
        Assert.IsNull(never.AddBusinessTime(Utc(2026, 1, 1, 0), TimeSpan.FromMinutes(1)));
        Assert.AreEqual(Utc(2026, 1, 1, 0), never.NextClose(Utc(2026, 1, 1, 0)));
        Assert.AreEqual(TimeSpan.Zero, never.BusinessTimeBetween(Utc(2026, 1, 1, 0), Utc(2030, 1, 1, 0)));
    }

    [TestMethod]
    public void InputsAreBounded()
    {
        var calculator = NineToFive();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.AddBusinessTime(Utc(2026, 1, 1, 0), TimeSpan.FromTicks(-1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.AddBusinessTime(Utc(2026, 1, 1, 0), BusinessCalendarCalculator.MaximumHorizon + TimeSpan.FromTicks(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.IsOpen(DateTimeOffset.MinValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.NextOpen(DateTimeOffset.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.BusinessTimeBetween(Utc(2000, 1, 1, 0), Utc(2020, 1, 1, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.GetOpenIntervals(Utc(2020, 1, 2, 0), Utc(2020, 1, 1, 0)));
        Assert.ThrowsExactly<TimeZoneNotFoundException>(() => new BusinessCalendarCalculator(new BusinessCalendar(new BusinessCalendarId("x"), "X", "Nowhere/Atlantis")));
        Assert.IsNotNull(calculator.NextOpen(BusinessCalendarCalculator.MaximumInstant));
        // Offsets on inputs are irrelevant: the same instant gives the same answer.
        Assert.AreEqual(calculator.IsOpen(Utc(2026, 3, 2, 14, 0)), calculator.IsOpen(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.FromHours(-5))));
    }

    [TestMethod]
    [DataRow("America/New_York", 11)]
    [DataRow("Europe/London", 23)]
    [DataRow("Australia/Lord_Howe", 37)]
    [DataRow("Asia/Kolkata", 41)]
    public void AddAndBetweenAreInversesOverRandomizedInstants(string zone, int seed)
    {
        var calculator = Calculator(zone,
            [.. Every(Weekdays, BusinessHours.From(8, 30, 12, 0)), .. Every(Weekdays, BusinessHours.From(13, 0, 17, 45)),
             new(DayOfWeek.Saturday, BusinessHours.From(22, 0, 24, 0)), new(DayOfWeek.Sunday, BusinessHours.From(0, 0, 1, 30)),
             new(DayOfWeek.Sunday, BusinessHours.From(1, 45, 3, 15))],
            [CalendarDateOverride.Holiday(new(2026, 3, 9)), CalendarDateOverride.Holiday(new(2026, 11, 2)), new CalendarDateOverride(new(2026, 4, 5), [BusinessHours.From(0, 0, 24, 0)])]);
        var random = new Random(seed);
        var origin = Utc(2026, 1, 1, 0);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var start = origin.AddMinutes(random.Next(0, 366 * 24 * 60)).AddSeconds(random.Next(0, 60));
            var duration = TimeSpan.FromMinutes(random.Next(0, 60 * 60)) + TimeSpan.FromTicks(random.Next(0, 1000));
            var end = calculator.AddBusinessTime(start, duration)!.Value;
            Assert.AreEqual(duration, calculator.BusinessTimeBetween(start, end), $"start {start:O} duration {duration}");
            if (duration > TimeSpan.Zero)
            {
                Assert.IsLessThan(duration, calculator.BusinessTimeBetween(start, end.AddTicks(-1)));
                Assert.IsTrue(calculator.IsOpen(end.AddTicks(-1)));
            }
            var next = calculator.NextOpen(start)!.Value;
            Assert.IsGreaterThanOrEqualTo(start, next);
            Assert.IsTrue(calculator.IsOpen(next));
            Assert.AreEqual(TimeSpan.Zero, calculator.BusinessTimeBetween(start, next));
            var close = calculator.NextClose(start)!.Value;
            Assert.IsFalse(calculator.IsOpen(close));
            Assert.AreEqual(close - start, calculator.BusinessTimeBetween(start, close));
        }
    }

    [TestMethod]
    public void IntervalsAgreeWithIsOpenAroundTransitions()
    {
        foreach (var (zone, from) in new[] { ("America/New_York", Utc(2026, 3, 6, 0)), ("America/New_York", Utc(2026, 10, 30, 0)), ("Australia/Lord_Howe", Utc(2026, 4, 2, 0)), ("Europe/London", Utc(2026, 10, 23, 0)) })
        {
            var calculator = Calculator(zone,
                [.. Every(AllDays, BusinessHours.From(0, 45, 1, 15)), .. Every(AllDays, BusinessHours.From(1, 50, 2, 40)), .. Every(AllDays, BusinessHours.From(23, 0, 24, 0))]);
            var to = from.AddDays(4);
            var intervals = calculator.GetOpenIntervals(from, to);
            for (var instant = from; instant < to; instant = instant.AddMinutes(5))
                Assert.AreEqual(intervals.Any(interval => interval.Start <= instant && instant < interval.End), calculator.IsOpen(instant), $"{zone} {instant:O}");
            Assert.AreEqual(TimeSpan.FromTicks(intervals.Sum(interval => interval.Duration.Ticks)), calculator.BusinessTimeBetween(from, to));
            for (var index = 1; index < intervals.Count; index++)
                Assert.IsLessThan(intervals[index].Start, intervals[index - 1].End);
        }
    }
}
