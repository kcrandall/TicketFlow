using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>
/// The time basis SLA clocks are measured in: elapsed wall-clock time (<see cref="Calendar"/>) or the open time of a business
/// calendar through <see cref="BusinessCalendarCalculator"/> (<see cref="Business"/>). All SLA business-time math goes through it.
/// </summary>
public sealed class SlaClockTime
{
    private readonly BusinessCalendarCalculator? calculator;

    private SlaClockTime(BusinessCalendarCalculator? calculator) => this.calculator = calculator;

    /// <summary>Elapsed wall-clock time.</summary>
    public static SlaClockTime Calendar { get; } = new(null);

    public static SlaClockTime Business(BusinessCalendarCalculator calculator) => new(calculator ?? throw new ArgumentNullException(nameof(calculator)));

    /// <summary>The calculator, or <see langword="null"/> for calendar time.</summary>
    public BusinessCalendarCalculator? Calculator => calculator;

    /// <summary>Measured time in [<paramref name="from"/>, <paramref name="to"/>); zero when <paramref name="to"/> precedes <paramref name="from"/>.</summary>
    public TimeSpan Between(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
            return TimeSpan.Zero;
        return calculator is null ? to - from : calculator.BusinessTimeBetween(from, to);
    }

    /// <summary>The earliest instant at which <paramref name="duration"/> of measured time has accumulated since <paramref name="from"/>, or <see langword="null"/> beyond the calculator horizon.</summary>
    public DateTimeOffset? Add(DateTimeOffset from, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return from;
        return calculator is null ? from + duration : calculator.AddBusinessTime(from, duration);
    }
}
