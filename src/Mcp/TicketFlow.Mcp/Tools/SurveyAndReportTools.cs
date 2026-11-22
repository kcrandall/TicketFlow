using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Reporting;
using TicketFlow.Surveys;

using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>Survey tools: definitions, responses (comments masked per field policy) and CSAT/NPS aggregates.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Surveys)]
public sealed class SurveyTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    private ISurveyService Service => context.Service<ISurveyService>();

    [McpServerTool(Name = "surveys_list", Title = "List surveys", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<SurveyOutput>))]
    [Description("Lists survey definitions (CSAT or NPS, inbox, enabled).")]
    [TicketFlowTool(P.SurveyRead)]
    public Task<CallToolResult> List(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.ListSurveysAsync(principal, cancellationToken), list => new ToolList<SurveyOutput>(
            [.. list.Select(stored => new SurveyOutput(stored.Survey.Id.Value, stored.Survey.Name, TicketFlowToolJson.Token(stored.Survey.Kind), stored.Survey.Inbox?.Value,
                stored.Survey.Enabled, stored.Survey.DefaultLocale.Value, stored.Revision))])), cancellationToken);

    [McpServerTool(Name = "surveys_list_responses", Title = "List survey responses", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<SurveyResponseOutput>))]
    [Description("Lists survey responses in a time range (newest first) the caller may view; comments are absent when masked for the caller." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.SurveyRead)]
    public Task<CallToolResult> ListResponses(
        [Description("Range start (ISO 8601, inclusive).")] DateTimeOffset from,
        [Description("Range end (ISO 8601, exclusive; at most 366 days after from).")] DateTimeOffset to,
        [Description("Only this survey.")] string? surveyId = null,
        [Description("Page size, 1-200 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            SurveyResponseCursor? after = null;
            if (cursor is not null)
            {
                var parts = TicketFlowToolJson.DecodeCursor(cursor, 2);
                after = new SurveyResponseCursor(TicketFlowToolJson.ParseInstant(parts[0]), new SurveyInvitationId(Guid.Parse(parts[1])));
            }
            var query = new SurveyResponseQuery(from, to, surveyId is null ? null : new SurveyId(surveyId), limit, after);
            return Map(await Service.ListResponsesAsync(principal, query, cancellationToken), page => new ToolPage<SurveyResponseOutput>(
                [.. page.Items.Select(view => new SurveyResponseOutput(view.Response.Invitation.Value.ToString("D"), view.Response.Survey.Value, TicketFlowToolJson.Token(view.Response.Kind),
                    view.Response.Score, context.UntrustedOrNull(ToolViews.Contact, view.Comment), view.CommentMasked, view.CommentRevealable, view.Response.RespondedAt,
                    view.Response.Conversation.Value, view.Response.Inbox?.Value, view.Response.Team?.Value, view.Response.Agent?.Value))],
                page.Next is { } next ? TicketFlowToolJson.EncodeCursor(TicketFlowToolJson.Instant(next.RespondedAt), next.Invitation.Value.ToString("D")) : null));
        }, cancellationToken);

    [McpServerTool(Name = "surveys_aggregate", Title = "Aggregate survey scores", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(SurveyAggregateOutput))]
    [Description("CSAT or NPS aggregates over a time range, optionally bucketed by day/week/month and grouped by inbox, team or agent; only responses the caller may view are counted.")]
    [TicketFlowTool(P.ReportView)]
    public Task<CallToolResult> Aggregate(
        [Description("csat or nps.")] string kind,
        [Description("Range start (ISO 8601).")] DateTimeOffset from,
        [Description("Range end (ISO 8601).")] DateTimeOffset to,
        [Description("none (default), day, week or month.")] string? bucket = null,
        [Description("none (default), inbox, team or agent.")] string? groupBy = null,
        [Description("Only this survey.")] string? surveyId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var query = new SurveyAggregateQuery(TicketFlowToolJson.Parse<SurveyKind>(kind, nameof(kind)), from, to,
                bucket is null ? SurveyTimeBucket.None : TicketFlowToolJson.Parse<SurveyTimeBucket>(bucket, nameof(bucket)),
                groupBy is null ? SurveyDimension.None : TicketFlowToolJson.Parse<SurveyDimension>(groupBy, nameof(groupBy)), surveyId is null ? null : new SurveyId(surveyId));
            return Map(await Service.AggregateAsync(principal, query, cancellationToken), result => new SurveyAggregateOutput(
                [.. result.Rows.Select(row => new SurveyAggregateRowOutput(TicketFlowToolJson.Token(row.Kind), row.BucketStart, row.Group, row.Responses, row.Positive, row.Neutral,
                    row.Negative, row.NpsScore, row.AverageScore))], result.Truncated));
        }, cancellationToken);

    private static object Map<T>(SurveyResult<T> result, Func<T, object> success) => result switch
    {
        SurveySucceeded<T> succeeded => success(succeeded.Value),
        SurveyRefused refused => refused.Reason switch
        {
            SurveyRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            SurveyRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            SurveyRefusal.NotFound => TicketFlowToolError.NotFound(),
            SurveyRefusal.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
    };
}

