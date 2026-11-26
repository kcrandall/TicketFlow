using System.Globalization;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Reporting;

/// <summary>Report grouping dimensions.</summary>
public enum ReportDimension
{
    None = 0,
    Inbox = 1,
    Team = 2,
    Agent = 3,
    Channel = 4,
    Priority = 5,
    Label = 6,
}

/// <summary>
/// Optional exact-match restrictions on report dimensions. A label restriction (or grouping by label) counts each fact once per
/// matching label it carried.
/// </summary>
public sealed record ReportFilter
{
    public ReportFilter(InboxId? inbox = null, TeamId? team = null, UserId? agent = null, string? channel = null, ConversationPriority? priority = null, LabelId? label = null)
    {
        if (inbox == default(InboxId) || team == default(TeamId) || agent == default(UserId) || label == default(LabelId))
            throw new ArgumentException("Filter identifiers must be initialized when supplied.", inbox == default(InboxId) ? nameof(inbox) : team == default(TeamId) ? nameof(team) : agent == default(UserId) ? nameof(agent) : nameof(label));
        if (priority is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(priority));
        Inbox = inbox;
        Team = team;
        Agent = agent;
        Channel = channel is null ? null : ReportText.RequireToken(channel, ReportDimensions.MaximumChannelLength, nameof(channel));
        Priority = priority;
        Label = label;
    }

    public static ReportFilter None { get; } = new();

    public InboxId? Inbox { get; }
    public TeamId? Team { get; }
    public UserId? Agent { get; }
    public string? Channel { get; }
    public ConversationPriority? Priority { get; }
    public LabelId? Label { get; }

    /// <summary>True when the dimensions match every supplied restriction (labels: the label is carried).</summary>
    public bool Matches(ReportDimensions dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        return (Inbox is null || dimensions.Inbox == Inbox) && (Team is null || dimensions.Team == Team) && (Agent is null || dimensions.Agent == Agent)
            && (Channel is null || string.Equals(dimensions.Channel, Channel, StringComparison.Ordinal)) && (Priority is null || dimensions.Priority == Priority)
            && (Label is null || dimensions.Labels.Contains(Label.Value));
    }
}

/// <summary>Grouping keys: identifiers verbatim, the priority as its persisted number, <see langword="null"/> when absent.</summary>
public static class ReportGroups
{
    public static string? Key(ReportDimension dimension, InboxId inbox, TeamId? team, UserId? agent, string? channel, ConversationPriority priority, LabelId? label) => dimension switch
    {
        ReportDimension.None => null,
        ReportDimension.Inbox => inbox.Value,
        ReportDimension.Team => team?.Value,
        ReportDimension.Agent => agent?.Value,
        ReportDimension.Channel => channel,
        ReportDimension.Priority => priority switch
        {
            ConversationPriority.None => "1",
            ConversationPriority.Low => "2",
            ConversationPriority.Medium => "3",
            ConversationPriority.High => "4",
            ConversationPriority.Urgent => "5",
            _ => ((int)priority).ToString(CultureInfo.InvariantCulture),
        },
        ReportDimension.Label => label?.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    public static ReportDimension Require(ReportDimension dimension, string parameterName) =>
        Enum.IsDefined(dimension) ? dimension : throw new ArgumentOutOfRangeException(parameterName);
}

/// <summary>
/// A store aggregate over rollup cells (label groupings and filters: over facts, see <see cref="UsesLabelRows"/>): cells with <c>Boundaries[0] ≤ cell start &lt; Boundaries[^1]</c> of the requested metrics,
/// matching <see cref="Filter"/>, grouped by the bucket index (cell start between consecutive boundaries), the
/// <see cref="GroupBy"/> key and the metric — and, when <see cref="Histograms"/> is set, by histogram bin for duration metrics.
/// Boundaries are strictly increasing, cell aligned (2-1001 of them).
/// </summary>
public sealed record ReportAggregateQuery
{
    public const int MaximumRows = 100_000;

    public ReportAggregateQuery(IEnumerable<DateTimeOffset> boundaries, IEnumerable<ReportMetric> metrics, ReportFilter? filter = null, ReportDimension groupBy = ReportDimension.None,
        bool histograms = false)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        var list = new List<DateTimeOffset>();
        foreach (var boundary in boundaries)
        {
            var instant = ReportText.Instant(boundary);
            if (!ReportBuckets.IsCellAligned(instant) || (list.Count > 0 && list[^1] >= instant))
                throw new ArgumentException("Boundaries must be 2-1001 strictly increasing, 15-minute aligned instants.", nameof(boundaries));
            list.Add(instant);
            if (list.Count > ReportBuckets.MaximumBuckets + 1)
                throw new ArgumentException("Boundaries must be 2-1001 strictly increasing, 15-minute aligned instants.", nameof(boundaries));
        }
        if (list.Count < 2)
            throw new ArgumentException("Boundaries must be 2-1001 strictly increasing, 15-minute aligned instants.", nameof(boundaries));
        Boundaries = list;
        Metrics = ReportMetrics.Set(metrics, nameof(metrics));
        Filter = filter ?? ReportFilter.None;
        GroupBy = ReportGroups.Require(groupBy, nameof(groupBy));
        Histograms = histograms;
    }

