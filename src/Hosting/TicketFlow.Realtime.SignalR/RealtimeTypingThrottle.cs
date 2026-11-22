using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace TicketFlow.Realtime.SignalR;

/// <summary>
/// Server-side typing throttle per connection and conversation: at most one typing event per
/// <see cref="TicketFlowSignalROptions.TypingThrottleInterval"/>, except that a stop after a start is always forwarded once. Throttled
/// calls succeed without publishing.
/// </summary>
public sealed class RealtimeTypingThrottle(IOptions<TicketFlowSignalROptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (bool Typing, DateTimeOffset At)>> connections = new(StringComparer.Ordinal);
    private readonly TimeSpan interval = (options ?? throw new ArgumentNullException(nameof(options))).Value.TypingThrottleInterval;
    private readonly TimeProvider time = time ?? throw new ArgumentNullException(nameof(time));

    private TimeSpan Interval => interval >= TimeSpan.Zero && interval <= TimeSpan.FromMinutes(1)
        ? interval
        : throw new InvalidOperationException("TicketFlowSignalROptions.TypingThrottleInterval must be 0 to 1 minute.");

    /// <summary>True when the typing change should be published (and records it).</summary>
    public bool Allow(string connectionId, string conversation, bool typing)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(conversation);
        var perConnection = connections.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, (bool, DateTimeOffset)>(StringComparer.Ordinal));
        var now = time.GetUtcNow();
        lock (perConnection)
        {
            if (perConnection.TryGetValue(conversation, out var last) && now - last.At < Interval && !(last.Typing && !typing))
                return false;
            if (perConnection.Count >= 256 && !perConnection.ContainsKey(conversation))
                return false;
            perConnection[conversation] = (typing, now);
            return true;
        }
    }

    public void Forget(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        connections.TryRemove(connectionId, out _);
    }
}
