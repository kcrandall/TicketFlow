using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Reporting;

/// <summary>A report range [<see cref="From"/>, <see cref="To"/>) of at most 366 days with optional dimension restrictions.</summary>
public sealed record ReportQuery
{
    public static readonly TimeSpan MaximumRange = TimeSpan.FromDays(366);

    public ReportQuery(DateTimeOffset from, DateTimeOffset to, ReportFilter? filter = null)
    {
        if (to <= from || to - from > MaximumRange)
            throw new ArgumentException("The range must be non-empty and at most 366 days.", nameof(to));
        From = ReportText.Instant(from);
        To = ReportText.Instant(to);
        Filter = filter ?? ReportFilter.None;
    }

    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public ReportFilter Filter { get; }
}

public enum ReportLeaderboardSort
{
    /// <summary>Most resolutions first.</summary>
    Resolved = 1,

    /// <summary>Most outgoing messages first.</summary>
    MessagesSent = 2,

    /// <summary>Most assignments first.</summary>
    Assignments = 3,

    /// <summary>Lowest median first response first (agents without responses last).</summary>
    FirstResponseMedian = 4,

    /// <summary>Lowest median response time first (agents without responses last).</summary>
    ResponseMedian = 5,
}

/// <summary>An agent leaderboard over <see cref="Query"/>, sorted by <see cref="Sort"/> then agent identifier, at most <see cref="Limit"/> (1-100) rows.</summary>
public sealed record ReportLeaderboardQuery
{
    public const int MaximumLimit = 100;

    public ReportLeaderboardQuery(ReportQuery query, ReportLeaderboardSort sort = ReportLeaderboardSort.Resolved, int limit = 25)
    {
        Query = query ?? throw new ArgumentNullException(nameof(query));
        if (!Enum.IsDefined(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumLimit);
        Sort = sort;
        Limit = limit;
    }

    public ReportQuery Query { get; }
    public ReportLeaderboardSort Sort { get; }
    public int Limit { get; }
}

/// <summary>
/// Time series of 1-16 <see cref="Metrics"/> bucketed by <see cref="Granularity"/> in the workspace time zone and optionally grouped.
/// Hourly series span at most 40 days.
/// </summary>
public sealed record ReportTrendQuery
{
    public static readonly TimeSpan MaximumHourlyRange = TimeSpan.FromDays(40);

    public ReportTrendQuery(ReportQuery query, IEnumerable<ReportMetric> metrics, ReportGranularity granularity, ReportDimension groupBy = ReportDimension.None)
    {
        Query = query ?? throw new ArgumentNullException(nameof(query));
        Metrics = ReportMetrics.Set(metrics, nameof(metrics));
        if (!Enum.IsDefined(granularity))
            throw new ArgumentOutOfRangeException(nameof(granularity));
        if (granularity == ReportGranularity.Hour && query.To - query.From > MaximumHourlyRange)
            throw new ArgumentException("Hourly trends span at most 40 days.", nameof(granularity));
        Granularity = granularity;
        GroupBy = ReportGroups.Require(groupBy, nameof(groupBy));
    }

    public ReportQuery Query { get; }
    public IReadOnlyList<ReportMetric> Metrics { get; }
    public ReportGranularity Granularity { get; }
    public ReportDimension GroupBy { get; }
}

/// <summary>A streamed CSV export of facts in the range (at most <see cref="MaximumRows"/> rows, 1-1 000 000).</summary>
public sealed record ReportExportQuery
{
    public const int Ceiling = 1_000_000;

    public ReportExportQuery(ReportQuery query, int maximumRows = 100_000)
    {
        Query = query ?? throw new ArgumentNullException(nameof(query));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRows, Ceiling);
        MaximumRows = maximumRows;
    }

    public ReportQuery Query { get; }
    public int MaximumRows { get; }
}

/// <summary>Count, exact mean and nearest-rank median / 90th percentile estimates (<see cref="ReportHistogram"/>) of a duration metric.</summary>
public sealed record ReportDurationSummary(long Count, TimeSpan? Average, TimeSpan? Median, TimeSpan? P90)
{
    public static ReportDurationSummary Empty { get; } = new(0, null, null, null);
}

/// <summary>
/// Satisfaction counts from survey responses. <see cref="CsatPercent"/> = satisfied (4-5) ÷ CSAT responses × 100;
/// <see cref="NpsScore"/> = (promoters − detractors) ÷ NPS responses × 100.
/// </summary>
public sealed record ReportSatisfaction(long CsatResponses, long CsatSatisfied, long NpsResponses, long Promoters, long Detractors)
{
    public static ReportSatisfaction Empty { get; } = new(0, 0, 0, 0, 0);

    public double? CsatPercent => CsatResponses > 0 ? CsatSatisfied * 100.0 / CsatResponses : null;

    public double? NpsScore => NpsResponses > 0 ? (Promoters - Detractors) * 100.0 / NpsResponses : null;

    public ReportSatisfaction Add(ReportSatisfaction other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(CsatResponses + other.CsatResponses, CsatSatisfied + other.CsatSatisfied, NpsResponses + other.NpsResponses, Promoters + other.Promoters,
            Detractors + other.Detractors);
    }
}

