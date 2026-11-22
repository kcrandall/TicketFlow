namespace TicketFlow.Reporting;

/// <summary>
/// Persisted report metric discriminators. Never renumber. Count metrics carry the value 1 per fact; duration metrics carry
/// microseconds and feed the <see cref="ReportHistogram"/>.
/// </summary>
public enum ReportMetric
{
    /// <summary>A conversation was created (count).</summary>
    ConversationCreated = 1,

    /// <summary>A conversation moved into Resolved or Closed from an unresolved status (count).</summary>
    ConversationResolved = 2,

    /// <summary>A resolved conversation moved back to Open, Pending or Snoozed (count).</summary>
    ConversationReopened = 3,

    /// <summary>An incoming message from the contact (count).</summary>
    MessageReceived = 4,

    /// <summary>An outgoing message by any principal (count; the agent dimension is set for user senders only).</summary>
    MessageSent = 5,

    /// <summary>Calendar time from the contact starting to wait until the first user reply.</summary>
    FirstResponseTime = 6,

    /// <summary><see cref="FirstResponseTime"/> measured in the inbox's business hours.</summary>
    FirstResponseBusinessTime = 7,

    /// <summary>Calendar time from the oldest unanswered incoming message until each user reply.</summary>
    ResponseTime = 8,

    /// <summary><see cref="ResponseTime"/> measured in the inbox's business hours.</summary>
    ResponseBusinessTime = 9,

    /// <summary>Calendar time from creation to the first resolution.</summary>
    ResolutionTime = 10,

    /// <summary><see cref="ResolutionTime"/> measured in the inbox's business hours.</summary>
    ResolutionBusinessTime = 11,

    /// <summary>The conversation was assigned to an agent (count; agent = new assignee).</summary>
    Assignment = 12,

    /// <summary>The conversation moved from one agent to another (count; agent = new assignee).</summary>
    Handoff = 13,

    /// <summary>The conversation moved from one team to another (count; team = new team).</summary>
    TeamTransfer = 14,

    /// <summary>An SLA metric was met within its target (authentic <c>sla.met</c>, not breached).</summary>
    SlaAchieved = 15,

    /// <summary>An SLA metric breached its target (authentic <c>sla.breached</c>, at the breach instant).</summary>
    SlaBreached = 16,
}

public static class ReportMetrics
{
    /// <summary>Every metric in numeric order.</summary>
    public static IReadOnlyList<ReportMetric> All { get; } = [.. Enum.GetValues<ReportMetric>().Order()];

    /// <summary>True for metrics whose value is a duration in microseconds.</summary>
    public static bool IsDuration(ReportMetric metric) => metric switch
    {
        ReportMetric.FirstResponseTime or ReportMetric.FirstResponseBusinessTime or ReportMetric.ResponseTime or ReportMetric.ResponseBusinessTime
            or ReportMetric.ResolutionTime or ReportMetric.ResolutionBusinessTime => true,
        _ => false,
    };

    public static ReportMetric Require(ReportMetric metric, string parameterName) =>
        Enum.IsDefined(metric) ? metric : throw new ArgumentOutOfRangeException(parameterName, "Unknown report metric.");

    /// <summary>1-16 distinct defined metrics in numeric order.</summary>
    public static IReadOnlyList<ReportMetric> Set(IEnumerable<ReportMetric> metrics, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(metrics, parameterName);
        IReadOnlyList<ReportMetric> snapshot = [.. metrics.Select(metric => Require(metric, parameterName)).Distinct().Order()];
        if (snapshot.Count is 0 or > 16)
            throw new ArgumentException("Between 1 and 16 distinct metrics are required.", parameterName);
        return snapshot;
    }
}
