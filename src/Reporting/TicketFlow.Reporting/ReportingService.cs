using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Reporting;

/// <summary>
/// Default <see cref="IReportingService"/>. Every report plans <c>report:view</c> (and, for user principals, <c>report:view-own</c>
/// restricted to rows attributed to the user) through <see cref="IAccessQueryPlanner"/> and pushes the combined filter into the store
/// query, so team/inbox scopes, deny grants and conditions apply per rollup row; a principal without applicable grants gets empty
/// reports. Trends bucket in the workspace time zone (<see cref="IWorkspaceSettingsStore"/>). Export requires a non-empty
/// <c>report:export</c> plan — a sensitive permission, so the plan is audited — and streams facts matching both plans.
/// </summary>
public sealed class ReportingService : IReportingService
{
    private static readonly ReportMetric[] LeaderboardMetrics =
    [
        ReportMetric.ConversationResolved, ReportMetric.MessageSent, ReportMetric.Assignment, ReportMetric.Handoff,
        ReportMetric.FirstResponseTime, ReportMetric.ResponseTime, ReportMetric.ResolutionTime,
    ];

    private readonly IReportingStore store;
    private readonly IAccessQueryPlanner planner;
    private readonly IWorkspaceSettingsStore settings;
    private readonly IReportSatisfactionSource satisfaction;
    private readonly ReportingOptions options;

