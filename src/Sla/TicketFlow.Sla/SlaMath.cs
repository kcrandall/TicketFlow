namespace TicketFlow.Sla;

/// <summary>Instant and duration normalization matching the abstractions (UTC, microsecond precision).</summary>
internal static class SlaMath
{
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

    public static TimeSpan Micros(TimeSpan value) => TimeSpan.FromTicks(value.Ticks - value.Ticks % 10);
}
