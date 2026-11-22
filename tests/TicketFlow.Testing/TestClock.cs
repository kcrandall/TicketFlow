using Microsoft.Extensions.Time.Testing;

namespace TicketFlow.Testing;

/// <summary>Deterministic <see cref="TimeProvider"/> instances. Advance time explicitly with <see cref="FakeTimeProvider.Advance"/>.</summary>
public static class TestClock
{
    /// <summary>A fixed, recognizable UTC instant used as the default test start time.</summary>
    public static DateTimeOffset DefaultStart { get; } = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Create(DateTimeOffset? start = null) => new(start ?? DefaultStart);
}
