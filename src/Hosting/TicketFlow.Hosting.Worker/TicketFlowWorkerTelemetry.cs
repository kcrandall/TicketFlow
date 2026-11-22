using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TicketFlow.Hosting.Worker;

/// <summary>
/// Telemetry names. Tags carry loop names and outcomes (<c>success</c>, <c>partial</c>, <c>failure</c>) only — never identifiers, bodies or
/// exception messages. Add <see cref="ActivitySourceName"/> and <see cref="MeterName"/> to OpenTelemetry.
/// </summary>
public static class TicketFlowWorkerTelemetry
{
    public const string ActivitySourceName = "TicketFlow.Hosting.Worker";
    public const string MeterName = "TicketFlow.Hosting.Worker";

    internal static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("ticketflow.worker.runs", "runs", "Worker loop runs by outcome.");
    private static readonly Counter<long> Items = Meter.CreateCounter<long>("ticketflow.worker.items", "items", "Items processed by worker loops.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("ticketflow.worker.run.duration", "ms", "Worker loop run duration.");

    internal static void Record(string loop, string outcome, int items, long started)
    {
        var loopTag = new KeyValuePair<string, object?>("loop", loop);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);
        Runs.Add(1, loopTag, outcomeTag);
        if (items > 0)
            Items.Add(items, loopTag);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, loopTag, outcomeTag);
    }
}
