using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
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
    private const int ExportFlushThreshold = 32_768;

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

    public async Task<TicketResult<ReportOverview>> GetOverviewAsync(PrincipalRef actor, ReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        var scope = actor.Scope;
        var range = ReportBuckets.Range(query.From, query.To);
        var filter = await ViewFilterAsync(actor, cancellationToken).ConfigureAwait(false);
        var satisfactionGroup = SatisfactionGroup(query.Filter);
        ReportAggregateResult aggregate = new([], false);
        long backlog = 0;
        ReportSatisfaction? overall = null;
        if (filter is not DenyAllFilter)
        {
            var aggregateTask = store.AggregateAsync(scope, new ReportAggregateQuery(range, ReportMetrics.All, query.Filter, histograms: true), filter, cancellationToken);
            var backlogTask = store.CountBacklogAsync(scope, query.Filter, filter, cancellationToken);
            if (satisfactionGroup is { } group)
            {
                var satisfactionTask = SatisfactionAsync(scope, range[0], range[1], group.Dimension, filter, cancellationToken);
                await Task.WhenAll(aggregateTask, backlogTask, satisfactionTask).ConfigureAwait(false);
                aggregate = aggregateTask.Result;
                backlog = backlogTask.Result;
                var sum = ReportSatisfaction.Empty;
                var satRows = satisfactionTask.Result;
                for (var i = 0; i < satRows.Count; i++)
                {
                    var row = satRows[i];
                    if (group.Key is null || row.Group == group.Key)
                        sum = sum.Add(row.Satisfaction);
                }
                overall = sum;
            }
            else
            {
                await Task.WhenAll(aggregateTask, backlogTask).ConfigureAwait(false);
                aggregate = aggregateTask.Result;
                backlog = backlogTask.Result;
            }
        }
        else if (satisfactionGroup is not null)
            overall = ReportSatisfaction.Empty;
        var totals = new Totals(aggregate.Rows, 0, aggregate.Rows.Count);
        return new TicketValue<ReportOverview>(new ReportOverview(range[0], range[1],
            totals.Count(ReportMetric.ConversationCreated), totals.Count(ReportMetric.ConversationResolved), totals.Count(ReportMetric.ConversationReopened),
            totals.Count(ReportMetric.MessageReceived), totals.Count(ReportMetric.MessageSent), totals.Count(ReportMetric.Assignment), totals.Count(ReportMetric.Handoff),
            totals.Count(ReportMetric.TeamTransfer), backlog,
            totals.Duration(ReportMetric.FirstResponseTime), totals.Duration(ReportMetric.FirstResponseBusinessTime), totals.Duration(ReportMetric.ResponseTime),
            totals.Duration(ReportMetric.ResponseBusinessTime), totals.Duration(ReportMetric.ResolutionTime), totals.Duration(ReportMetric.ResolutionBusinessTime),
            totals.Count(ReportMetric.SlaAchieved), totals.Count(ReportMetric.SlaBreached), overall, aggregate.Truncated));
    }

    public async Task<TicketResult<ReportLeaderboard>> GetLeaderboardAsync(PrincipalRef actor, ReportLeaderboardQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        var scope = actor.Scope;
        var range = ReportBuckets.Range(query.Query.From, query.Query.To);
        var filter = await ViewFilterAsync(actor, cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new TicketValue<ReportLeaderboard>(new(range[0], range[1], [], false));
        var restriction = query.Query.Filter;
        var wantSatisfaction = restriction.Inbox is null && restriction.Team is null && restriction.Channel is null && restriction.Priority is null && restriction.Label is null;
        var aggregateTask = store.AggregateAsync(scope, new ReportAggregateQuery(range, LeaderboardMetrics, restriction, ReportDimension.Agent, histograms: true), filter,
            cancellationToken);
        Dictionary<string, ReportSatisfaction>? perAgent = null;
        ReportAggregateResult aggregate;
        if (wantSatisfaction)
        {
            var satisfactionTask = SatisfactionAsync(scope, range[0], range[1], ReportDimension.Agent, filter, cancellationToken);
            await Task.WhenAll(aggregateTask, satisfactionTask).ConfigureAwait(false);
            aggregate = aggregateTask.Result;
            var satRows = satisfactionTask.Result;
            perAgent = new Dictionary<string, ReportSatisfaction>(satRows.Count, StringComparer.Ordinal);
            for (var i = 0; i < satRows.Count; i++)
                if (satRows[i].Group is { } key)
                    perAgent[key] = satRows[i].Satisfaction;
        }
        else
            aggregate = await aggregateTask.ConfigureAwait(false);

        var sourceRows = aggregate.Rows;
        var rows = new List<ReportLeaderboardRow>();
        var idx = 0;
        while (idx < sourceRows.Count)
        {
            var agent = sourceRows[idx].Group;
            if (agent is null)
            {
                idx++;
                continue;
            }
            var start = idx;
            idx++;
            while (idx < sourceRows.Count && string.Equals(sourceRows[idx].Group, agent, StringComparison.Ordinal))
                idx++;
            var totals = new Totals(sourceRows, start, idx);
            rows.Add(new ReportLeaderboardRow(new UserId(agent), totals.Count(ReportMetric.ConversationResolved), totals.Count(ReportMetric.MessageSent),
                totals.Count(ReportMetric.Assignment), totals.Count(ReportMetric.Handoff), totals.Duration(ReportMetric.FirstResponseTime), totals.Duration(ReportMetric.ResponseTime),
                totals.Duration(ReportMetric.ResolutionTime), perAgent is null ? null : perAgent.GetValueOrDefault(agent, ReportSatisfaction.Empty)));
        }
        var ordered = query.Sort switch
        {
            ReportLeaderboardSort.Resolved => rows.OrderByDescending(row => row.Resolved),
            ReportLeaderboardSort.MessagesSent => rows.OrderByDescending(row => row.MessagesSent),
            ReportLeaderboardSort.Assignments => rows.OrderByDescending(row => row.Assignments),
            ReportLeaderboardSort.FirstResponseMedian => rows.OrderBy(row => row.FirstResponse.Median is null).ThenBy(row => row.FirstResponse.Median),
            ReportLeaderboardSort.ResponseMedian => rows.OrderBy(row => row.Response.Median is null).ThenBy(row => row.Response.Median),
            _ => throw new InvalidOperationException("Unknown leaderboard sort."),
        };
        return new TicketValue<ReportLeaderboard>(new(range[0], range[1],
            [.. ordered.ThenBy(row => row.Agent.Value, StringComparer.Ordinal).Take(query.Limit)], aggregate.Truncated));
    }

    public async Task<TicketResult<ReportTrend>> GetTrendsAsync(PrincipalRef actor, ReportTrendQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        var scope = actor.Scope;
        var zoneTask = ZoneAsync(scope, cancellationToken);
        var filterTask = ViewFilterAsync(actor, cancellationToken);
        await Task.WhenAll(zoneTask, filterTask).ConfigureAwait(false);
        var (zoneId, zone) = zoneTask.Result;
        var filter = filterTask.Result;
        IReadOnlyList<DateTimeOffset> boundaries;
        try
        {
            boundaries = ReportBuckets.Boundaries(zone, query.Query.From, query.Query.To, query.Granularity);
        }
        catch (ArgumentException)
        {
            return new TicketFailure(TicketFailureKind.Invalid, [$"The range spans more than {ReportBuckets.MaximumBuckets} buckets."]);
        }
        ReportAggregateResult aggregate = new([], false);
        if (filter is not DenyAllFilter)
        {
            var hasDuration = false;
            for (var i = 0; i < query.Metrics.Count; i++)
                if (ReportMetrics.IsDuration(query.Metrics[i]))
                {
                    hasDuration = true;
                    break;
                }
            aggregate = await store.AggregateAsync(scope, new ReportAggregateQuery(boundaries, query.Metrics, query.Query.Filter, query.GroupBy, hasDuration), filter,
                cancellationToken).ConfigureAwait(false);
        }
        var sourceRows = aggregate.Rows;
        var points = new List<ReportTrendPoint>();
        List<ReportHistogramBin>? bins = null;
        var idx = 0;
        while (idx < sourceRows.Count)
        {
            var first = sourceRows[idx];
            var bucket = first.Bucket;
            var group = first.Group;
            var metric = first.Metric;
            var isDuration = ReportMetrics.IsDuration(metric);
            long count = 0;
            long sum = 0;
            if (isDuration)
            {
                bins ??= [];
                bins.Clear();
            }
            while (idx < sourceRows.Count)
            {
                var row = sourceRows[idx];
                if (row.Bucket != bucket || row.Metric != metric || !string.Equals(row.Group, group, StringComparison.Ordinal))
                    break;
                count += row.Count;
                sum += row.Sum;
                if (isDuration)
                    bins!.Add(new ReportHistogramBin(row.Bin, row.Count, row.Minimum, row.Maximum));
                idx++;
            }
            TimeSpan? median = null, p90 = null;
            if (isDuration)
            {
                var (p50Val, p90Val) = ReportHistogram.Percentiles(bins!, 0.5, 0.9);
                median = Micro(p50Val);
                p90 = Micro(p90Val);
            }
            points.Add(new ReportTrendPoint(boundaries[bucket], DisplayGroup(query.GroupBy, group), metric, count, sum, median, p90));
        }
        return new TicketValue<ReportTrend>(new ReportTrend(boundaries[0], boundaries[^1], zoneId, query.Granularity, boundaries, points, aggregate.Truncated));
    }

    public async Task<TicketResult<ReportExportSummary>> ExportAsync(PrincipalRef actor, ReportExportQuery query, TextWriter output, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(output);
        var scope = actor.Scope;
        // report:export is sensitive: planning it records an audit event (who exported, outcome) before any row is read.
        var export = await planner.PlanAsync(new AccessQuery(actor, ReportPermissions.Export, scope), cancellationToken).ConfigureAwait(false);
        if (export is DenyAllFilter)
            return new TicketFailure(TicketFailureKind.Forbidden, [$"Permission '{ReportPermissions.Export}' is required."]);
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
        var newLine = output.NewLine;
        var buffer = new StringBuilder(ExportFlushThreshold + 512);
        buffer.Append("source_id,metric,occurred_at,conversation_id,inbox_id,team_id,agent_id,channel,priority,labels,value,unit").Append(newLine);
        long rows = 0;
        var truncated = false;
        if (filter is not DenyAllFilter)
        {
            ReportFactCursor? after = null;
            do
            {
                var limit = (int)Math.Min(ReportFactQuery.MaximumLimit, query.MaximumRows - rows + 1);
                var page = await store.ListFactsAsync(scope, new ReportFactQuery(query.Query.From, query.Query.To, query.Query.Filter, limit, after), filter, cancellationToken)
                    .ConfigureAwait(false);
                var items = page.Items;
                for (var i = 0; i < items.Count; i++)
                {
                    if (rows == query.MaximumRows)
                    {
                        truncated = true;
                        break;
                    }
                    AppendCsv(buffer, items[i]);
                    buffer.Append(newLine);
                    rows++;
                    if (buffer.Length >= ExportFlushThreshold)
                    {
                        await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                        buffer.Clear();
                    }
                }
                if (buffer.Length > 0)
                {
                    await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                    buffer.Clear();
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                after = page.Next;
                if (after is not null && rows == query.MaximumRows)
                    truncated = true;
            }
            while (after is not null && !truncated);
        }
        if (buffer.Length > 0)
        {
            await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Clear();
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new TicketValue<ReportExportSummary>(new ReportExportSummary(rows, truncated));
    }

    /// <summary>The <c>report:view</c> plan, widened by <c>report:view-own</c> to rows attributed to the calling user.</summary>
    private async Task<AccessFilter> ViewFilterAsync(PrincipalRef actor, CancellationToken cancellationToken)
    {
        if (actor is not UserPrincipal user)
            return await planner.PlanAsync(new AccessQuery(actor, ReportPermissions.View, actor.Scope), cancellationToken).ConfigureAwait(false);
        var viewTask = planner.PlanAsync(new AccessQuery(actor, ReportPermissions.View, actor.Scope), cancellationToken);
        var ownTask = planner.PlanAsync(new AccessQuery(actor, ReportPermissions.ViewOwn, actor.Scope), cancellationToken);
        await Task.WhenAll(viewTask, ownTask).ConfigureAwait(false);
        var view = viewTask.Result;
        var own = ownTask.Result;
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
            && Enum.IsDefined((ConversationPriority)value)
            ? PriorityName((ConversationPriority)value)
            : group;

    private static string PriorityName(ConversationPriority priority) => priority switch
    {
        ConversationPriority.None => "none",
        ConversationPriority.Low => "low",
        ConversationPriority.Medium => "medium",
        ConversationPriority.High => "high",
        ConversationPriority.Urgent => "urgent",
        _ => priority.ToString().ToLowerInvariant(),
    };

    private static TimeSpan? Micro(long? microseconds) => microseconds is { } value ? TimeSpan.FromTicks(value * 10) : null;

    private static void AppendCsv(StringBuilder line, ReportFact fact)
    {
        var dimensions = fact.Dimensions;
        Span<char> scratch = stackalloc char[40];
        // 1. source_id (36-char GUID "D" — always safe for CSV)
        fact.SourceId.TryFormat(scratch, out var written, "D");
        line.Append(scratch[..written]);
        // 2. metric (lowercase kebab-case — always safe)
        line.Append(',').Append(ReportMetricNames.Name(fact.Metric));
        // 3. occurred_at (UTC ISO-8601 microsecond — always safe)
        line.Append(',');
        FormatUtcInstant(scratch, fact.OccurredAt);
        line.Append(scratch[..27]);
        // 4. conversation_id
        line.Append(',');
        Csv.Append(line, fact.Conversation.Value);
        // 5. inbox_id
        line.Append(',');
        Csv.Append(line, dimensions.Inbox.Value);
        // 6. team_id
        line.Append(',');
        if (dimensions.Team is { } team)
            Csv.Append(line, team.Value);
        // 7. agent_id
        line.Append(',');
        if (dimensions.Agent is { } agent)
            Csv.Append(line, agent.Value);
        // 8. channel
        line.Append(',');
        if (dimensions.Channel is { } channel)
            Csv.Append(line, channel);
        // 9. priority (always lowercase alpha — safe)
        line.Append(',').Append(PriorityName(dimensions.Priority));
        // 10. labels (';'-joined)
        line.Append(',');
        var labels = dimensions.Labels;
        if (labels.Count == 1)
            Csv.Append(line, labels[0].Value);
        else if (labels.Count > 1)
        {
            var joined = new StringBuilder();
            for (var i = 0; i < labels.Count; i++)
            {
                if (i > 0)
                    joined.Append(';');
                joined.Append(labels[i].Value);
            }
            Csv.Append(line, joined.ToString());
        }
        // 11. value (non-negative long — always safe)
        line.Append(',');
        fact.Value.TryFormat(scratch, out written, provider: CultureInfo.InvariantCulture);
        line.Append(scratch[..written]);
        // 12. unit
        line.Append(ReportMetrics.IsDuration(fact.Metric) ? ",us" : ",count");
    }

    private static void FormatUtcInstant(Span<char> dest, DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        var year = utc.Year;
        dest[0] = (char)('0' + year / 1000);
        dest[1] = (char)('0' + year / 100 % 10);
        dest[2] = (char)('0' + year / 10 % 10);
        dest[3] = (char)('0' + year % 10);
        dest[4] = '-';
        WriteTwo(dest, 5, utc.Month);
        dest[7] = '-';
        WriteTwo(dest, 8, utc.Day);
        dest[10] = 'T';
        WriteTwo(dest, 11, utc.Hour);
        dest[13] = ':';
        WriteTwo(dest, 14, utc.Minute);
        dest[16] = ':';
        WriteTwo(dest, 17, utc.Second);
        dest[19] = '.';
        var micros = (int)(utc.Ticks % TimeSpan.TicksPerSecond / 10);
        dest[20] = (char)('0' + micros / 100000);
        dest[21] = (char)('0' + micros / 10000 % 10);
        dest[22] = (char)('0' + micros / 1000 % 10);
        dest[23] = (char)('0' + micros / 100 % 10);
        dest[24] = (char)('0' + micros / 10 % 10);
        dest[25] = (char)('0' + micros % 10);
        dest[26] = 'Z';
    }

    private static void WriteTwo(Span<char> dest, int offset, int value)
    {
        dest[offset] = (char)('0' + value / 10);
        dest[offset + 1] = (char)('0' + value % 10);
    }

    private static void RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
    }

    /// <summary>Per-metric totals and duration summaries over aggregate rows (any grouping).</summary>
    private sealed class Totals
    {
        private readonly long[] counts = new long[17];
        private readonly long[] sums = new long[17];
        private readonly List<ReportHistogramBin>?[] bins = new List<ReportHistogramBin>?[17];

        public Totals(IReadOnlyList<ReportAggregateRow> source, int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                var row = source[i];
                var m = (int)row.Metric;
                counts[m] += row.Count;
                sums[m] += row.Sum;
                if (ReportMetrics.IsDuration(row.Metric))
                    (bins[m] ??= []).Add(new ReportHistogramBin(row.Bin, row.Count, row.Minimum, row.Maximum));
            }
        }

        public long Count(ReportMetric metric) => counts[(int)metric];

        public ReportDurationSummary Duration(ReportMetric metric)
        {
            var m = (int)metric;
            var count = counts[m];
            if (count <= 0)
                return ReportDurationSummary.Empty;
            var sum = sums[m];
            var list = bins[m];
            var (p50, p90) = list is not null ? ReportHistogram.Percentiles(list, 0.5, 0.9) : (null, null);
            return new ReportDurationSummary(count, TimeSpan.FromTicks(sum / count * 10), Micro(p50), Micro(p90));
        }
    }
}

/// <summary>Stable lowercase kebab-case metric names used in exports (for example <c>first-response-time</c>).</summary>
public static class ReportMetricNames
{
    public static string Name(ReportMetric metric) => metric switch
    {
        ReportMetric.ConversationCreated => "conversation-created",
        ReportMetric.ConversationResolved => "conversation-resolved",
        ReportMetric.ConversationReopened => "conversation-reopened",
        ReportMetric.MessageReceived => "message-received",
        ReportMetric.MessageSent => "message-sent",
        ReportMetric.FirstResponseTime => "first-response-time",
        ReportMetric.FirstResponseBusinessTime => "first-response-business-time",
        ReportMetric.ResponseTime => "response-time",
        ReportMetric.ResponseBusinessTime => "response-business-time",
        ReportMetric.ResolutionTime => "resolution-time",
        ReportMetric.ResolutionBusinessTime => "resolution-business-time",
        ReportMetric.Assignment => "assignment",
        ReportMetric.Handoff => "handoff",
        ReportMetric.TeamTransfer => "team-transfer",
        ReportMetric.SlaAchieved => "sla-achieved",
        ReportMetric.SlaBreached => "sla-breached",
        _ => throw new ArgumentOutOfRangeException(nameof(metric), "Unknown report metric."),
    };
}

/// <summary>RFC 4180 field encoding with spreadsheet formula neutralization.</summary>
public static class Csv
{
    private static readonly SearchValues<char> Special = SearchValues.Create(",\"\r\n");

    public static void Append(StringBuilder line, string value)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(value);
        var span = value.AsSpan();
        // A leading = + - @ (or tab/CR) would be evaluated as a formula by spreadsheet applications.
        var neutralize = span.Length > 0 && span[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
        if (span.IndexOfAny(Special) < 0)
        {
            if (neutralize)
                line.Append('\'');
            line.Append(value);
            return;
        }
        line.Append('"');
        if (neutralize)
            line.Append('\'');
        while (true)
        {
            var quote = span.IndexOf('"');
            if (quote < 0)
            {
                line.Append(span);
                break;
            }
            line.Append(span[..quote]).Append("\"\"");
            span = span[(quote + 1)..];
        }
        line.Append('"');
    }
}