    public ReportingService(IReportingStore store, IAccessQueryPlanner planner, IWorkspaceSettingsStore settings, IReportSatisfactionSource satisfaction,
        IOptions<ReportingOptions> options)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.satisfaction = satisfaction ?? throw new ArgumentNullException(nameof(satisfaction));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
    }

    public async Task<ReportResult<ReportOverview>> GetOverviewAsync(PrincipalRef actor, ReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        var scope = actor.Scope;
        var range = ReportBuckets.Range(query.From, query.To);
        var filter = await ViewFilterAsync(actor, cancellationToken).ConfigureAwait(false);
        ReportAggregateResult aggregate = new([], false);
        long backlog = 0;
        if (filter is not DenyAllFilter)
        {
            aggregate = await store.AggregateAsync(scope, new ReportAggregateQuery(range, ReportMetrics.All, query.Filter, histograms: true), filter, cancellationToken).ConfigureAwait(false);
            backlog = await store.CountBacklogAsync(scope, query.Filter, filter, cancellationToken).ConfigureAwait(false);
        }
        var totals = new Totals(aggregate.Rows);
        var satisfactionGroup = SatisfactionGroup(query.Filter);
        ReportSatisfaction? overall = null;
        if (satisfactionGroup is { } group)
        {
            var rows = await SatisfactionAsync(scope, range[0], range[1], group.Dimension, filter, cancellationToken).ConfigureAwait(false);
            overall = rows.Where(row => group.Key is null || row.Group == group.Key).Aggregate(ReportSatisfaction.Empty, (sum, row) => sum.Add(row.Satisfaction));
        }
        return new ReportSucceeded<ReportOverview>(new ReportOverview(range[0], range[1],
            totals.Count(ReportMetric.ConversationCreated), totals.Count(ReportMetric.ConversationResolved), totals.Count(ReportMetric.ConversationReopened),
            totals.Count(ReportMetric.MessageReceived), totals.Count(ReportMetric.MessageSent), totals.Count(ReportMetric.Assignment), totals.Count(ReportMetric.Handoff),
            totals.Count(ReportMetric.TeamTransfer), backlog,
            totals.Duration(ReportMetric.FirstResponseTime), totals.Duration(ReportMetric.FirstResponseBusinessTime), totals.Duration(ReportMetric.ResponseTime),
            totals.Duration(ReportMetric.ResponseBusinessTime), totals.Duration(ReportMetric.ResolutionTime), totals.Duration(ReportMetric.ResolutionBusinessTime),
            totals.Count(ReportMetric.SlaAchieved), totals.Count(ReportMetric.SlaBreached), overall, aggregate.Truncated));
    }

    public async Task<ReportResult<ReportLeaderboard>> GetLeaderboardAsync(PrincipalRef actor, ReportLeaderboardQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        var scope = actor.Scope;
        var range = ReportBuckets.Range(query.Query.From, query.Query.To);
        var filter = await ViewFilterAsync(actor, cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new ReportSucceeded<ReportLeaderboard>(new(range[0], range[1], [], false));
        var aggregate = await store.AggregateAsync(scope, new ReportAggregateQuery(range, LeaderboardMetrics, query.Query.Filter, ReportDimension.Agent, histograms: true), filter,
            cancellationToken).ConfigureAwait(false);
        var restriction = query.Query.Filter;
        Dictionary<string, ReportSatisfaction>? perAgent = null;
        if (restriction.Inbox is null && restriction.Team is null && restriction.Channel is null && restriction.Priority is null && restriction.Label is null)
            perAgent = (await SatisfactionAsync(scope, range[0], range[1], ReportDimension.Agent, filter, cancellationToken).ConfigureAwait(false))
                .Where(row => row.Group is not null).ToDictionary(row => row.Group!, row => row.Satisfaction, StringComparer.Ordinal);
        var rows = aggregate.Rows.Where(row => row.Group is not null).GroupBy(row => row.Group!, StringComparer.Ordinal).Select(agentRows =>
        {
            var totals = new Totals(agentRows);
            return new ReportLeaderboardRow(new UserId(agentRows.Key), totals.Count(ReportMetric.ConversationResolved), totals.Count(ReportMetric.MessageSent),
                totals.Count(ReportMetric.Assignment), totals.Count(ReportMetric.Handoff), totals.Duration(ReportMetric.FirstResponseTime), totals.Duration(ReportMetric.ResponseTime),
                totals.Duration(ReportMetric.ResolutionTime), perAgent is null ? null : perAgent.GetValueOrDefault(agentRows.Key, ReportSatisfaction.Empty));
        });
        var ordered = query.Sort switch
        {
            ReportLeaderboardSort.Resolved => rows.OrderByDescending(row => row.Resolved),
            ReportLeaderboardSort.MessagesSent => rows.OrderByDescending(row => row.MessagesSent),
            ReportLeaderboardSort.Assignments => rows.OrderByDescending(row => row.Assignments),
            ReportLeaderboardSort.FirstResponseMedian => rows.OrderBy(row => row.FirstResponse.Median is null).ThenBy(row => row.FirstResponse.Median),
            ReportLeaderboardSort.ResponseMedian => rows.OrderBy(row => row.Response.Median is null).ThenBy(row => row.Response.Median),
            _ => throw new InvalidOperationException("Unknown leaderboard sort."),
        };
        return new ReportSucceeded<ReportLeaderboard>(new(range[0], range[1],
            [.. ordered.ThenBy(row => row.Agent.Value, StringComparer.Ordinal).Take(query.Limit)], aggregate.Truncated));
    }

    public async Task<ReportResult<ReportTrend>> GetTrendsAsync(PrincipalRef actor, ReportTrendQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        var scope = actor.Scope;
        var (zoneId, zone) = await ZoneAsync(scope, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DateTimeOffset> boundaries;
        try
        {
            boundaries = ReportBuckets.Boundaries(zone, query.Query.From, query.Query.To, query.Granularity);
        }
        catch (ArgumentException)
        {
            return new ReportRefused(ReportRefusal.Invalid, [$"The range spans more than {ReportBuckets.MaximumBuckets} buckets."]);
        }
        var filter = await ViewFilterAsync(actor, cancellationToken).ConfigureAwait(false);
        ReportAggregateResult aggregate = new([], false);
        if (filter is not DenyAllFilter)
            aggregate = await store.AggregateAsync(scope, new ReportAggregateQuery(boundaries, query.Metrics, query.Query.Filter, query.GroupBy,
                query.Metrics.Any(ReportMetrics.IsDuration)), filter, cancellationToken).ConfigureAwait(false);
        var points = aggregate.Rows.GroupBy(row => (row.Bucket, row.Group, row.Metric)).Select(rows =>
        {
            var count = rows.Sum(row => row.Count);
            var sum = rows.Sum(row => row.Sum);
            TimeSpan? median = null, p90 = null;
            if (ReportMetrics.IsDuration(rows.Key.Metric))
            {
                var bins = rows.Select(row => new ReportHistogramBin(row.Bin, row.Count, row.Minimum, row.Maximum)).ToList();
                median = Micro(ReportHistogram.Percentile(bins, 0.5));
                p90 = Micro(ReportHistogram.Percentile(bins, 0.9));
            }
            return new ReportTrendPoint(boundaries[rows.Key.Bucket], DisplayGroup(query.GroupBy, rows.Key.Group), rows.Key.Metric, count, sum, median, p90);
        }).ToList();
        return new ReportSucceeded<ReportTrend>(new ReportTrend(boundaries[0], boundaries[^1], zoneId, query.Granularity, boundaries, points, aggregate.Truncated));
    }

    public async Task<ReportResult<ReportExportSummary>> ExportAsync(PrincipalRef actor, ReportExportQuery query, TextWriter output, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(output);
        var scope = actor.Scope;
        // report:export is sensitive: planning it records an audit event (who exported, outcome) before any row is read.
        var export = await planner.PlanAsync(new AccessQuery(actor, ReportPermissions.Export, scope), cancellationToken).ConfigureAwait(false);
        if (export is DenyAllFilter)
            return new ReportRefused(ReportRefusal.Forbidden, [$"Permission '{ReportPermissions.Export}' is required."]);
        var view = await ViewFilterAsync(actor, cancellationToken).ConfigureAwait(false);
        AccessFilter filter;
        try
        {
            filter = view is DenyAllFilter ? DenyAllFilter.Instance : new AndFilter(export, view);
        }
        catch (AccessFilterLimitExceededException)
        {
            filter = DenyAllFilter.Instance;
        }
        await output.WriteLineAsync("source_id,metric,occurred_at,conversation_id,inbox_id,team_id,agent_id,channel,priority,labels,value,unit".AsMemory(), cancellationToken)
            .ConfigureAwait(false);
        long rows = 0;
        var truncated = false;
        if (filter is not DenyAllFilter)
        {
            ReportFactCursor? after = null;
            var line = new StringBuilder();
            do
            {
                var limit = (int)Math.Min(ReportFactQuery.MaximumLimit, query.MaximumRows - rows + 1);
                var page = await store.ListFactsAsync(scope, new ReportFactQuery(query.Query.From, query.Query.To, query.Query.Filter, limit, after), filter, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var fact in page.Items)
                {
                    if (rows == query.MaximumRows)
                    {
                        truncated = true;
                        break;
                    }
                    line.Clear();
                    AppendCsv(line, fact);
                    await output.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
                    rows++;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                after = page.Next;
                if (after is not null && rows == query.MaximumRows)
                    truncated = true;
            }
            while (after is not null && !truncated);
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ReportSucceeded<ReportExportSummary>(new ReportExportSummary(rows, truncated));
    }

    /// <summary>The <c>report:view</c> plan, widened by <c>report:view-own</c> to rows attributed to the calling user.</summary>
    private async Task<AccessFilter> ViewFilterAsync(PrincipalRef actor, CancellationToken cancellationToken)
    {
        var view = await planner.PlanAsync(new AccessQuery(actor, ReportPermissions.View, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (actor.Value is not UserPrincipal user)
            return view;
        var own = await planner.PlanAsync(new AccessQuery(actor, ReportPermissions.ViewOwn, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (own is DenyAllFilter)
            return view;
        try
        {
            AccessFilter mine = new AndFilter(own, new AssigneeIsFilter(user.UserId));
            return view is DenyAllFilter ? mine : new OrFilter(view, mine);
        }
        catch (AccessFilterLimitExceededException)
        {
            return view;   // fail closed to the narrower plan
        }
    }

    private async Task<IReadOnlyList<ReportSatisfactionRow>> SatisfactionAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, ReportDimension groupBy, AccessFilter filter,
        CancellationToken cancellationToken)
    {
        if (filter is DenyAllFilter)
            return [];
        if (to - from > ReportQuery.MaximumRange)
            to = from + ReportQuery.MaximumRange;
        return await satisfaction.GetAsync(scope, from, to, groupBy, filter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How survey aggregates can honor the report filter: no restriction, or exactly one of inbox, team and agent.</summary>
    private static (ReportDimension Dimension, string? Key)? SatisfactionGroup(ReportFilter filter)
    {
        if (filter.Channel is not null || filter.Priority is not null || filter.Label is not null)
            return null;
        return (filter.Inbox, filter.Team, filter.Agent) switch
        {
            (null, null, null) => (ReportDimension.None, null),
            ({ } inbox, null, null) => (ReportDimension.Inbox, inbox.Value),
            (null, { } team, null) => (ReportDimension.Team, team.Value),
            (null, null, { } agent) => (ReportDimension.Agent, agent.Value),
            _ => null,
        };
    }

    private async Task<(string Id, TimeZoneInfo Zone)> ZoneAsync(TicketScope scope, CancellationToken cancellationToken)
    {
        var stored = await settings.GetAsync(scope, cancellationToken).ConfigureAwait(false);
        if (stored is not null && WorkspaceTimeZones.TryResolve(stored.Settings.TimeZoneId, out var zone))
            return (stored.Settings.TimeZoneId, zone);
        return WorkspaceTimeZones.TryResolve(options.DefaultTimeZoneId, out var fallback) ? (options.DefaultTimeZoneId, fallback) : ("UTC", TimeZoneInfo.Utc);
    }

    private static string? DisplayGroup(ReportDimension dimension, string? group) =>
        dimension == ReportDimension.Priority && int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && Enum.IsDefined((Conversations.ConversationPriority)value)
            ? ((Conversations.ConversationPriority)value).ToString().ToLowerInvariant()
            : group;

    private static TimeSpan? Micro(long? microseconds) => microseconds is { } value ? TimeSpan.FromTicks(value * 10) : null;

    private static void AppendCsv(StringBuilder line, ReportFact fact)
    {
        var dimensions = fact.Dimensions;
        string[] fields =
        [
            fact.SourceId.ToString("D"), ReportMetricNames.Name(fact.Metric), fact.OccurredAt.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            fact.Conversation.Value, dimensions.Inbox.Value, dimensions.Team?.Value ?? string.Empty, dimensions.Agent?.Value ?? string.Empty, dimensions.Channel ?? string.Empty,
            dimensions.Priority.ToString().ToLowerInvariant(), string.Join(';', dimensions.Labels.Select(label => label.Value)),
            fact.Value.ToString(CultureInfo.InvariantCulture), ReportMetrics.IsDuration(fact.Metric) ? "us" : "count",
        ];
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0)
                line.Append(',');
            Csv.Append(line, fields[index]);
        }
    }

    private static void RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    /// <summary>Per-metric totals and duration summaries over aggregate rows (any grouping).</summary>
    private sealed class Totals
    {
        private readonly Dictionary<ReportMetric, List<ReportAggregateRow>> rows = [];

        public Totals(IEnumerable<ReportAggregateRow> source)
        {
            foreach (var row in source)
            {
                if (!rows.TryGetValue(row.Metric, out var list))
                    rows[row.Metric] = list = [];
                list.Add(row);
            }
        }

        public long Count(ReportMetric metric) => rows.TryGetValue(metric, out var list) ? list.Sum(row => row.Count) : 0;

        public ReportDurationSummary Duration(ReportMetric metric)
        {
            if (!rows.TryGetValue(metric, out var list))
                return ReportDurationSummary.Empty;
            var count = list.Sum(row => row.Count);
            if (count <= 0)
                return ReportDurationSummary.Empty;
            var sum = list.Sum(row => row.Sum);
            var bins = list.Select(row => new ReportHistogramBin(row.Bin, row.Count, row.Minimum, row.Maximum)).ToList();
            return new ReportDurationSummary(count, TimeSpan.FromTicks(sum / count * 10), Micro(ReportHistogram.Percentile(bins, 0.5)), Micro(ReportHistogram.Percentile(bins, 0.9)));
        }
    }
}

/// <summary>Stable lowercase kebab-case metric names used in exports (for example <c>first-response-time</c>).</summary>
public static class ReportMetricNames
{
    public static string Name(ReportMetric metric)
    {
        ReportMetrics.Require(metric, nameof(metric));
        var text = metric.ToString();
        var builder = new StringBuilder(text.Length + 4);
        foreach (var character in text)
        {
            if (char.IsUpper(character) && builder.Length > 0)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}

/// <summary>RFC 4180 field encoding with spreadsheet formula neutralization.</summary>
public static class Csv
{
    public static void Append(StringBuilder line, string value)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(value);
        // A leading = + - @ (or tab/CR) would be evaluated as a formula by spreadsheet applications.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        if (value.AsSpan().IndexOfAny(",\"\r\n") < 0)
        {
            line.Append(value);
            return;
        }
        line.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}