/// <summary>
/// The support overview over the effective (cell-aligned) range. <see cref="OpenBacklog"/> is the current number of open
/// conversations matching the filter (not range-bound). <see cref="Satisfaction"/> is <see langword="null"/> when no satisfaction
/// source is registered or the filter cannot be applied to survey responses (channel, priority, label or more than one of inbox,
/// team and agent).
/// </summary>
public sealed record ReportOverview(
    DateTimeOffset From,
    DateTimeOffset To,
    long Created,
    long Resolved,
    long Reopened,
    long MessagesReceived,
    long MessagesSent,
    long Assignments,
    long Handoffs,
    long TeamTransfers,
    long OpenBacklog,
    ReportDurationSummary FirstResponse,
    ReportDurationSummary FirstResponseBusiness,
    ReportDurationSummary Response,
    ReportDurationSummary ResponseBusiness,
    ReportDurationSummary Resolution,
    ReportDurationSummary ResolutionBusiness,
    long SlaAchieved,
    long SlaBreached,
    ReportSatisfaction? Satisfaction,
    bool Truncated)
{
    /// <summary>Achieved ÷ (achieved + breached) × 100; <see langword="null"/> without SLA outcomes.</summary>
    public double? SlaCompliancePercent => SlaAchieved + SlaBreached > 0 ? SlaAchieved * 100.0 / (SlaAchieved + SlaBreached) : null;
}

public sealed record ReportLeaderboardRow(
    UserId Agent,
    long Resolved,
    long MessagesSent,
    long Assignments,
    long Handoffs,
    ReportDurationSummary FirstResponse,
    ReportDurationSummary Response,
    ReportDurationSummary Resolution,
    ReportSatisfaction? Satisfaction);

/// <summary>Leaderboard rows; <see cref="Truncated"/> when the underlying aggregate exceeded its row bound.</summary>
public sealed record ReportLeaderboard(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<ReportLeaderboardRow> Rows, bool Truncated);

/// <summary>One non-empty series point; duration metrics carry median and 90th percentile estimates.</summary>
public sealed record ReportTrendPoint(DateTimeOffset BucketStart, string? Group, ReportMetric Metric, long Count, long Sum, TimeSpan? Median, TimeSpan? P90);

/// <summary>
/// A trend: the bucket starts (the last boundary is <see cref="To"/>), the time zone used and the sparse points ordered by bucket,
/// group and metric.
/// </summary>
public sealed record ReportTrend(DateTimeOffset From, DateTimeOffset To, string TimeZoneId, ReportGranularity Granularity, IReadOnlyList<DateTimeOffset> Buckets,
    IReadOnlyList<ReportTrendPoint> Points, bool Truncated);

/// <summary>Rows written; <see cref="Truncated"/> when more facts matched than <see cref="ReportExportQuery.MaximumRows"/>.</summary>
public sealed record ReportExportSummary(long Rows, bool Truncated);

/// <summary>
/// Authorized support reports. Reads push the <c>report:view</c> plan — widened by <c>report:view-own</c> to rows attributed to
/// the calling user — into the store; a principal without applicable grants gets empty reports, not a refusal. Export additionally
/// requires a non-empty <c>report:export</c> plan (sensitive, so planning is audited).
/// </summary>
public interface IReportingService
{
    Task<TicketResult<ReportOverview>> GetOverviewAsync(PrincipalRef actor, ReportQuery query, CancellationToken cancellationToken = default);

    Task<TicketResult<ReportLeaderboard>> GetLeaderboardAsync(PrincipalRef actor, ReportLeaderboardQuery query, CancellationToken cancellationToken = default);

    Task<TicketResult<ReportTrend>> GetTrendsAsync(PrincipalRef actor, ReportTrendQuery query, CancellationToken cancellationToken = default);

    /// <summary>Streams matching facts as RFC 4180 CSV to <paramref name="output"/> (flushed per page) and returns the row count.</summary>
    Task<TicketResult<ReportExportSummary>> ExportAsync(PrincipalRef actor, ReportExportQuery query, TextWriter output, CancellationToken cancellationToken = default);
}

/// <summary>Satisfaction counts of one group (<see langword="null"/> group when ungrouped or absent).</summary>
public sealed record ReportSatisfactionRow(string? Group, ReportSatisfaction Satisfaction);

/// <summary>
/// Supplies CSAT/NPS counts for reports, filtered by the caller's planned Access filter over the survey response resource (inbox,
/// team, agent as assignee). The default implementation adapts survey aggregates when a survey store is registered.
/// </summary>
public interface IReportSatisfactionSource
{
    /// <summary>
    /// Counts of responses with <paramref name="from"/> ≤ responded at &lt; <paramref name="to"/> (at most 366 days) grouped by
    /// <see cref="ReportDimension.None"/>, <see cref="ReportDimension.Inbox"/>, <see cref="ReportDimension.Team"/> or
    /// <see cref="ReportDimension.Agent"/>.
    /// </summary>
    Task<IReadOnlyList<ReportSatisfactionRow>> GetAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, ReportDimension groupBy, AccessFilter filter,
        CancellationToken cancellationToken = default);
}