    public IReadOnlyList<DateTimeOffset> Boundaries { get; }
    public IReadOnlyList<ReportMetric> Metrics { get; }
    public ReportFilter Filter { get; }
    public ReportDimension GroupBy { get; }
    public bool Histograms { get; }

    public DateTimeOffset From => Boundaries[0];
    public DateTimeOffset To => Boundaries[^1];

    /// <summary>The aggregate is computed from facts, once per carried label (labels are not rolled up).</summary>
    public bool UsesLabelRows => GroupBy == ReportDimension.Label || Filter.Label is not null;
}

/// <summary>
/// One aggregate row: fact count, value sum (microseconds for durations; equal to the count for count metrics) and the minimum and
/// maximum value. <see cref="Bin"/> is the histogram bin for duration metrics when histograms were requested, otherwise 0.
/// </summary>
public sealed record ReportAggregateRow(int Bucket, string? Group, ReportMetric Metric, int Bin, long Count, long Sum, long Minimum, long Maximum);

/// <summary>Rows ordered by (bucket, group ordinal with nulls first, metric, bin). <see cref="Truncated"/>: more than <see cref="ReportAggregateQuery.MaximumRows"/> rows existed.</summary>
public sealed record ReportAggregateResult(IReadOnlyList<ReportAggregateRow> Rows, bool Truncated);

/// <summary>Keyset cursor over facts ordered by (occurred at, source id as lowercase hex, metric).</summary>
public sealed record ReportFactCursor(DateTimeOffset OccurredAt, Guid SourceId, ReportMetric Metric);

/// <summary>
/// A bounded fact listing: facts with <see cref="From"/> ≤ occurred at &lt; <see cref="To"/> (at most 366 days) matching
/// <see cref="Filter"/>, oldest first, at most <see cref="Limit"/> (1-1000) per page.
/// </summary>
public sealed record ReportFactQuery
{
    public const int MaximumLimit = 1000;
    public static readonly TimeSpan MaximumRange = TimeSpan.FromDays(366);

    public ReportFactQuery(DateTimeOffset from, DateTimeOffset to, ReportFilter? filter = null, int limit = 500, ReportFactCursor? after = null)
    {
        if (to <= from || to - from > MaximumRange)
            throw new ArgumentException("The range must be non-empty and at most 366 days.", nameof(to));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumLimit);
        if (after is not null && (after.SourceId == Guid.Empty || !Enum.IsDefined(after.Metric)))
            throw new ArgumentException("The cursor is invalid.", nameof(after));
        From = ReportText.Instant(from);
        To = ReportText.Instant(to);
        Filter = filter ?? ReportFilter.None;
        Limit = limit;
        After = after is null ? null : after with { OccurredAt = ReportText.Instant(after.OccurredAt) };
    }

    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public ReportFilter Filter { get; }
    public int Limit { get; }
    public ReportFactCursor? After { get; }

    /// <summary>The fact ordering stores use (source identifiers compare as lowercase hex, like PostgreSQL <c>uuid</c>).</summary>
    public static int Compare(ReportFact left, ReportFact right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return Compare(left.OccurredAt, left.SourceId, left.Metric, right.OccurredAt, right.SourceId, right.Metric);
    }

    public static int Compare(DateTimeOffset leftAt, Guid leftSource, ReportMetric leftMetric, DateTimeOffset rightAt, Guid rightSource, ReportMetric rightMetric)
    {
        var order = leftAt.CompareTo(rightAt);
        if (order == 0 && leftSource != rightSource)
        {
            Span<byte> leftBytes = stackalloc byte[16];
            Span<byte> rightBytes = stackalloc byte[16];
            leftSource.TryWriteBytes(leftBytes, bigEndian: true, out _);
            rightSource.TryWriteBytes(rightBytes, bigEndian: true, out _);
            order = leftBytes.SequenceCompareTo(rightBytes);
        }
        return order != 0 ? order : ((int)leftMetric).CompareTo((int)rightMetric);
    }
}

public sealed record ReportFactPage(IReadOnlyList<ReportFact> Items, ReportFactCursor? Next);
