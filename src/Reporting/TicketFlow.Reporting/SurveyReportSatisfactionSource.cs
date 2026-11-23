using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Surveys;

namespace TicketFlow.Reporting;

/// <summary>
/// <see cref="IReportSatisfactionSource"/> over survey aggregates (<see cref="ISurveyStore.AggregateAsync"/>): the caller's planned
/// filter is applied by the survey store over the response resource (inbox, team, agent as assignee; labels unknown).
/// </summary>
public sealed class SurveyReportSatisfactionSource(ISurveyStore surveys) : IReportSatisfactionSource
{
    private readonly ISurveyStore surveys = surveys ?? throw new ArgumentNullException(nameof(surveys));

    public async Task<IReadOnlyList<ReportSatisfactionRow>> GetAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, ReportDimension groupBy, AccessFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(filter);
        var dimension = groupBy switch
        {
            ReportDimension.None => SurveyDimension.None,
            ReportDimension.Inbox => SurveyDimension.Inbox,
            ReportDimension.Team => SurveyDimension.Team,
            ReportDimension.Agent => SurveyDimension.Agent,
            _ => throw new ArgumentOutOfRangeException(nameof(groupBy), "Satisfaction groups by inbox, team or agent only."),
        };
        if (filter is DenyAllFilter)
            return [];
        var csatTask = surveys.AggregateAsync(scope, new SurveyAggregateQuery(SurveyKind.Csat, from, to, SurveyTimeBucket.None, dimension), filter, cancellationToken);
        var npsTask = surveys.AggregateAsync(scope, new SurveyAggregateQuery(SurveyKind.Nps, from, to, SurveyTimeBucket.None, dimension), filter, cancellationToken);
        await Task.WhenAll(csatTask, npsTask).ConfigureAwait(false);
        var groups = new Dictionary<string, ReportSatisfaction>(StringComparer.Ordinal);
        ReportSatisfaction? ungrouped = null;
        foreach (var row in csatTask.Result.Rows)
        {
            var counts = new ReportSatisfaction(row.Responses, row.Positive, 0, 0, 0);
            if (row.Group is null)
                ungrouped = (ungrouped ?? ReportSatisfaction.Empty).Add(counts);
            else
                groups[row.Group] = groups.GetValueOrDefault(row.Group, ReportSatisfaction.Empty).Add(counts);
        }
        foreach (var row in npsTask.Result.Rows)
        {
            var counts = new ReportSatisfaction(0, 0, row.Responses, row.Positive, row.Negative);
            if (row.Group is null)
                ungrouped = (ungrouped ?? ReportSatisfaction.Empty).Add(counts);
            else
                groups[row.Group] = groups.GetValueOrDefault(row.Group, ReportSatisfaction.Empty).Add(counts);
        }
        var rows = new List<ReportSatisfactionRow>((ungrouped is not null ? 1 : 0) + groups.Count);
        if (ungrouped is not null)
            rows.Add(new(null, ungrouped));
        if (groups.Count > 0)
        {
            var keys = new string[groups.Count];
            groups.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            for (var i = 0; i < keys.Length; i++)
                rows.Add(new(keys[i], groups[keys[i]]));
        }
        return rows;
    }
}

/// <summary>The satisfaction source used when no survey store is registered: always empty.</summary>
public sealed class NoReportSatisfactionSource : IReportSatisfactionSource
{
    private NoReportSatisfactionSource()
    {
    }

    public static NoReportSatisfactionSource Instance { get; } = new();

    public Task<IReadOnlyList<ReportSatisfactionRow>> GetAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, ReportDimension groupBy, AccessFilter filter,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReportSatisfactionRow>>([]);
}