/// <summary>Report tools over <see cref="IReportingService"/>: rows are filtered by the caller's Access plan inside the store.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Reports)]
public sealed class ReportTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;
    private const string RangeHelp = "Range is [from, to), at most 366 days.";

    private IReportingService Service => context.Service<IReportingService>();

    [McpServerTool(Name = "reports_overview", Title = "Support overview", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ReportOverviewOutput))]
    [Description("Support overview for a time range: created/resolved/reopened conversations, messages, assignments, open backlog, response and resolution time summaries (seconds), SLA compliance and CSAT/NPS. " + RangeHelp + " A caller without report grants gets empty numbers, not an error.")]
    [TicketFlowTool(P.ReportView, P.ReportViewOwn, AnyOf = true)]
    public Task<CallToolResult> Overview(
        [Description("Range start (ISO 8601).")] DateTimeOffset from,
        [Description("Range end (ISO 8601).")] DateTimeOffset to,
        [Description("Only this inbox.")] string? inboxId = null,
        [Description("Only this team.")] string? teamId = null,
        [Description("Only this agent.")] string? agentId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.GetOverviewAsync(principal, Query(from, to, inboxId, teamId, agentId), cancellationToken), overview =>
            new ReportOverviewOutput(overview.From, overview.To, overview.Created, overview.Resolved, overview.Reopened, overview.MessagesReceived, overview.MessagesSent,
                overview.Assignments, overview.Handoffs, overview.TeamTransfers, overview.OpenBacklog, Duration(overview.FirstResponse), Duration(overview.FirstResponseBusiness),
                Duration(overview.Response), Duration(overview.Resolution), Duration(overview.ResolutionBusiness), overview.SlaAchieved, overview.SlaBreached,
                overview.SlaCompliancePercent, Satisfaction(overview.Satisfaction), overview.Truncated)), cancellationToken);

    [McpServerTool(Name = "reports_leaderboard", Title = "Agent leaderboard", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(LeaderboardOutput))]
    [Description("Agent leaderboard for a time range, sorted by resolved (default), messagesSent, assignments, firstResponseMedian or responseMedian. " + RangeHelp)]
    [TicketFlowTool(P.ReportView, P.ReportViewOwn, AnyOf = true)]
    public Task<CallToolResult> Leaderboard(
        [Description("Range start (ISO 8601).")] DateTimeOffset from,
        [Description("Range end (ISO 8601).")] DateTimeOffset to,
        [Description("resolved, messagesSent, assignments, firstResponseMedian or responseMedian.")] string? sort = null,
        [Description("Rows, 1-100 (default 25).")] int limit = 25,
        [Description("Only this inbox.")] string? inboxId = null,
        [Description("Only this team.")] string? teamId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var query = new ReportLeaderboardQuery(Query(from, to, inboxId, teamId, null),
                sort is null ? ReportLeaderboardSort.Resolved : TicketFlowToolJson.Parse<ReportLeaderboardSort>(sort, nameof(sort)), limit);
            return Map(await Service.GetLeaderboardAsync(principal, query, cancellationToken), board => new LeaderboardOutput(board.From, board.To,
                [.. board.Rows.Select(row => new LeaderboardRowOutput(row.Agent.Value, row.Resolved, row.MessagesSent, row.Assignments, row.Handoffs, Duration(row.FirstResponse),
                    Duration(row.Response), Duration(row.Resolution), Satisfaction(row.Satisfaction)))], board.Truncated));
        }, cancellationToken);

    [McpServerTool(Name = "reports_trends", Title = "Report trends", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(TrendOutput))]
    [Description("Time series of 1-16 metrics (for example conversationCreated, conversationResolved, messageReceived, firstResponseTime) bucketed by hour, day, week or month in the workspace time zone, optionally grouped by inbox, team, agent, channel, priority or label. Hourly series span at most 40 days. " + RangeHelp)]
    [TicketFlowTool(P.ReportView, P.ReportViewOwn, AnyOf = true)]
    public Task<CallToolResult> Trends(
        [Description("Range start (ISO 8601).")] DateTimeOffset from,
        [Description("Range end (ISO 8601).")] DateTimeOffset to,
        [Description("Metric names.")] string[] metrics,
        [Description("hour, day (default), week or month.")] string? granularity = null,
        [Description("none (default), inbox, team, agent, channel, priority or label.")] string? groupBy = null,
        [Description("Only this inbox.")] string? inboxId = null,
        [Description("Only this team.")] string? teamId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            ArgumentNullException.ThrowIfNull(metrics, nameof(metrics));
            var query = new ReportTrendQuery(Query(from, to, inboxId, teamId, null), metrics.Select(metric => TicketFlowToolJson.Parse<ReportMetric>(metric, nameof(metrics))),
                granularity is null ? ReportGranularity.Day : TicketFlowToolJson.Parse<ReportGranularity>(granularity, nameof(granularity)),
                groupBy is null ? ReportDimension.None : TicketFlowToolJson.Parse<ReportDimension>(groupBy, nameof(groupBy)));
            return Map(await Service.GetTrendsAsync(principal, query, cancellationToken), trend => new TrendOutput(trend.From, trend.To, trend.TimeZoneId,
                TicketFlowToolJson.Token(trend.Granularity), trend.Buckets,
                [.. trend.Points.Select(point => new TrendPointOutput(point.BucketStart, point.Group, TicketFlowToolJson.Token(point.Metric), point.Count, point.Sum,
                    Seconds(point.Median), Seconds(point.P90)))], trend.Truncated));
        }, cancellationToken);

    private static ReportQuery Query(DateTimeOffset from, DateTimeOffset to, string? inboxId, string? teamId, string? agentId) =>
        new(from, to, new ReportFilter(inboxId is null ? null : new InboxId(inboxId), teamId is null ? null : new TeamId(teamId), agentId is null ? null : new UserId(agentId)));

    private static long? Seconds(TimeSpan? value) => value is { } some ? (long)Math.Round(some.TotalSeconds) : null;

    private static DurationOutput Duration(ReportDurationSummary summary) => new(summary.Count, Seconds(summary.Average), Seconds(summary.Median), Seconds(summary.P90));

    private static SatisfactionOutput? Satisfaction(ReportSatisfaction? satisfaction) => satisfaction is null ? null
        : new(satisfaction.CsatResponses, satisfaction.CsatPercent, satisfaction.NpsResponses, satisfaction.NpsScore);

    private static object Map<T>(ReportResult<T> result, Func<T, object> success) => result switch
    {
        ReportSucceeded<T> succeeded => success(succeeded.Value),
        ReportRefused refused => refused.Reason switch
        {
            ReportRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            _ => TicketFlowToolError.Invalid(refused.Errors),
        },
    };
}
